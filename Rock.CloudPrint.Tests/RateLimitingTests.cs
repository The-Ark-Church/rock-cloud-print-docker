using System.Net;
using System.Threading.RateLimiting;

using Rock.CloudPrint.Service;

using Xunit;

namespace Rock.CloudPrint.Tests;

/// <summary>
/// Which bucket a client is counted in, what a refused client is told, and
/// that the login limit stops one client without stopping everybody.
/// </summary>
public class RateLimitingTests
{
    [Fact]
    public void ForAddress_CountsAnIPv4ClientOnceHoweverItConnected()
    {
        // The same machine, once over an IPv4 socket and once over a
        // dual-stack one. Two keys would give it twice the allowance.
        var plain = IPAddress.Parse( "192.0.2.20" );
        var mapped = IPAddress.Parse( "::ffff:192.0.2.20" );

        Assert.Equal( "192.0.2.20", RateLimitKeys.ForAddress( plain ) );
        Assert.Equal( "192.0.2.20", RateLimitKeys.ForAddress( mapped ) );
    }

    [Fact]
    public void ForAddress_KeepsARealIPv6AddressAsItIs()
    {
        Assert.Equal( "2001:db8::20", RateLimitKeys.ForAddress( IPAddress.Parse( "2001:db8::20" ) ) );
    }

    [Fact]
    public void ForAddress_KeepsDifferentClientsApart()
    {
        Assert.NotEqual(
            RateLimitKeys.ForAddress( IPAddress.Parse( "192.0.2.20" ) ),
            RateLimitKeys.ForAddress( IPAddress.Parse( "192.0.2.21" ) ) );
    }

    [Fact]
    public void ForAddress_PutsEveryoneWithoutAnAddressInOneSharedBucket()
    {
        Assert.Equal( RateLimitKeys.Unknown, RateLimitKeys.ForAddress( null ) );
    }

    [Fact]
    public void Message_SaysAMinuteForTheLoginWindow()
    {
        Assert.Equal( "Too many attempts - wait a minute and try again.", RateLimitRejection.Message( TimeSpan.FromMinutes( 1 ) ) );
    }

    [Fact]
    public void Message_SaysAMomentWhenTheWaitIsShortOrUnknown()
    {
        Assert.Equal( "Too many attempts - wait a moment and try again.", RateLimitRejection.Message( TimeSpan.FromSeconds( 1 ) ) );
        Assert.Equal( "Too many attempts - wait a moment and try again.", RateLimitRejection.Message( null ) );
    }

    [Fact]
    public void Message_CountsSecondsInBetween()
    {
        Assert.Equal( "Too many attempts - wait 30 seconds and try again.", RateLimitRejection.Message( TimeSpan.FromSeconds( 30 ) ) );
    }

    [Fact]
    public void RetryAfterHeader_RoundsUpAndIsNeverZero()
    {
        // A client that waits exactly what it is told must not be refused
        // again, and "0" would invite an immediate retry.
        Assert.Equal( "2", RateLimitRejection.RetryAfterHeader( TimeSpan.FromMilliseconds( 1500 ) ) );
        Assert.Equal( "1", RateLimitRejection.RetryAfterHeader( TimeSpan.Zero ) );
        Assert.Equal( "60", RateLimitRejection.RetryAfterHeader( TimeSpan.FromMinutes( 1 ) ) );
    }

    [Fact]
    public void ClientThenShared_OneClientUsingUpItsOwnLimitLeavesTheSharedOneAlone()
    {
        using var shared = Window( 3 );
        using var noisy = new ClientThenSharedLimiter( Window( 2 ), shared );
        using var other = new ClientThenSharedLimiter( Window( 2 ), shared );

        Assert.True( noisy.AttemptAcquire().IsAcquired );
        Assert.True( noisy.AttemptAcquire().IsAcquired );

        // However hard the noisy client keeps trying, it is refused by its own
        // limit before the shared one is asked, so the shared one still has a
        // permit for somebody else.
        for ( var i = 0; i < 10; i++ )
            Assert.False( noisy.AttemptAcquire().IsAcquired );

        Assert.True( other.AttemptAcquire().IsAcquired );
    }

