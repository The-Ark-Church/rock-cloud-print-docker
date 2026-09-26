using Rock.CloudPrint.Service;

using Xunit;

namespace Rock.CloudPrint.Tests;

/// <summary>
/// What the container reports to Docker: healthy while connected, while there
/// is nothing to connect to yet, and while a lost connection is still within
/// the time the reconnect backoff needs - and only then unhealthy.
/// </summary>
public class ProxyHealthTests
{
    private static readonly DateTimeOffset Now = new( 2026, 9, 27, 9, 0, 0, TimeSpan.Zero );

    [Fact]
    public void Evaluate_IsHealthyWhileConnected()
    {
        var health = ProxyHealth.Evaluate( true, true, null, Now );

        Assert.True( health.Healthy );
        Assert.True( health.Connected );
        Assert.Equal( "connected", health.Status );
    }

    [Fact]
    public void Evaluate_IsHealthyOnAFreshInstallThatHasNothingToConnectTo()
    {
        // Started a day ago and never configured. That is somebody who has not
        // opened the settings page yet, not a proxy that is failing.
        var health = ProxyHealth.Evaluate( false, false, Now.AddDays( -1 ), Now );

        Assert.True( health.Healthy );
        Assert.False( health.Connected );
        Assert.Equal( "unconfigured", health.Status );
    }

    [Fact]
    public void Evaluate_RidesOutADisconnectShorterThanTheGracePeriod()
    {
        var health = ProxyHealth.Evaluate( true, false, Now - ProxyHealth.DisconnectedGracePeriod + TimeSpan.FromSeconds( 1 ), Now );

        Assert.True( health.Healthy );
        Assert.Equal( "reconnecting", health.Status );
    }

    [Fact]
    public void Evaluate_IsUnhealthyOnceTheGracePeriodHasPassed()
    {
        var health = ProxyHealth.Evaluate( true, false, Now - ProxyHealth.DisconnectedGracePeriod, Now );

        Assert.False( health.Healthy );
        Assert.False( health.Connected );
        Assert.Equal( "disconnected", health.Status );
    }

    [Fact]
    public void Evaluate_IsUnhealthyWhenConfiguredAndDisconnectedWithNoTimestamp()
    {
        Assert.False( ProxyHealth.Evaluate( true, false, null, Now ).Healthy );
    }

    [Fact]
    public void GracePeriod_OutlastsTheLongestReconnectDelay()
    {
        // The reconnect backoff caps at 60 seconds. A grace period shorter than
        // that would report a proxy as down between two attempts it was always
        // going to make.
        Assert.True( ProxyHealth.DisconnectedGracePeriod > TimeSpan.FromSeconds( 60 ) );
    }

    [Theory]
    [InlineData( "http://+:8080", "http://127.0.0.1:8080/healthz" )]
    [InlineData( "http://*:9000", "http://127.0.0.1:9000/healthz" )]
    [InlineData( "http://0.0.0.0:8080", "http://127.0.0.1:8080/healthz" )]
    [InlineData( "http://[::]:8080", "http://[::1]:8080/healthz" )]
    [InlineData( "http://localhost:5000", "http://localhost:5000/healthz" )]
    [InlineData( "http://192.168.1.20:8080", "http://192.168.1.20:8080/healthz" )]
    [InlineData( "http://+", "http://127.0.0.1:80/healthz" )]
    public void ResolveProbeUrl_ConnectsToWhereKestrelListens( string urls, string expected )
    {
        Assert.Equal( new Uri( expected ), ProxyHealth.ResolveProbeUrl( urls ) );
    }

    [Fact]
    public void ResolveProbeUrl_PrefersPlainHttpWhenThereAreSeveralBindings()
    {
        Assert.Equal(
            new Uri( "http://127.0.0.1:8080/healthz" ),
            ProxyHealth.ResolveProbeUrl( "https://+:8443; http://+:8080" ) );
    }

    [Fact]
    public void ResolveProbeUrl_UsesHttpsWhenThatIsAllThereIs()
    {
        Assert.Equal(
            new Uri( "https://127.0.0.1:8443/healthz" ),
            ProxyHealth.ResolveProbeUrl( "https://+:8443" ) );
    }

    [Theory]
    [InlineData( null )]
    [InlineData( "" )]
    [InlineData( "not a url" )]
    public void ResolveProbeUrl_FallsBackToTheDefaultPort( string? urls )
    {
        Assert.Equal( new Uri( "http://127.0.0.1:8080/healthz" ), ProxyHealth.ResolveProbeUrl( urls ) );
    }
}

/// <summary>
/// The timestamp the grace period is measured from. It has to survive being
/// told about a disconnect more than once, or a proxy that keeps failing would
/// keep resetting its own clock.
/// </summary>
public class ProxyStatusTests
{
    [Fact]
    public void DisconnectedDateTime_StartsAtStartupBeforeAnyConnection()
    {
        var status = new ProxyStatus();

        Assert.False( status.IsConnected );
        Assert.Equal( status.StartedDateTime, status.DisconnectedDateTime );
    }

    [Fact]
    public void DisconnectedDateTime_IsClearedWhileConnectedAndSetWhenTheConnectionDrops()
    {
        var status = new ProxyStatus();

        status.SetConnected( true );
        Assert.Null( status.DisconnectedDateTime );
        Assert.NotNull( status.ConnectedDateTime );

        var before = DateTimeOffset.Now;
        status.SetConnected( false );

        Assert.NotNull( status.DisconnectedDateTime );
        Assert.True( status.DisconnectedDateTime >= before );
        Assert.Null( status.ConnectedDateTime );
    }

    [Fact]
    public void DisconnectedDateTime_IsNotMovedByASecondDisconnect()
    {
        var status = new ProxyStatus();
        var first = status.DisconnectedDateTime;

        status.SetConnected( false );

        Assert.Equal( first, status.DisconnectedDateTime );
    }
}
