using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

using Rock.CloudPrint.Service;

using Xunit;

namespace Rock.CloudPrint.Tests;

/// <summary>
/// The web API as a browser meets it: the real <see cref="Program"/>, hosted
/// in memory, answering real HTTP requests.
///
/// <para>
/// The unit tests next door prove each piece on its own - that a token is
/// checked properly, that a rate limit key is right, that a settings save keeps
/// other keys. None of them can tell that the pieces are wired together in
/// Program.cs: that the PIN check actually sits in front of /api, that /healthz
/// and the page itself stay open, that the security headers go on every
/// response, that the login endpoint is the one carrying the rate limit, and
/// that a settings save lands in the file the service reads. A reordered
/// middleware or a dropped RequireRateLimiting would pass every unit test and
/// still ship an open proxy. These tests pin that wiring.
/// </para>
///
/// <para>
/// Each test gets its own host and its own temporary content root, so the
/// settings file, labels and records it writes never touch the repository and
/// no test sees another's state or another's rate limit buckets. The
/// background worker that dials Rock is removed from the test host: nothing
/// here may make a network call, and these tests are about the HTTP surface,
/// not the print path.
/// </para>
/// </summary>
public class HttpApiTests
{
    private const string Pin = "2468";

    // ── Authentication ────────────────────────────────────────────────

    [Fact]
    public async Task WithAPin_ApiRequestsWithoutATokenAreRefused()
    {
        await using var host = ProxyHost.Start( new() { ["Password"] = Pin } );
        var client = host.CreateClient();

        var status = await client.GetAsync( "/api/status" );
        Assert.Equal( HttpStatusCode.Unauthorized, status.StatusCode );
        Assert.Equal( "Unauthorized", ( await ReadJson( status ) )["error"]!.GetValue<string>() );

        Assert.Equal( HttpStatusCode.Unauthorized, ( await client.GetAsync( "/api/settings" ) ).StatusCode );
        Assert.Equal( HttpStatusCode.Unauthorized, ( await client.GetAsync( "/api/logs" ) ).StatusCode );

        // A write is refused before it reaches the endpoint, so the file
        // keeps what it had - the empty Url the test host starts with.
        var save = await client.PostAsJsonAsync( "/api/settings", new { url = "https://rock.example.com", name = "Lobby", id = "abc" } );
        Assert.Equal( HttpStatusCode.Unauthorized, save.StatusCode );
        Assert.Equal( "", host.ReadSettingsFile()["Url"]!.GetValue<string>() );
    }

    [Fact]
    public async Task WithAPin_AMadeUpTokenIsRefused()
    {
        await using var host = ProxyHost.Start( new() { ["Password"] = Pin } );
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue( "Bearer", new string( 'a', 64 ) );

        Assert.Equal( HttpStatusCode.Unauthorized, ( await client.GetAsync( "/api/status" ) ).StatusCode );
    }

    [Fact]
    public async Task WithAPin_AWrongPinGetsNoToken()
    {
        await using var host = ProxyHost.Start( new() { ["Password"] = Pin } );
        var client = host.CreateClient();

        var login = await client.PostAsJsonAsync( "/api/auth/login", new { password = "1357" } );

        Assert.Equal( HttpStatusCode.Unauthorized, login.StatusCode );
        Assert.Null( ( await ReadJson( login ) )["token"] );
    }

    [Fact]
    public async Task WithAPin_ATokenFromLoginOpensTheApi()
    {
        await using var host = ProxyHost.Start( new() { ["Password"] = Pin } );
        var client = host.CreateClient();

        var login = await client.PostAsJsonAsync( "/api/auth/login", new { password = Pin } );
        Assert.Equal( HttpStatusCode.OK, login.StatusCode );

        var token = ( await ReadJson( login ) )["token"]!.GetValue<string>();
        Assert.False( string.IsNullOrEmpty( token ) );

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue( "Bearer", token );

        Assert.Equal( HttpStatusCode.OK, ( await client.GetAsync( "/api/status" ) ).StatusCode );
        Assert.Equal( HttpStatusCode.OK, ( await client.GetAsync( "/api/settings" ) ).StatusCode );

        var save = await client.PostAsJsonAsync( "/api/settings", new { url = "https://rock.example.com", name = "Lobby", id = "abc" } );
        Assert.Equal( HttpStatusCode.OK, save.StatusCode );

        // Signing out ends that token straight away.
        Assert.Equal( HttpStatusCode.OK, ( await client.PostAsync( "/api/auth/logout", null ) ).StatusCode );
        Assert.Equal( HttpStatusCode.Unauthorized, ( await client.GetAsync( "/api/status" ) ).StatusCode );
    }

