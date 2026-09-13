// <copyright>
// Copyright by the Spark Development Network
//
// Licensed under the Rock Community License (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
// http://www.rockrms.com/license
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.
// </copyright>
//
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

using Microsoft.Extensions.Options;

namespace Rock.CloudPrint.Service;

public class Program
{
    public static void Main( string[] args )
    {
        var builder = WebApplication.CreateBuilder( args );

        // Wire up the in-memory log sink before building so the logger
        // provider can capture startup messages.
        var logSink = new InMemoryLogSink();
        builder.Services.AddSingleton( logSink );
        builder.Logging.AddProvider( new InMemoryLoggerProvider( logSink ) );

        builder.Services.Configure<CloudPrintOptions>( builder.Configuration );
        builder.Services.AddSingleton<ProxyStatus>();
        builder.Services.AddSingleton<AuthService>();
        builder.Services.AddHostedService<ProxyWorker>();

        // Rate limit /api/auth/login to 5 attempts per minute per source.
        // Excess attempts return HTTP 429 with no queue, blocking PIN brute-force
        // attacks against the web UI without affecting normal interactive logins.
        builder.Services.AddRateLimiter( options =>
        {
            options.RejectionStatusCode = 429;
            options.AddFixedWindowLimiter( "login", o =>
            {
                o.PermitLimit = 5;
                o.Window = TimeSpan.FromMinutes( 1 );
                o.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
                o.QueueLimit = 0;
            } );
        } );

        // Suppress the default "Server: Kestrel" response header so the
        // service does not advertise its underlying web stack to clients.
        builder.WebHost.ConfigureKestrel( o => o.AddServerHeader = false );

        // Persistent user settings are stored in the config sub-directory so
        // operators can bind-mount an entire directory (e.g. a TrueNAS dataset
        // or a host folder) rather than a single file.
        builder.Configuration.AddJsonFile( "config/appsettings.json", optional: true, reloadOnChange: true );

        var app = builder.Build();

        app.UseRateLimiter();

        // ── Security headers ───────────────────────────────────────
        // Applied to every response (including static files). Tailwind is
        // compiled into wwwroot/app.css at build time rather than pulled from
        // a CDN, so no third-party origin is allowed here and style-src needs
        // no 'unsafe-inline'. script-src still needs 'unsafe-inline' because
        // the SPA keeps its JavaScript in an inline <script> block and uses
        // inline onclick handlers.
        //   X-Frame-Options:        defence against clickjacking embed
        //   X-Content-Type-Options: disables MIME-type sniffing
        //   Referrer-Policy:        prevents Rock URL leakage via Referer
        //   Cache-Control:          stops sensitive pages from being cached
        //   Content-Security-Policy:restricts what the page may load/run
        app.Use( async ( ctx, next ) =>
        {
            ctx.Response.Headers["X-Frame-Options"]        = "DENY";
            ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
            ctx.Response.Headers["Referrer-Policy"]        = "no-referrer";
            ctx.Response.Headers["Cache-Control"]          = "no-store";
            ctx.Response.Headers["Content-Security-Policy"] =
                "default-src 'self'; " +
                "script-src 'self' 'unsafe-inline'; " +
                "style-src 'self'; " +
                "img-src 'self' data:;";
            await next();
        } );

        app.UseDefaultFiles();
        app.UseStaticFiles();

        // ── Authentication middleware ──────────────────────────────────────
        // Protect every /api/* route except /api/auth/* which must remain
        // publicly accessible so the login flow and first-time PIN setup work
        // without a token.
        app.Use( async ( context, next ) =>
        {
            if ( context.Request.Path.StartsWithSegments( "/api" ) &&
                 !context.Request.Path.StartsWithSegments( "/api/auth" ) )
            {
                var auth    = context.RequestServices.GetRequiredService<AuthService>();
                var options = context.RequestServices.GetRequiredService<IOptionsMonitor<CloudPrintOptions>>();

                if ( auth.IsPasswordConfigured( options ) )
                {
                    var header = context.Request.Headers.Authorization.FirstOrDefault() ?? string.Empty;
                    var token  = header.StartsWith( "Bearer " ) ? header["Bearer ".Length..] : string.Empty;

                    if ( !auth.ValidateToken( token ) )
                    {
                        context.Response.StatusCode = 401;
                        await context.Response.WriteAsJsonAsync( new { error = "Unauthorized" } );
                        return;
                    }
                }
            }

            await next( context );
        } );

        // ── Public auth endpoints ─────────────────────────────────────────

        // Returns whether a PIN is required and whether it comes from an env var.
        app.MapGet( "/api/auth/config", ( IOptionsMonitor<CloudPrintOptions> options ) =>
        {
            var fromEnvVar = !string.IsNullOrWhiteSpace( Environment.GetEnvironmentVariable( "Password" ) );
            var required   = !string.IsNullOrWhiteSpace( options.CurrentValue.Password );
            return Results.Ok( new { required, fromEnvVar } );
        } );

        // Validates a PIN and issues a bearer token on success.
        app.MapPost( "/api/auth/login", ( LoginRequest req, IOptionsMonitor<CloudPrintOptions> options, AuthService auth ) =>
        {
            if ( !auth.IsPasswordConfigured( options ) )
                return Results.Ok( new { token = string.Empty } );

            if ( !auth.ValidatePassword( req.Password, options ) )
                return Results.Json( new { error = "Incorrect PIN or password." }, statusCode: 401 );

            return Results.Ok( new { token = auth.IssueToken() } );
        } ).RequireRateLimiting( "login" );  // brute-force protection

        // Revokes the caller's bearer token.
        app.MapPost( "/api/auth/logout", ( HttpContext ctx, AuthService auth ) =>
        {
            var header = ctx.Request.Headers.Authorization.FirstOrDefault() ?? string.Empty;
            var token  = header.StartsWith( "Bearer " ) ? header["Bearer ".Length..] : string.Empty;
            if ( !string.IsNullOrEmpty( token ) ) auth.RevokeToken( token );
            return Results.Ok( new { success = true } );
        } );

        // ── Protected endpoints ──────────────────────────────────────────

        app.MapGet( "/api/status", ( ProxyStatus status, IOptionsMonitor<CloudPrintOptions> options ) =>
            Results.Ok( new
            {
                isConnected = status.IsConnected,
                isConfigured = !string.IsNullOrWhiteSpace( options.CurrentValue.Url )
                    && !string.IsNullOrWhiteSpace( options.CurrentValue.Id ),
                startedDateTime = status.StartedDateTime,
                connectedDateTime = status.ConnectedDateTime,
                totalLabelsPrinted = status.TotalPrinted
            } ) );

        app.MapGet( "/api/settings", ( IConfiguration config ) =>
            Results.Ok( new
            {
                url  = config["Url"]  ?? string.Empty,
                name = config["Name"] ?? string.Empty,
                id   = config["Id"]   ?? string.Empty
            } ) );

        app.MapPost( "/api/settings", async ( SettingsRequest settings, IConfiguration config, IWebHostEnvironment env ) =>
        {
            var settingsPath = Path.Combine( env.ContentRootPath, "config", "appsettings.json" );
            Directory.CreateDirectory( Path.GetDirectoryName( settingsPath )! );

            JsonNode json;

            if ( File.Exists( settingsPath ) )
            {
                await using var stream = File.OpenRead( settingsPath );
                json = await JsonNode.ParseAsync( stream ) ?? new JsonObject();
            }
            else
            {
                json = new JsonObject();
            }

            json["Url"]  = settings.Url;
            json["Name"] = settings.Name;
            json["Id"]   = settings.Id;

            await File.WriteAllTextAsync( settingsPath, json.ToJsonString( new JsonSerializerOptions { WriteIndented = true } ) );

            if ( config is IConfigurationRoot root )
            {
                root.Reload();
            }

            return Results.Ok( new { success = true } );
        } );

        // Sets, changes, or removes the PIN. Requires the correct current PIN when
        // one is already configured. Sending an empty newPassword removes protection.
        app.MapPost( "/api/settings/security", async ( SecurityRequest req, IOptionsMonitor<CloudPrintOptions> options, IConfiguration config, IWebHostEnvironment env, AuthService auth ) =>
        {
            // If the password is supplied via environment variable the web UI
            // cannot override it — direct the user to docker-compose.yml instead.
            if ( !string.IsNullOrWhiteSpace( Environment.GetEnvironmentVariable( "Password" ) ) )
                return Results.Json(
                    new { error = "PIN is controlled by the Password environment variable and cannot be changed here. Edit docker-compose.yml and restart the container." },
                    statusCode: 403 );

            // When a PIN is already set the caller must prove they know it.
            if ( auth.IsPasswordConfigured( options ) )
            {
                if ( !auth.ValidatePassword( req.CurrentPassword ?? string.Empty, options ) )
                    return Results.Json( new { error = "Current PIN is incorrect." }, statusCode: 400 );
            }

            var settingsPath = Path.Combine( env.ContentRootPath, "config", "appsettings.json" );
            Directory.CreateDirectory( Path.GetDirectoryName( settingsPath )! );

            JsonNode json;
            if ( File.Exists( settingsPath ) )
            {
                await using var stream = File.OpenRead( settingsPath );
                json = await JsonNode.ParseAsync( stream ) ?? new JsonObject();
            }
            else
            {
                json = new JsonObject();
            }

            // Store the PIN as plain text. This service runs on a local trusted
            // network; the overhead of hashing is not warranted here.
            if ( string.IsNullOrWhiteSpace( req.NewPassword ) )
                json.AsObject().Remove( "Password" );
            else
                json["Password"] = req.NewPassword;

            await File.WriteAllTextAsync( settingsPath, json.ToJsonString( new JsonSerializerOptions { WriteIndented = true } ) );

            if ( config is IConfigurationRoot root )
                root.Reload();

            // Invalidate all existing sessions so they must re-authenticate.
            auth.RevokeAll();

            return Results.Ok( new { success = true } );
        } );

        app.MapGet( "/api/logs", ( InMemoryLogSink sink ) =>
            Results.Ok( sink.GetEntries() ) );

        app.MapPost( "/api/restart", ( IHostApplicationLifetime lifetime ) =>
        {
            // Delay slightly so the HTTP response is fully sent before shutdown begins.
            _ = Task.Run( async () =>
            {
                await Task.Delay( 300 );
                lifetime.StopApplication();
            } );

            return Results.Ok( new { restarting = true } );
        } );

        app.Run();
    }
}

internal record SettingsRequest( string Url, string Name, string Id );
internal record LoginRequest( string Password );
internal record SecurityRequest( string? CurrentPassword, string? NewPassword );
