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
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

using Microsoft.Extensions.Options;

namespace Rock.CloudPrint.Service;

public class Program
{
    /// <summary>
    /// How long the printer test waits before giving up. The proxy itself
    /// imposes no such limit; this exists so a person pressing a button gets an
    /// answer rather than waiting on the operating system.
    /// </summary>
    private static readonly TimeSpan PrinterTestTimeout = TimeSpan.FromSeconds( 5 );

    /// <summary>
    /// Longest string accepted as a printer address, checked before parsing.
    /// </summary>
    private const int MaxPrinterAddressLength = 100;

    /// <summary>Longest string accepted as a notification webhook URL.</summary>
    private const int MaxNotificationUrlLength = 500;

    /// <summary>
    /// Longest base64 string accepted for an uploaded label, checked before it
    /// is decoded rather than after. Base64 costs four characters for every
    /// three bytes, and the slack covers any line breaks the encoder added.
    /// </summary>
    private const int MaxLabelUploadCharacters = ( LabelStore.MaxContentBytes / 3 + 1 ) * 4 + 1024;

    public static void Main( string[] args )
    {
        var builder = WebApplication.CreateBuilder( args );

        // Wire up the in-memory log sink before building so the logger
        // provider can capture startup messages.
        var logSink = new InMemoryLogSink();
        builder.Services.AddSingleton( logSink );
        builder.Logging.AddProvider( new InMemoryLoggerProvider( logSink ) );

        var serviceVersion = GetServiceVersion();

        builder.Services.Configure<CloudPrintOptions>( builder.Configuration );
        builder.Services.AddSingleton<ProxyStatus>();
        builder.Services.AddSingleton<PrintMetrics>();
        builder.Services.AddSingleton<PrinterTester>();
        builder.Services.AddSingleton<LabelStore>();
        builder.Services.AddSingleton<AuthService>();

        // Redirects are NOT followed: a URL matching no webhook in Rock redirects
        // to Rock's own 404 page, which answers 200 - so a following client would
        // report a wrong URL as a broken template instead of a wrong URL.
        builder.Services.AddHttpClient( "notifications" )
            .ConfigurePrimaryHttpMessageHandler( () => new HttpClientHandler { AllowAutoRedirect = false } );

        builder.Services.AddSingleton( sp => new FailureNotifier(
            sp.GetRequiredService<IHttpClientFactory>(),
            sp.GetRequiredService<IOptionsMonitor<CloudPrintOptions>>(),
            sp.GetRequiredService<ILogger<FailureNotifier>>(),
            serviceVersion ) );

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

            // The printer test opens a TCP connection to whatever address it is
            // given. That is not a capability an authenticated user lacks - they
            // could already point the proxy at a server of their own choosing -
            // but it is a far more convenient one, so it is capped. Ten a minute
            // is generous for someone pressing a button and useless for sweeping
            // a subnet.
            options.AddFixedWindowLimiter( "printertest", o =>
            {
                o.PermitLimit = 10;
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

        // On a genuinely first run this creates config/labels and writes the
        // demo templates. It swallows its own failures: an unwritable config
        // mount is a reason to have no demo labels, not a reason for the proxy
        // not to start.
        app.Services.GetRequiredService<LabelStore>().SeedIfFirstRun();

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

        // Describes a print attempt for the dashboard, or null when there has
        // not been one of that kind yet.
        static object? DescribePrintEvent( PrintEvent? printEvent )
        {
            if ( printEvent == null )
            {
                return null;
            }

            return new
            {
                address = printEvent.Address,
                reason = printEvent.Reason,
                elapsedMilliseconds = printEvent.ElapsedMilliseconds,
                at = printEvent.Timestamp,
                exceededRockTimeout = printEvent.ExceededRockTimeout,
                succeeded = printEvent.Succeeded
            };
        }

        // Describes the last notification attempt so the dashboard can name the
        // fault rather than saying "it failed".
        static object? DescribeNotification( NotificationAttempt? attempt )
        {
            if ( attempt == null )
            {
                return null;
            }

            return new
            {
                at = attempt.At,
                eventKind = attempt.Event,
                printer = attempt.Printer,
                delivered = attempt.Delivered,
                statusCode = attempt.StatusCode,
                outcome = attempt.Outcome,
                detail = attempt.Detail
            };
        }

        app.MapGet( "/api/status", ( ProxyStatus status, PrintMetrics metrics, FailureNotifier notifier, IOptionsMonitor<CloudPrintOptions> options ) =>
            Results.Ok( new
            {
                version = serviceVersion,
                isConnected = status.IsConnected,
                isConfigured = !string.IsNullOrWhiteSpace( options.CurrentValue.Url )
                    && !string.IsNullOrWhiteSpace( options.CurrentValue.Id ),
                startedDateTime = status.StartedDateTime,
                connectedDateTime = status.ConnectedDateTime,
                totalLabelsPrinted = status.TotalPrinted,
                labelsPrinted = metrics.SuccessfulLabels,
                labelsFailed = metrics.FailedLabels,
                slowPrints = metrics.SlowPrints,
                lastFailure = DescribePrintEvent( metrics.LastFailure ),
                lastSlowPrint = DescribePrintEvent( metrics.LastSlowPrint ),
                notifications = new
                {
                    enabled = options.CurrentValue.NotificationsEnabled,
                    configured = !string.IsNullOrWhiteSpace( options.CurrentValue.NotificationUrl ),
                    lastAttempt = DescribeNotification( notifier.LastAttempt )
                }
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

        // Returns the tail of the in-memory log buffer. Pass ?after= with the
        // lastSeq from the previous call to fetch only what is new - this is how
        // the UI keeps its log pane live without re-reading the whole buffer.
        // Omit it to start fresh, and pass ?limit=2000 to pull the full buffer
        // when investigating something.
        app.MapGet( "/api/logs", ( InMemoryLogSink sink, long? after, int? limit ) =>
            Results.Ok( sink.GetTail( after ?? 0, limit ?? 300 ) ) );

        // Opens a connection to a printer and reports what happened, using the
        // same parsing and the same kind of socket the print path uses.
        //
        // Connection only - nothing is written and nothing is read back, so this
        // reports reachable or not and never any content from the far end.
        app.MapPost( "/api/printer/test", async ( PrinterTestRequest request, PrinterTester tester, CancellationToken cancellationToken ) =>
        {
            var address = ( request.Address ?? string.Empty ).Trim();

            if ( string.IsNullOrWhiteSpace( address ) )
                return Results.Json( new { error = "Enter a printer address." }, statusCode: 400 );

            // Bounded before parsing rather than after.
            if ( address.Length > MaxPrinterAddressLength )
                return Results.Json( new { error = "That address is too long to be a printer address." }, statusCode: 400 );

            // Only one mode exists today. An unrecognised one is rejected rather
            // than quietly downgraded, so a future client cannot believe it sent
            // a label when it only opened a connection.
            if ( !string.IsNullOrWhiteSpace( request.Mode )
                && !string.Equals( request.Mode, "connect", StringComparison.OrdinalIgnoreCase ) )
                return Results.Json( new { error = $"Unsupported test mode '{request.Mode}'." }, statusCode: 400 );

            var result = await tester.TestAsync( address, null, PrinterTestTimeout, cancellationToken );

            return Results.Ok( result );
        } ).RequireRateLimiting( "printertest" );

        // Returns the notification settings. The secret is never sent back - only
        // whether one is set - so it cannot be read out of the UI.
        app.MapGet( "/api/settings/notifications", ( IOptionsMonitor<CloudPrintOptions> options ) =>
        {
            var current = options.CurrentValue;

            return Results.Ok( new
            {
                enabled = current.NotificationsEnabled,
                url = current.NotificationUrl,
                secretIsSet = !string.IsNullOrWhiteSpace( current.NotificationSecret ),
                cooldownMinutes = current.NotificationCooldownMinutes
            } );
        } );

        // Saves the notification settings. Sending null for the secret leaves the
        // stored one alone, so the UI can save the other fields without having to
        // round-trip a value it is never given.
        app.MapPost( "/api/settings/notifications", async ( NotificationSettingsRequest request, IConfiguration config, IWebHostEnvironment env ) =>
        {
            var url = ( request.Url ?? string.Empty ).Trim();

            if ( url.Length > MaxNotificationUrlLength )
                return Results.Json( new { error = "That URL is too long." }, statusCode: 400 );

            if ( url.Length > 0 && !Uri.TryCreate( url, UriKind.Absolute, out _ ) )
                return Results.Json( new { error = "That is not a valid URL." }, statusCode: 400 );

            // The secret travels in a header. Refuse plain HTTP outright rather
            // than accepting a setting that can only ever send it in the clear.
            if ( url.Length > 0 && !url.StartsWith( "https://", StringComparison.OrdinalIgnoreCase ) )
                return Results.Json( new { error = "The webhook URL must start with https:// - the secret is sent in a request header." }, statusCode: 400 );

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

            json["NotificationsEnabled"] = request.Enabled;
            json["NotificationUrl"] = url;
            json["NotificationCooldownMinutes"] = Math.Clamp( request.CooldownMinutes ?? 5, 0, 1440 );

            if ( request.Secret != null )
            {
                if ( string.IsNullOrWhiteSpace( request.Secret ) )
                    json.AsObject().Remove( "NotificationSecret" );
                else
                    json["NotificationSecret"] = request.Secret;
            }

            await File.WriteAllTextAsync( settingsPath, json.ToJsonString( new JsonSerializerOptions { WriteIndented = true } ) );

            if ( config is IConfigurationRoot root )
                root.Reload();

            return Results.Ok( new { success = true } );
        } );

        // Sends a real notification so the whole chain can be proved at
        // configuration time: URL, secret, address allowance, webhook match,
        // workflow type lookup and activation. Rate limited, because it reaches
        // out to another server on demand.
        //
        // This messages whoever the Rock workflow is configured to tell. The UI
        // says so beside the button.
        app.MapPost( "/api/notifications/test", async ( FailureNotifier notifier, CancellationToken cancellationToken ) =>
        {
            var attempt = await notifier.SendTestAsync( cancellationToken );

            return Results.Ok( new
            {
                delivered = attempt.Delivered,
                statusCode = attempt.StatusCode,
                outcome = attempt.Outcome,
                detail = attempt.Detail
            } );
        } ).RequireRateLimiting( "printertest" );

        // ── Blank labels ───────────────────────────────────────────────────
        // The stored ZPL templates that blank check-in labels are printed from.
        // Nothing here touches the Rock server, and that is the point: blanks
        // exist for the times Rock cannot be reached, so what produces them
        // must not depend on it.

        app.MapGet( "/api/labels", ( LabelStore labels ) =>
            Results.Ok( labels.List().Select( label => new
            {
                name       = label.Name,
                bytes      = label.Bytes,
                modifiedAt = label.ModifiedAt,
                widthDots  = label.WidthDots,
                lengthDots = label.LengthDots
            } ) ) );

        // The template arrives as base64 inside JSON rather than as a multipart
        // form. A form post would bring .NET's antiforgery validation into play
        // for no gain, since the bearer token this endpoint already requires is
        // the defence that matters here.
        app.MapPost( "/api/labels", ( LabelUploadRequest request, LabelStore labels ) =>
        {
            var name = ( request.Name ?? string.Empty ).Trim();
            var encoded = request.ContentBase64 ?? string.Empty;

            if ( !LabelStore.IsValidName( name ) )
                return Results.Json( new { error = $"A label name can be up to {LabelStore.MaxNameLength} letters, digits, spaces, dots, dashes and underscores." }, statusCode: 400 );

            // Bounded before decoding rather than after, so an oversized upload
            // costs a length check instead of a megabyte of allocation.
            if ( encoded.Length > MaxLabelUploadCharacters )
                return Results.Json( new { error = "That label is too large." }, statusCode: 400 );

            byte[] content;

            try
            {
                content = Convert.FromBase64String( encoded );
            }
            catch ( FormatException )
            {
                return Results.Json( new { error = "The label content was not valid base64." }, statusCode: 400 );
            }

            return labels.Save( name, content ) switch
            {
                LabelSaveOutcome.Saved => Results.Ok( new { name, bytes = content.Length } ),
                LabelSaveOutcome.AlreadyExists => Results.Json( new { error = $"There is already a label called '{name}'. Delete that one first, or use another name." }, statusCode: 409 ),
                LabelSaveOutcome.TooLarge => Results.Json( new { error = "That label is too large." }, statusCode: 400 ),
                LabelSaveOutcome.NotZpl => Results.Json( new { error = "That does not look like ZPL. A label should contain ^XA and ^XZ." }, statusCode: 400 ),
                LabelSaveOutcome.NoCodeToken => Results.Json( new { error = $"That label has no {ZplTemplate.CodeToken} inside a ^FD field, so there is nowhere to put the security code." }, statusCode: 400 ),
                _ => Results.Json( new { error = "That label could not be stored." }, statusCode: 400 )
            };
        } );

        app.MapDelete( "/api/labels/{name}", ( string name, LabelStore labels ) =>
        {
            // The route value is checked exactly as an uploaded name is. A route
            // segment is every bit as much caller-supplied input as a body, and
            // it is the one people forget.
            if ( !LabelStore.IsValidName( name ) )
                return Results.Json( new { error = "That is not a label name." }, statusCode: 400 );

            return labels.Delete( name )
                ? Results.Ok( new { deleted = name } )
                : Results.Json( new { error = $"There is no label called '{name}'." }, statusCode: 404 );
        } );

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

    /// <summary>
    /// The version this build was published with. Supplied by the Docker build
    /// from the git tag, so what the UI reports and what the image is tagged
    /// with cannot drift apart. Local builds report the csproj default.
    /// </summary>
    private static string GetServiceVersion()
    {
        var informationalVersion = typeof( Program ).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

        if ( string.IsNullOrWhiteSpace( informationalVersion ) )
        {
            return "unknown";
        }

        // The SDK appends "+<commit sha>" when source revision info is present.
        var metadataIndex = informationalVersion.IndexOf( '+' );

        return metadataIndex >= 0
            ? informationalVersion[..metadataIndex]
            : informationalVersion;
    }
}

internal record SettingsRequest( string Url, string Name, string Id );
internal record LoginRequest( string Password );
internal record SecurityRequest( string? CurrentPassword, string? NewPassword );
internal record PrinterTestRequest( string? Address, string? Mode );

/// <summary>
/// A label template being uploaded. The content is base64 so that arbitrary
/// bytes survive the trip - a template can legitimately contain <c>^GF</c>
/// graphics and text that is not valid UTF-8.
/// </summary>
internal record LabelUploadRequest( string? Name, string? ContentBase64 );

/// <summary>
/// Notification settings from the web UI. <c>Secret</c> is null when the caller
/// is leaving the stored secret alone, and empty when clearing it.
/// </summary>
internal record NotificationSettingsRequest( bool Enabled, string? Url, string? Secret, int? CooldownMinutes );