    [Fact]
    public async Task WithAPin_TheHealthCheckThePageAndTheLoginConfigNeedNoToken()
    {
        await using var host = ProxyHost.Start( new() { ["Password"] = Pin } );
        var client = host.CreateClient();

        // Docker has no PIN to send.
        Assert.Equal( HttpStatusCode.OK, ( await client.GetAsync( "/healthz" ) ).StatusCode );

        // The page has to load before anybody can type the PIN into it.
        var page = await client.GetAsync( "/" );
        Assert.Equal( HttpStatusCode.OK, page.StatusCode );
        Assert.Equal( "text/html", page.Content.Headers.ContentType?.MediaType );
        Assert.Contains( "<html", await page.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase );

        // And it has to be able to ask whether a PIN is wanted.
        var config = await client.GetAsync( "/api/auth/config" );
        Assert.Equal( HttpStatusCode.OK, config.StatusCode );
        Assert.True( ( await ReadJson( config ) )["required"]!.GetValue<bool>() );
    }

    [Fact]
    public async Task WithoutAPin_TheApiIsOpen()
    {
        await using var host = ProxyHost.Start( new() );
        var client = host.CreateClient();

        var status = await client.GetAsync( "/api/status" );

        Assert.Equal( HttpStatusCode.OK, status.StatusCode );
        Assert.False( ( await ReadJson( status ) )["isConfigured"]!.GetValue<bool>() );
    }

    // ── Security headers ──────────────────────────────────────────────

    [Theory]
    [InlineData( "/" )]
    [InlineData( "/api/status" )]
    [InlineData( "/healthz" )]
    public async Task EveryResponseCarriesTheSecurityHeaders( string path )
    {
        await using var host = ProxyHost.Start( new() );
        var response = await host.CreateClient().GetAsync( path );

        Assert.Equal( HttpStatusCode.OK, response.StatusCode );
        Assert.Equal( "DENY", Header( response, "X-Frame-Options" ) );
        Assert.Equal( "nosniff", Header( response, "X-Content-Type-Options" ) );
        Assert.Equal( "no-referrer", Header( response, "Referrer-Policy" ) );
        Assert.Equal( "no-store", Header( response, "Cache-Control" ) );

        var csp = Header( response, "Content-Security-Policy" );
        Assert.NotNull( csp );
        Assert.Contains( "script-src 'self'", csp );
        Assert.Contains( "frame-ancestors 'none'", csp );
        Assert.DoesNotContain( "unsafe-inline", csp );
        Assert.DoesNotContain( "unsafe-eval", csp );

        Assert.False( response.Headers.Contains( "Server" ) );
    }

    [Fact]
    public async Task ARefusedRequestCarriesTheSecurityHeadersToo()
    {
        await using var host = ProxyHost.Start( new() { ["Password"] = Pin } );
        var response = await host.CreateClient().GetAsync( "/api/status" );

        Assert.Equal( HttpStatusCode.Unauthorized, response.StatusCode );
        Assert.Equal( "DENY", Header( response, "X-Frame-Options" ) );
        Assert.Equal( "no-store", Header( response, "Cache-Control" ) );
    }

    [Fact]
    public void KestrelIsToldNotToSendAServerHeader()
    {
        // The in-memory server never sends a Server header, so the assertion
        // in the test above cannot fail on its own. This checks the setting
        // that keeps Kestrel quiet in the real container.
        using var host = ProxyHost.Start( new() );

        var kestrel = host.Services.GetRequiredService<IOptions<KestrelServerOptions>>().Value;

        Assert.False( kestrel.AddServerHeader );
    }