    [Fact]
    public void ClientThenShared_ManyClientsTogetherAreHeldToTheSharedLimit()
    {
        using var shared = Window( 3 );
        var clients = Enumerable.Range( 0, 5 )
            .Select( _ => new ClientThenSharedLimiter( Window( 5 ), shared ) )
            .ToList();

        var admitted = clients.Count( c => c.AttemptAcquire().IsAcquired );

        Assert.Equal( 3, admitted );

        foreach ( var client in clients )
            client.Dispose();
    }

    [Fact]
    public void ClientThenShared_ARefusalCarriesARetryTime()
    {
        using var shared = Window( 1 );
        using var first = new ClientThenSharedLimiter( Window( 5 ), shared );
        using var second = new ClientThenSharedLimiter( Window( 5 ), shared );

        Assert.True( first.AttemptAcquire().IsAcquired );

        using var refused = second.AttemptAcquire();

        Assert.False( refused.IsAcquired );
        Assert.True( refused.TryGetMetadata( MetadataName.RetryAfter, out _ ) );
    }

    [Fact]
    public void ClientThenShared_DisposingAClientLeavesTheSharedLimiterWorking()
    {
        // A partitioned limiter disposes a client's limiter once it goes idle.
        // The shared one belongs to every client and must survive that.
        using var shared = Window( 5 );

        new ClientThenSharedLimiter( Window( 5 ), shared ).Dispose();

        Assert.True( shared.AttemptAcquire().IsAcquired );
    }

    [Fact]
    public async Task ClientThenShared_ARequestTheSharedLimitRefusesCostsTheClientOnePermit()
    {
        // The rate limiting middleware tries AttemptAcquire and, when that is
        // refused, AcquireAsync for the same request. The client's permit is
        // taken on the first call; the second must not take another.
        using var shared = Window( 1 );
        using var other = new ClientThenSharedLimiter( Window( 5 ), shared );
        using var client = new ClientThenSharedLimiter( Window( 5 ), shared );

        // Somebody else uses up the shared ceiling.
        Assert.True( other.AttemptAcquire().IsAcquired );

        var before = client.GetStatistics()!.CurrentAvailablePermits;

        using var attempted = client.AttemptAcquire();
        using var waited = await client.AcquireAsync();

        Assert.False( attempted.IsAcquired );
        Assert.False( waited.IsAcquired );
        Assert.True( waited.TryGetMetadata( MetadataName.RetryAfter, out _ ) );
        Assert.Equal( before - 1, client.GetStatistics()!.CurrentAvailablePermits );
    }

    [Fact]
    public async Task ClientThenShared_TheSecondLookAtAClientRefusalTakesNothingFromTheSharedLimit()
    {
        // A client over its own limit is refused on both calls without the
        // shared limiter being charged on either.
        using var shared = Window( 3 );
        using var client = new ClientThenSharedLimiter( Window( 1 ), shared );

        Assert.True( client.AttemptAcquire().IsAcquired );

        using var attempted = client.AttemptAcquire();
        using var waited = await client.AcquireAsync();

        Assert.False( attempted.IsAcquired );
        Assert.False( waited.IsAcquired );
        Assert.Equal( 2, shared.GetStatistics()!.CurrentAvailablePermits );
    }

    [Fact]
    public async Task ClientThenShared_AcquireAsyncStillAdmitsWhenBothHaveRoom()
    {
        // Used on its own, the asynchronous path still counts against both.
        using var shared = Window( 3 );
        using var client = new ClientThenSharedLimiter( Window( 2 ), shared );

        using var lease = await client.AcquireAsync();

        Assert.True( lease.IsAcquired );
        Assert.Equal( 1, client.GetStatistics()!.CurrentAvailablePermits );
        Assert.Equal( 2, shared.GetStatistics()!.CurrentAvailablePermits );
    }

    // A window long enough that nothing replenishes while a test runs.
    private static FixedWindowRateLimiter Window( int permits ) => new( new FixedWindowRateLimiterOptions
    {
        PermitLimit = permits,
        Window = TimeSpan.FromHours( 1 ),
        QueueLimit = 0,
        AutoReplenishment = true
    } );
}
