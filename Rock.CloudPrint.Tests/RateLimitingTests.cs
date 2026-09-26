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
        var plain = IPAddress.Parse( "192.168.1.20" );
        var mapped = IPAddress.Parse( "::ffff:192.168.1.20" );

        Assert.Equal( "192.168.1.20", RateLimitKeys.ForAddress( plain ) );
        Assert.Equal( "192.168.1.20", RateLimitKeys.ForAddress( mapped ) );
    }

    [Fact]
    public void ForAddress_KeepsARealIPv6AddressAsItIs()
    {
        Assert.Equal( "fd00::20", RateLimitKeys.ForAddress( IPAddress.Parse( "fd00::20" ) ) );
    }

    [Fact]
    public void ForAddress_KeepsDifferentClientsApart()
    {
        Assert.NotEqual(
            RateLimitKeys.ForAddress( IPAddress.Parse( "192.168.1.20" ) ),
            RateLimitKeys.ForAddress( IPAddress.Parse( "192.168.1.21" ) ) );
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

    // A window long enough that nothing replenishes while a test runs.
    private static FixedWindowRateLimiter Window( int permits ) => new( new FixedWindowRateLimiterOptions
    {
        PermitLimit = permits,
        Window = TimeSpan.FromHours( 1 ),
        QueueLimit = 0,
        AutoReplenishment = true
    } );
}