    // ── Rate limiting ─────────────────────────────────────────────────

    [Fact]
    public async Task TheSixthLoginAttemptInAMinuteIsTurnedAway()
    {
        await using var host = ProxyHost.Start( new() { ["Password"] = Pin } );
        var client = host.CreateClient( clientAddress: "192.0.2.10" );

        for ( var attempt = 1; attempt <= 5; attempt++ )
        {
            var wrong = await client.PostAsJsonAsync( "/api/auth/login", new { password = "0000" } );
            Assert.Equal( HttpStatusCode.Unauthorized, wrong.StatusCode );
        }

        var sixth = await client.PostAsJsonAsync( "/api/auth/login", new { password = Pin } );

        Assert.Equal( HttpStatusCode.TooManyRequests, sixth.StatusCode );
        Assert.True( sixth.Headers.RetryAfter?.Delta > TimeSpan.Zero, "A refused login should say how long to wait." );
        Assert.Equal( "application/json", sixth.Content.Headers.ContentType?.MediaType );
        Assert.StartsWith( "Too many attempts", ( await ReadJson( sixth ) )["error"]!.GetValue<string>() );

        // Turned away even with the right PIN: the limit is on attempts,
        // not on wrong answers.
        Assert.Null( ( await ReadJson( sixth ) )["token"] );
    }

    [Fact]
    public async Task OneClientUsingUpItsLoginsDoesNotLockOutAnother()
    {
        await using var host = ProxyHost.Start( new() { ["Password"] = Pin } );
        var guesser = host.CreateClient( clientAddress: "192.0.2.10" );
        var staff = host.CreateClient( clientAddress: "192.0.2.11" );

        for ( var attempt = 1; attempt <= 6; attempt++ )
            await guesser.PostAsJsonAsync( "/api/auth/login", new { password = "0000" } );

        Assert.Equal( HttpStatusCode.TooManyRequests, ( await guesser.PostAsJsonAsync( "/api/auth/login", new { password = Pin } ) ).StatusCode );
        Assert.Equal( HttpStatusCode.OK, ( await staff.PostAsJsonAsync( "/api/auth/login", new { password = Pin } ) ).StatusCode );
    }

    [Fact]
    public async Task RequestsWithNoClientAddressShareOneLoginLimit()
    {
        // The in-memory server gives a request no remote address, which the
        // limiter counts as one "unknown" client. Pinned so that a change
        // there - which would also apply to any odd transport in production -
        // is a decision rather than an accident.
        await using var host = ProxyHost.Start( new() { ["Password"] = Pin } );
        var first = host.CreateClient();
        var second = host.CreateClient();

        for ( var attempt = 1; attempt <= 5; attempt++ )
            await first.PostAsJsonAsync( "/api/auth/login", new { password = "0000" } );

        Assert.Equal( HttpStatusCode.TooManyRequests, ( await second.PostAsJsonAsync( "/api/auth/login", new { password = Pin } ) ).StatusCode );
    }

    // ── Health check ──────────────────────────────────────────────────

    [Fact]
    public async Task TheHealthCheckSaysUnconfiguredAndNothingElse()
    {
        await using var host = ProxyHost.Start( new() { ["Name"] = "Lobby Proxy" } );

        var response = await host.CreateClient().GetAsync( "/healthz" );

        // Unconfigured is not unhealthy: a fresh container waiting to be set
        // up should not be restarted by Docker over and over.
        Assert.Equal( HttpStatusCode.OK, response.StatusCode );

        var body = await ReadJson( response );

        Assert.Equal( new[] { "connected", "status" }, body.Select( p => p.Key ).Order().ToArray() );
        Assert.Equal( "unconfigured", body["status"]!.GetValue<string>() );
        Assert.False( body["connected"]!.GetValue<bool>() );

        // Open to anyone, so it must not say anything about the installation.
        var raw = body.ToJsonString();
        Assert.DoesNotContain( "Lobby Proxy", raw );
        Assert.DoesNotContain( "version", raw, StringComparison.OrdinalIgnoreCase );
    }

    // ── Settings ──────────────────────────────────────────────────────

