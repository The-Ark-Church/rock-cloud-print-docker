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

    /// <summary>
    /// How many labels one preview may draw. Each is a separate call to the
    /// rendering service, and a copy is two or three labels.
    /// </summary>
    private const int MaxLabelsPerPreview = 8;

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
        builder.Services.AddSingleton<BlankLabelStateStore>();
        builder.Services.AddSingleton<BlankLabelRunner>();
        builder.Services.AddSingleton<LabelCapture>();

        builder.Services.AddHttpClient( "labelary" );
        builder.Services.AddSingleton<LabelPreview>();
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

            // The preview endpoint calls a third-party service on every press.
            // Labelary publishes a free-tier rate for exactly this, and staying
            // under it is good manners rather than a security measure - the
            // caller is already past the PIN.
            options.AddFixedWindowLimiter( "labelary", o =>
            {
                o.PermitLimit = 3;
                o.Window = TimeSpan.FromSeconds( 1 );
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

        // Resolved at startup rather than on first use so that a record of used
        // security codes which cannot be read is reported in the log while
        // somebody is looking at it, not at the moment they press print.
        app.Services.GetRequiredService<BlankLabelStateStore>();

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

        // Renders the ticked labels as pictures, so somebody can see what will
        // come out before printing a stack of it.
        //
        // Every label is drawn with the SAME code, because that is what a copy
        // is - the child's tag and the parent's receipt carry one code between
        // them. Showing them with different codes would misrepresent the thing
        // being previewed.
        //
        // The code is a real one from the chosen mode: an actual random code,
        // or the actual next sequential code. It used to be a row of X, on the
        // reasoning that the widest character is the safest test of whether a
        // code fits. Twice wrong: W is the widest character, not X, and being
        // shown a placeholder after asking for random codes says nothing about
        // what will be on the labels. Widest-case now belongs to the test copy,
        // which prints WWW.
        //
        // The PNGs come back as base64 for data: URLs. An img tag cannot carry
        // the bearer token this API requires, and the content security policy
        // allows images only from this origin and from data:, so a blob: URL
        // would fail to render with nothing in the console to say why.
        app.MapPost( "/api/labels/preview", async ( LabelPreviewRequest request, LabelStore labels, LabelPreview preview, BlankLabelStateStore state, CancellationToken cancellationToken ) =>
        {
            var names = ( request.Names ?? Array.Empty<string>() )
                .Select( n => ( n ?? string.Empty ).Trim() )
                .Where( n => n.Length > 0 )
                .ToArray();

            if ( names.Length == 0 )
                return Results.Json( new { error = "Choose at least one label to preview." }, statusCode: 400 );

            // Each label is one call to the rendering service, so this bounds
            // how many a single request can make. A copy is two or three.
            if ( names.Length > MaxLabelsPerPreview )
                return Results.Json( new { error = $"Preview shows up to {MaxLabelsPerPreview} labels at a time." }, statusCode: 400 );

            var prefix = ( request.Prefix ?? string.Empty ).Trim();

            if ( !SecurityCode.IsUsablePrefix( prefix ) )
                return Results.Json( new { error = "A prefix can be letters, digits, dashes and underscores." }, statusCode: 400 );

            var templates = new List<(string Name, byte[] Content)>( names.Length );

            foreach ( var name in names )
            {
                if ( !LabelStore.IsValidName( name ) )
                    return Results.Json( new { error = "That is not a label name." }, statusCode: 400 );

                var template = labels.Read( name );

                if ( template == null )
                    return Results.Json( new { error = $"There is no label called '{name}'." }, statusCode: 404 );

                templates.Add( (name, template) );
            }

            string code;

            if ( string.Equals( request.Mode, "sequential", StringComparison.OrdinalIgnoreCase ) )
            {
                // The code the next run would actually start with.
                var start = !string.IsNullOrWhiteSpace( request.Start )
                    ? request.Start.Trim()
                    : state.Current.SequentialNext;

                if ( !SecurityCode.IsUsableStart( start ) )
                    start = "0001";

                code = SecurityCode.Sequential( start!, 1, prefix )[0];
            }
            else
            {
                var length = request.CodeLength ?? SecurityCode.DefaultLength;

                if ( length < 1 || length > SecurityCode.MaxLength )
                    return Results.Json( new { error = $"A security code is between 1 and {SecurityCode.MaxLength} characters." }, statusCode: 400 );

                code = SecurityCode.Random( length, 1, prefix )[0];
            }

            var rendered = new List<object>( templates.Count );

            foreach ( var (name, template) in templates )
            {
                // One at a time rather than together, to stay well inside the
                // rendering service's published rate.
                var result = await preview.RenderAsync( template, code, cancellationToken );

                if ( !result.Ok )
                {
                    // A bad gateway rather than a server error: the proxy is
                    // fine, the thing it asked is not. Printing is unaffected.
                    return Results.Json( new { error = result.Error }, statusCode: 502 );
                }

                rendered.Add( new
                {
                    name,
                    png = Convert.ToBase64String( result.Png! ),
                    widthDots = result.WidthDots,
                    lengthDots = result.LengthDots
                } );
            }

            return Results.Ok( new { code, dpi = LabelPreview.Dpi, labels = rendered } );
        } ).RequireRateLimiting( "labelary" );

        // Starts a run and answers immediately with its id. The run is not tied
        // to this request: a stack of a thousand takes a while, and a closed
        // tab must not cancel it half way through and lose the record of which
        // codes went out. Progress is polled, and the only thing that ends a
        // run early is a person pressing Cancel.
        //
        // Shares the printer test's rate limit, for the same reason it exists:
        // this opens a connection to whatever address it is given.
        app.MapPost( "/api/labels/print", ( BlankPrintRequest request, BlankLabelRunner runner, BlankLabelStateStore state ) =>
        {
            var address = ( request.Address ?? string.Empty ).Trim();

            if ( address.Length > MaxPrinterAddressLength )
                return Results.Json( new { error = "That address is too long to be a printer address." }, statusCode: 400 );

            var (outcome, run, detail) = runner.Start( new BlankRunRequest
            {
                Address = address,
                Labels = request.Labels ?? Array.Empty<string>(),
                Quantity = request.Quantity ?? 0,
                Mode = request.Mode ?? "random",
                CodeLength = request.CodeLength ?? SecurityCode.DefaultLength,
                Start = request.Start,
                Prefix = ( request.Prefix ?? string.Empty ).Trim(),
                HasCutter = request.HasCutter ?? false,
                IsTestCopy = request.Test ?? false
            } );

            return outcome switch
            {
                BlankRunStartOutcome.Started => Results.Json( run, statusCode: 202 ),

                // No queue. One run at a time is the whole of the serialisation
                // story, and telling somebody to wait is clearer than silently
                // holding their request until a stack of a thousand finishes.
                BlankRunStartOutcome.AlreadyRunning => Results.Json(
                    new { error = "A run is already in progress.", run }, statusCode: 409 ),

                BlankRunStartOutcome.NoLabels => Results.Json(
                    new { error = detail ?? "Choose at least one label to print." }, statusCode: 400 ),
                BlankRunStartOutcome.UnknownLabel => Results.Json(
                    new { error = $"There is no label called '{detail}'." }, statusCode: 400 ),
                BlankRunStartOutcome.BadAddress => Results.Json(
                    new { error = detail ?? "That is not a printer address." }, statusCode: 400 ),
                BlankRunStartOutcome.BadQuantity => Results.Json(
                    new { error = "Enter how many copies to print." }, statusCode: 400 ),
                BlankRunStartOutcome.BadCodeLength => Results.Json(
                    new { error = $"A security code is between 1 and {SecurityCode.MaxLength} characters." }, statusCode: 400 ),
                BlankRunStartOutcome.NotEnoughCodes => Results.Json(
                    new { error = $"There are not enough different codes for that many copies. {detail}" }, statusCode: 400 ),
                BlankRunStartOutcome.BadPrefix => Results.Json(
                    new { error = "A prefix can be letters, digits, dashes and underscores." }, statusCode: 400 ),

                BlankRunStartOutcome.NoSequentialStart => Results.Json( new
                {
                    error = state.SequentialStateUnreadable
                        ? "The record of which codes have been used could not be read, so this run needs a starting number. Check the last stack that was printed."
                        : "Enter the number to start counting from.",
                    sequentialStateUnreadable = state.SequentialStateUnreadable
                }, statusCode: 400 ),

                // The codes could not be written down, so they are not sent.
                // Printing a stack nobody has a record of is worse than not
                // printing one.
                BlankRunStartOutcome.CouldNotReserve => Results.Json(
                    new { error = $"The security codes could not be recorded, so nothing was printed. {detail}" }, statusCode: 500 ),

                _ => Results.Json( new { error = "That run could not be started." }, statusCode: 400 )
            };
        } ).RequireRateLimiting( "printertest" );

        // What the UI needs on load and while polling: the run, where the
        // numbering has reached, and what went out before.
        app.MapGet( "/api/labels/print", ( BlankLabelRunner runner, BlankLabelStateStore state ) =>
        {
            var current = state.Current;

            return Results.Ok( new
            {
                run = runner.Current,
                sequentialNext = current.SequentialNext,
                sequentialReservedThrough = current.SequentialReservedThrough,
                sequentialStateUnreadable = state.SequentialStateUnreadable,
                history = current.History
            } );
        } );

        app.MapGet( "/api/labels/print/{runId}", ( string runId, BlankLabelRunner runner ) =>
        {
            var run = runner.Current;

            return run != null && run.Id == runId
                ? Results.Ok( run )
                : Results.Json( new { error = "There is no run with that id." }, statusCode: 404 );
        } );

        // Cancelling is a person's decision, and it is also how a run that is
        // waiting on a printer nobody is going to fix gets out of the way.
        app.MapPost( "/api/labels/print/{runId}/cancel", ( string runId, BlankLabelRunner runner ) =>
        {
            return runner.Cancel( runId )
                ? Results.Ok( new { cancelling = runId } )
                : Results.Json( new { error = "There is no run with that id still going." }, statusCode: 404 );
        } );

        // ── Capturing a label from Rock ────────────────────────────────────
        // Rock will not hand out the ZPL for a label designed in its own
        // designer, but the proxy is in the middle of every print, so a label
        // Rock prints arrives here as raw ZPL whatever it was authored as.
        // Capture works by being an ordinary printer: point a Device in Rock at
        // this proxy and the capture port, and print to it. Nothing in the
        // print path changes, or knows.

        app.MapPost( "/api/labels/capture/arm", ( CaptureArmRequest request, LabelCapture capture ) =>
        {
            var error = capture.Arm( request.Port ?? LabelCapture.DefaultPort );

            return error == null
                ? Results.Json( capture.Snapshot(), statusCode: 202 )
                : Results.Json( new { error }, statusCode: 409 );
        } );

        app.MapGet( "/api/labels/capture", ( LabelCapture capture ) => Results.Ok( capture.Snapshot() ) );

        app.MapPost( "/api/labels/capture/disarm", ( LabelCapture capture ) =>
        {
            capture.Disarm();

            return Results.Ok( capture.Snapshot() );
        } );

        app.MapPost( "/api/labels/capture/discard", ( LabelCapture capture ) =>
        {
            capture.Discard();

            return Results.Ok( capture.Snapshot() );
        } );

        // Saves what was captured as a template, with the designer's own
        // placeholder marked as the security code position.
        app.MapPost( "/api/labels/capture/save", ( CaptureSaveRequest request, LabelCapture capture, LabelStore labels ) =>
        {
            var captured = capture.Captured;

            if ( captured == null )
                return Results.Json( new { error = "There is nothing captured to save." }, statusCode: 409 );

            var name = ( request.Name ?? string.Empty ).Trim();
            var fields = request.Fields ?? Array.Empty<int>();

            if ( !LabelStore.IsValidName( name ) )
                return Results.Json( new { error = $"A label name can be up to {LabelStore.MaxNameLength} letters, digits, spaces, dots, dashes and underscores." }, statusCode: 400 );

            if ( fields.Length == 0 )
                return Results.Json( new { error = "Choose which field holds the security code." }, statusCode: 400 );

            // By position rather than by text. On a captured label the security
            // code field is usually empty - Rock renders it from an attendance
            // that a test print does not have - and whatever stands in for
            // empty appears in other fields too.
            var template = ZplTemplate.MarkCodeFields( captured, fields );

            var outcome = labels.Save( name, template );

            if ( outcome == LabelSaveOutcome.Saved )
            {
                capture.Discard();

                return Results.Ok( new { name, bytes = template.Length } );
            }

            return outcome switch
            {
                LabelSaveOutcome.AlreadyExists => Results.Json(
                    new { error = $"There is already a label called \'{name}\'. Delete that one first, or use another name." }, statusCode: 409 ),
                LabelSaveOutcome.NoCodeToken => Results.Json( new
                {
                    error = "Those fields are not in the captured label, so it would have nowhere to put a security code."
                }, statusCode: 400 ),
                LabelSaveOutcome.NotZpl => Results.Json(
                    new { error = "What was captured does not look like ZPL." }, statusCode: 400 ),
                LabelSaveOutcome.TooLarge => Results.Json(
                    new { error = "That label is too large." }, statusCode: 400 ),
                _ => Results.Json( new { error = "That label could not be stored." }, statusCode: 400 )
            };
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
/// A request to print a stack of blank labels. Every field is optional on the
/// wire and checked here, so a hand-written or half-filled request fails with
/// a sentence rather than an exception.
///
/// <c>Quantity</c> is always a number of copies, never of labels: 500 with
/// three labels ticked prints 1,500 labels.
/// </summary>
/// <summary>
/// A request to draw one copy's worth of labels. The code is generated from the
/// mode rather than supplied, so a preview cannot be used to render arbitrary
/// text onto a label.
/// </summary>
internal record LabelPreviewRequest( string[]? Names, string? Mode, int? CodeLength, string? Start, string? Prefix );

/// <summary>Which port to wait on for a label from Rock.</summary>
internal record CaptureArmRequest( int? Port );

/// <summary>
/// Saving a captured label. <c>Fields</c> are the positions of the fields that
/// hold the security code, picked from the list the capture reports. More than
/// one is normal - a receipt torn in half carries the code on both halves.
/// </summary>
internal record CaptureSaveRequest( string? Name, int[]? Fields );

internal record BlankPrintRequest(
    string? Address,
    string[]? Labels,
    int? Quantity,
    string? Mode,
    int? CodeLength,
    string? Start,
    string? Prefix,
    bool? HasCutter,
    bool? Test );

/// <summary>
/// Notification settings from the web UI. <c>Secret</c> is null when the caller
/// is leaving the stored secret alone, and empty when clearing it.
/// </summary>
internal record NotificationSettingsRequest( bool Enabled, string? Url, string? Secret, int? CooldownMinutes );
