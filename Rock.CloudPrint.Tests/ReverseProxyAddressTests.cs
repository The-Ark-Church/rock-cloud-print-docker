using System.Net;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

using Rock.CloudPrint.Service;

using Xunit;

namespace Rock.CloudPrint.Tests;

/// <summary>
/// Which client a request is counted as when the page may be reached through
/// a reverse proxy.
///
/// <para>
/// The failure worth guarding against is believing a header the client wrote.
/// Every case that is not "the configured proxy, telling us who it forwarded
/// for" must leave the connection's own address alone.
/// </para>
/// </summary>
public class ReverseProxyAddressTests
{
    private const string Proxy = "198.51.100.7";
    private const string Client = "203.0.113.25";

    private static IPAddress? Resolve( string? remote, StringValues forwardedFor, bool trust = true, string? proxies = Proxy ) =>
        ReverseProxyAddress.ForwardedClient(
            remote is null ? null : IPAddress.Parse( remote ),
            forwardedFor,
            trust,
            proxies );

    [Fact]
    public void Off_LeavesTheAddressAloneEvenFromTheProxy()
    {
        Assert.Null( Resolve( Proxy, Client, trust: false ) );
    }

    [Fact]
    public void On_FromTheTrustedProxy_UsesTheForwardedClient()
    {
        Assert.Equal( IPAddress.Parse( Client ), Resolve( Proxy, Client ) );
    }

    [Fact]
    public void On_FromAnywhereElse_IgnoresTheHeader()
    {
        // Somebody reaching the container directly and writing the header
        // themselves would otherwise pick a fresh rate limit bucket at will.
        Assert.Null( Resolve( "192.0.2.50", Client ) );
    }

    [Fact]
    public void SeveralEntries_OnlyTheRightmostIsBelieved()
    {
        // The client wrote the first two; the proxy appended the last.
        Assert.Equal(
            IPAddress.Parse( Client ),
            Resolve( Proxy, "192.0.2.1, 192.0.2.2, " + Client ) );
    }

    [Fact]
    public void SeveralCopiesOfTheHeader_TheEndOfTheLastOneIsBelieved()
    {
        Assert.Equal(
            IPAddress.Parse( Client ),
            Resolve( Proxy, new StringValues( new[] { "192.0.2.1", "192.0.2.2, " + Client } ) ) );
    }

    [Theory]
    [InlineData( "not-an-address" )]
    [InlineData( "unknown" )]
    [InlineData( "" )]
    [InlineData( "1234" )]
    [InlineData( "203.0.113.25, " )]
    [InlineData( "203.0.113.25, garbage" )]
    public void AnInvalidLastEntry_LeavesTheAddressAlone( string header )
    {
        // Never falls back to an entry further left: those are the client's.
        Assert.Null( Resolve( Proxy, header ) );
    }

    [Fact]
    public void NoHeader_LeavesTheAddressAlone()
    {
        Assert.Null( Resolve( Proxy, StringValues.Empty ) );
    }

    [Fact]
    public void AnIPv4MappedConnectionFromTheProxyStillMatches()
    {
        Assert.Equal( IPAddress.Parse( Client ), Resolve( "::ffff:" + Proxy, Client ) );
    }

    [Fact]
    public void AnyOfSeveralListedProxiesIsTrusted()
    {
        Assert.Equal(
            IPAddress.Parse( Client ),
            Resolve( "2001:db8::7", Client, proxies: Proxy + ", 2001:db8::7" ) );
    }

    [Fact]
    public void AnUnparseableProxyListTrustsNobody()
    {
        Assert.Null( Resolve( Proxy, Client, proxies: Proxy + ", nonsense" ) );
        Assert.Null( Resolve( Proxy, Client, proxies: "" ) );
    }