    [Fact]
    public async Task SavingSettingsWritesTheConfigFileAndTakesEffect()
    {
        await using var host = ProxyHost.Start( new() { ["NotificationCooldownMinutes"] = 7 } );
        var client = host.CreateClient();

        var save = await client.PostAsJsonAsync( "/api/settings", new
        {
            url = "https://rock.example.com",
            name = "Lobby Proxy",
            id = "11111111-2222-3333-4444-555555555555"
        } );

        Assert.Equal( HttpStatusCode.OK, save.StatusCode );
        Assert.True( ( await ReadJson( save ) )["success"]!.GetValue<bool>() );

        // On disk, in the temporary config directory, alongside what was
        // already there.
        var file = host.ReadSettingsFile();
        Assert.Equal( "https://rock.example.com", file["Url"]!.GetValue<string>() );
        Assert.Equal( "Lobby Proxy", file["Name"]!.GetValue<string>() );
        Assert.Equal( "11111111-2222-3333-4444-555555555555", file["Id"]!.GetValue<string>() );
        Assert.Equal( 7, file["NotificationCooldownMinutes"]!.GetValue<int>() );

        // And read straight back, without waiting for the file watcher.
        var settings = await ReadJson( await client.GetAsync( "/api/settings" ) );
        Assert.Equal( "https://rock.example.com", settings["url"]!.GetValue<string>() );
        Assert.Equal( "Lobby Proxy", settings["name"]!.GetValue<string>() );
        Assert.Equal( "11111111-2222-3333-4444-555555555555", settings["id"]!.GetValue<string>() );

        var status = await ReadJson( await client.GetAsync( "/api/status" ) );
        Assert.True( status["isConfigured"]!.GetValue<bool>() );
    }

    [Fact]
    public async Task SettingsAreNotWrittenIntoTheRepository()
    {
        // A guard on the harness itself: the host's content root is the
        // temporary directory, so the service's config/ lands there.
        await using var host = ProxyHost.Start( new() );

        var environment = host.Services.GetRequiredService<IWebHostEnvironment>();

        Assert.Equal( host.ContentRoot, environment.ContentRootPath.TrimEnd( Path.DirectorySeparatorChar ) );
        Assert.StartsWith( Path.GetTempPath().TrimEnd( Path.DirectorySeparatorChar ), environment.ContentRootPath );
    }

    // ── Helpers ───────────────────────────────────────────────────────

    private static async Task<JsonObject> ReadJson( HttpResponseMessage response ) =>
        JsonNode.Parse( await response.Content.ReadAsStringAsync() )!.AsObject();

    private static string? Header( HttpResponseMessage response, string name )
    {
        if ( response.Headers.TryGetValues( name, out var values ) )
            return string.Join( ", ", values );

        if ( response.Content.Headers.TryGetValues( name, out values ) )
            return string.Join( ", ", values );

        return null;
    }
}

/// <summary>
/// One in-memory proxy with its own temporary content root, torn down with
/// it.
/// </summary>
internal sealed class ProxyHost : IDisposable, IAsyncDisposable
{
    /// <summary>
    /// A request header the test host turns into the connection's remote
    /// address, because the in-memory server gives every request none. Read
    /// before any of the service's own middleware, so the service sees it
    /// exactly as it would see a real client's address.
    /// </summary>
    internal const string ClientAddressHeader = "X-Test-Client-Address";

    private readonly WebApplicationFactory<Program> _factory;

    public string ContentRoot { get; }

    public IServiceProvider Services => _factory.Services;

    private ProxyHost( string contentRoot, WebApplicationFactory<Program> factory )
    {
        ContentRoot = contentRoot;
        _factory = factory;
    }