    [Theory]
    [InlineData( "203.0.113.25:51234", "203.0.113.25" )]
    [InlineData( "[2001:db8::25]:51234", "2001:db8::25" )]
    [InlineData( "[2001:db8::25]", "2001:db8::25" )]
    [InlineData( "2001:db8::25", "2001:db8::25" )]
    public void AForwardedPortIsDropped( string header, string expected )
    {
        Assert.Equal( IPAddress.Parse( expected ), Resolve( Proxy, header ) );
    }

    [Theory]
    [InlineData( "198.51.100.7" )]
    [InlineData( "2001:db8::7" )]
    [InlineData( " 198.51.100.7 " )]
    public void TryParseAddress_AcceptsRealAddresses( string text )
    {
        Assert.True( ReverseProxyAddress.TryParseAddress( text, out _ ) );
    }

    [Theory]
    [InlineData( "1234" )]
    [InlineData( "198.51.100" )]
    [InlineData( "198.51.100.07" )]
    [InlineData( "proxy.example.com" )]
    [InlineData( "198.51.100.7:80" )]
    [InlineData( "" )]
    public void TryParseAddress_RefusesShorthandAndNonsense( string text )
    {
        // IPAddress.TryParse would turn the first three into some other
        // address entirely, and trust that instead of what was meant.
        Assert.False( ReverseProxyAddress.TryParseAddress( text, out _ ) );
    }

    [Fact]
    public void TryParseList_NamesTheEntryThatIsNotAnAddress()
    {
        Assert.False( ReverseProxyAddress.TryParseList( "198.51.100.7, gateway", out _, out var invalid ) );
        Assert.Equal( "gateway", invalid );
    }

    [Fact]
    public void TryParseList_AcceptsSeveralAndIgnoresStrayCommas()
    {
        Assert.True( ReverseProxyAddress.TryParseList( "198.51.100.7, 2001:db8::7,", out var addresses, out _ ) );
        Assert.Equal( 2, addresses.Count );
    }

    [Fact]
    public async Task Middleware_RewritesTheAddressForTheRestOfThePipeline()
    {
        var options = new Settings();
        options.CurrentValue.TrustReverseProxy = true;
        options.CurrentValue.TrustedProxy = Proxy;

        IPAddress? seen = null;
        var middleware = new TrustedProxyMiddleware( ctx =>
        {
            seen = ctx.Connection.RemoteIpAddress;
            return Task.CompletedTask;
        }, options );

        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse( Proxy );
        context.Request.Headers["X-Forwarded-For"] = Client;

        await middleware.InvokeAsync( context );

        Assert.Equal( IPAddress.Parse( Client ), seen );
    }

    [Fact]
    public async Task Middleware_ReadsTheSettingsOnEveryRequest()
    {
        // Saved from the web UI, the change must apply without a restart.
        var options = new Settings();

        IPAddress? seen = null;
        var middleware = new TrustedProxyMiddleware( ctx =>
        {
            seen = ctx.Connection.RemoteIpAddress;
            return Task.CompletedTask;
        }, options );

        HttpContext Request()
        {
            var context = new DefaultHttpContext();
            context.Connection.RemoteIpAddress = IPAddress.Parse( Proxy );
            context.Request.Headers["X-Forwarded-For"] = Client;
            return context;
        }

        await middleware.InvokeAsync( Request() );
        Assert.Equal( IPAddress.Parse( Proxy ), seen );

        options.CurrentValue.TrustReverseProxy = true;
        options.CurrentValue.TrustedProxy = Proxy;

        await middleware.InvokeAsync( Request() );
        Assert.Equal( IPAddress.Parse( Client ), seen );
    }

    /// <summary>Options that can be changed mid-test.</summary>
    private sealed class Settings : IOptionsMonitor<CloudPrintOptions>
    {
        public CloudPrintOptions CurrentValue { get; } = new();

        public CloudPrintOptions Get( string? name ) => CurrentValue;

        public IDisposable? OnChange( Action<CloudPrintOptions, string?> listener ) => null;
    }
}