    /// <summary>
    /// Starts a proxy whose config/appsettings.json holds
    /// <paramref name="settings"/>.
    /// </summary>
    public static ProxyHost Start( Dictionary<string, JsonNode?> settings )
    {
        var contentRoot = Path.Combine( Path.GetTempPath(), "cloudprint-http-" + Guid.NewGuid().ToString( "n" ) );
        Directory.CreateDirectory( Path.Combine( contentRoot, "config" ) );

        // Always written, so a Url, Id or Password in the environment of
        // whoever runs the tests cannot leak in: this file is added after the
        // environment and wins over it.
        var file = new JsonObject
        {
            ["Url"] = "",
            ["Id"] = "",
            ["Password"] = ""
        };

        foreach ( var (key, value) in settings )
            file[key] = value?.DeepClone();

        File.WriteAllText( Path.Combine( contentRoot, "config", "appsettings.json" ), file.ToJsonString() );

        var webRoot = FindServiceWebRoot();

        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder( builder =>
        {
            // Production, as the container runs.
            builder.UseEnvironment( Environments.Production );
            builder.UseContentRoot( contentRoot );
            builder.UseWebRoot( webRoot );

            builder.ConfigureTestServices( services =>
            {
                // The worker that dials Rock. With a Url saved it would try to
                // reach it, and nothing here may touch the network.
                foreach ( var worker in services.Where( d => d.ImplementationType == typeof( ProxyWorker ) ).ToList() )
                    services.Remove( worker );

                services.AddSingleton<IStartupFilter, ClientAddressStartupFilter>();
            } );
        } );

        var host = new ProxyHost( contentRoot, factory );

        // Build and start now, so a broken host fails here rather than on the
        // first request.
        _ = factory.Server;

        return host;
    }

    /// <summary>
    /// A client that sends what the web UI sends: <c>X-CloudPrint-Request</c>
    /// on anything that is not a GET, and optionally a client address.
    /// </summary>
    public HttpClient CreateClient( string? clientAddress = null ) =>
        _factory.CreateDefaultClient( new UiHeadersHandler( clientAddress ) );

    public JsonObject ReadSettingsFile() =>
        JsonNode.Parse( File.ReadAllText( Path.Combine( ContentRoot, "config", "appsettings.json" ) ) )!.AsObject();

    public void Dispose()
    {
        _factory.Dispose();
        DeleteContentRoot();
    }

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();
        DeleteContentRoot();
    }

    private void DeleteContentRoot()
    {
        try
        {
            if ( Directory.Exists( ContentRoot ) )
                Directory.Delete( ContentRoot, recursive: true );
        }
        catch ( IOException )
        {
            // A leftover temporary directory is not a test failure.
        }
    }

    /// <summary>
    /// The service's own wwwroot in the source tree, read-only, so the page
    /// served is the real one. Found by walking up from the test output.
    /// </summary>
    private static string FindServiceWebRoot()
    {
        for ( var directory = new DirectoryInfo( AppContext.BaseDirectory ); directory != null; directory = directory.Parent )
        {
            var candidate = Path.Combine( directory.FullName, "Rock.CloudPrint.Service", "wwwroot" );

            if ( File.Exists( Path.Combine( candidate, "index.html" ) ) )
                return candidate;
        }

        throw new DirectoryNotFoundException( "Could not find Rock.CloudPrint.Service/wwwroot above " + AppContext.BaseDirectory );
    }

    private sealed class UiHeadersHandler : DelegatingHandler
    {
        private readonly string? _clientAddress;

        public UiHeadersHandler( string? clientAddress ) => _clientAddress = clientAddress;

        protected override Task<HttpResponseMessage> SendAsync( HttpRequestMessage request, CancellationToken cancellationToken )
        {
            if ( request.Method != HttpMethod.Get && request.Method != HttpMethod.Head )
                request.Headers.TryAddWithoutValidation( "X-CloudPrint-Request", "1" );

            if ( _clientAddress != null )
                request.Headers.TryAddWithoutValidation( ClientAddressHeader, _clientAddress );

            return base.SendAsync( request, cancellationToken );
        }
    }

    private sealed class ClientAddressStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure( Action<IApplicationBuilder> next ) => app =>
        {
            app.Use( ( context, nextMiddleware ) =>
            {
                if ( IPAddress.TryParse( context.Request.Headers[ClientAddressHeader].ToString(), out var address ) )
                    context.Connection.RemoteIpAddress = address;

                return nextMiddleware( context );
            } );

            next( app );
        };
    }
}
