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
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.Options;

namespace Rock.CloudPrint.Service;

/// <summary>
/// The outcome of one attempt to tell the Rock server about a print problem.
/// Kept so the dashboard can name the fault rather than saying "it failed".
/// </summary>
internal class NotificationAttempt
{
    public DateTimeOffset At { get; init; }

    /// <summary>The event kind: <c>failed</c>, <c>slow</c> or <c>test</c>.</summary>
    public string Event { get; init; } = string.Empty;

    /// <summary>The printer the notification was about.</summary>
    public string Printer { get; init; } = string.Empty;

    /// <summary>
    /// Whether Rock accepted it. Deliberately narrow - HTTP 202 <b>and</b> a body
    /// saying <c>accepted: true</c>. A bare 200 is what Rock returns when the
    /// webhook's template throws, so treating any 2xx as success would report a
    /// broken webhook as a healthy one.
    /// </summary>
    public bool Delivered { get; init; }

    /// <summary>The HTTP status, or zero when the request never completed.</summary>
    public int StatusCode { get; init; }

    /// <summary>A sentence naming the fault, suitable for the dashboard.</summary>
    public string Outcome { get; init; } = string.Empty;

    /// <summary>
    /// Extra detail: the workflow's own error when it started but something
    /// inside it failed, or the transport error when nothing arrived.
    /// </summary>
    public string Detail { get; init; } = string.Empty;
}

/// <summary>
/// Reports print problems to the Rock server so somebody can be told.
///
/// Two rules this must never break. It must not block the print path: prints are
/// handled inline on the socket's receive loop, so anything slow here stops the
/// proxy answering the server at all - every send is dispatched to a background
/// task and never awaited. And it must not claim a success it did not have,
/// because a notification nobody receives is worse than none at all.
/// </summary>
internal class FailureNotifier
{
    /// <summary>What is known about one printer, for debouncing and for counting.</summary>
    private sealed class PrinterState
    {
        /// <summary>When a failure for this printer was last reported.</summary>
        public DateTimeOffset? FailureReportedAt;

        /// <summary>When a slow print for this printer was last reported.</summary>
        public DateTimeOffset? SlowReportedAt;

        /// <summary>Failures since this printer last managed to print anything.</summary>
        public int ConsecutiveFailures;

        /// <summary>Whether the last attempt failed and nothing has printed since.</summary>
        public bool IsFailing;
    }

    private readonly Dictionary<string, PrinterState> _printers = new();
    private readonly object _lock = new();

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptionsMonitor<CloudPrintOptions> _options;
    private readonly ILogger<FailureNotifier> _logger;
    private readonly string _proxyVersion;

    private NotificationAttempt? _lastAttempt;

    public FailureNotifier( IHttpClientFactory httpClientFactory,
        IOptionsMonitor<CloudPrintOptions> options,
        ILogger<FailureNotifier> logger,
        string proxyVersion )
    {
        _httpClientFactory = httpClientFactory;
        _options = options;
        _logger = logger;
        _proxyVersion = proxyVersion;
    }

    /// <summary>
    /// The most recent attempt, or <c>null</c> if none has been made. The
    /// dashboard shows a banner for as long as this is a failure.
    /// </summary>
    public NotificationAttempt? LastAttempt
    {
        get { lock ( _lock ) { return _lastAttempt; } }
    }

    /// <summary>Printers that have failed and have not printed since.</summary>
    public int PrintersFailing
    {
        get { lock ( _lock ) { return CountFailing(); } }
    }

    /// <summary>
    /// Called for every completed print attempt. Updates what is known about that
    /// printer, decides whether this is worth reporting, and if it is, dispatches
    /// the notification without waiting for it.
    /// </summary>
    public void OnPrintResult( PrintEvent printEvent, int labelCount )
    {
        var options = _options.CurrentValue;

        string? eventKind;
        int consecutiveFailures;
        int printersFailing;

        lock ( _lock )
        {
            if ( !_printers.TryGetValue( printEvent.Address, out var state ) )
            {
                state = new PrinterState();
                _printers[printEvent.Address] = state;
            }

            eventKind = Classify( state, printEvent, options );

            consecutiveFailures = state.ConsecutiveFailures;
            printersFailing = CountFailing();
        }

        if ( eventKind == null )
        {
            return;
        }

        var payload = BuildPayload( eventKind, printEvent.Address, printEvent.Reason,
            labelCount, printEvent.ElapsedMilliseconds, printEvent.Timestamp,
            consecutiveFailures, printersFailing );

        // Never awaited. See the class summary - this is the rule that keeps a
        // slow or hanging webhook from stopping the proxy answering the server.
        _ = Task.Run( () => SendAsync( payload, CancellationToken.None ) );
    }

    /// <summary>
    /// Sends a notification on demand so the whole chain can be proved at
    /// configuration time - URL, secret, address allowance, webhook match,
    /// workflow lookup and activation - rather than during an outage. Unlike the
    /// automatic path this one is awaited, because a person is waiting for it.
    /// </summary>
    public Task<NotificationAttempt> SendTestAsync( CancellationToken cancellationToken )
    {
        var payload = BuildPayload( "test", "test", string.Empty, 0, 0,
            DateTimeOffset.Now, 0, 0 );

        return SendAsync( payload, cancellationToken );
    }

    /// <summary>
    /// Updates the printer's state and returns the event kind worth reporting, or
    /// <c>null</c> when there is nothing new to say. Caller holds the lock.
    /// </summary>
    private static string? Classify( PrinterState state, PrintEvent printEvent, CloudPrintOptions options )
    {
        var now = DateTimeOffset.Now;
        var cooldown = TimeSpan.FromMinutes( Math.Max( 0, options.NotificationCooldownMinutes ) );

        if ( !printEvent.Succeeded )
        {
            state.IsFailing = true;
            state.ConsecutiveFailures++;

            return ShouldReport( ref state.FailureReportedAt, now, cooldown, options.NotificationsEnabled )
                ? "failed"
                : null;
        }

        // It printed, so this printer is not failing and the run of failures ends,
        // whether or not the label arrived too late to be any use.
        state.IsFailing = false;
        state.ConsecutiveFailures = 0;

        if ( !printEvent.ExceededRockTimeout )
        {
            // A clean, on-time success. Clear both cooldowns: the printer is
            // healthy, so the next problem of either kind is news again.
            state.FailureReportedAt = null;
            state.SlowReportedAt = null;

            return null;
        }

        // Printed, but too late for the server to still be listening - so the
        // operator saw a timeout even though the label came out. Worth saying, on
        // its own cooldown.
        //
        // This deliberately does NOT clear the cooldowns. A slow success is still
        // a success, and clearing them would let the very next slow print report
        // again, so a persistently slow printer would notify on every label.
        return ShouldReport( ref state.SlowReportedAt, now, cooldown, options.NotificationsEnabled )
            ? "slow"
            : null;
    }

    /// <summary>
    /// Decides whether this event is outside its cooldown, and starts a new
    /// cooldown when it is.
    /// </summary>
    private static bool ShouldReport( ref DateTimeOffset? lastReportedAt, DateTimeOffset now,
        TimeSpan cooldown, bool notificationsEnabled )
    {
        // The printer state above is tracked whether or not notifications are on,
        // so printersFailing and the failure count are already correct the moment
        // somebody switches them on. A cooldown is different: starting one for a
        // notification that was never sent would silently swallow the first real
        // notification after the feature is enabled.
        if ( !notificationsEnabled )
        {
            return false;
        }

        if ( lastReportedAt.HasValue && now - lastReportedAt.Value < cooldown )
        {
            return false;
        }

        lastReportedAt = now;

        return true;
    }

    /// <summary>Printers failing right now. Caller holds the lock.</summary>
    private int CountFailing()
    {
        var failing = 0;

        foreach ( var state in _printers.Values )
        {
            if ( state.IsFailing )
            {
                failing++;
            }
        }

        return failing;
    }

    /// <summary>
    /// Builds the document sent to Rock. The shape is a contract with the Rock
    /// workflow that receives it - fields are not renamed or dropped without
    /// changing that workflow to match.
    /// </summary>
    private Dictionary<string, object?> BuildPayload( string eventKind, string printer, string reason,
        int labelCount, long elapsedMilliseconds, DateTimeOffset occurredAt,
        int consecutiveFailures, int printersFailing )
    {
        var options = _options.CurrentValue;

        return new Dictionary<string, object?>
        {
            ["schema"] = 1,
            ["event"] = eventKind,
            ["printer"] = printer,
            ["reason"] = reason,
            ["labelCount"] = labelCount,
            ["elapsedMs"] = elapsedMilliseconds,
            ["occurredAt"] = occurredAt.ToUniversalTime().ToString( "yyyy-MM-ddTHH:mm:ssZ" ),
            ["proxyName"] = string.IsNullOrWhiteSpace( options.Name ) ? Environment.MachineName : options.Name,
            ["proxyId"] = options.Id,
            ["proxyVersion"] = _proxyVersion,
            ["consecutiveFailures"] = consecutiveFailures,
            ["printersFailing"] = printersFailing
        };
    }

    private async Task<NotificationAttempt> SendAsync( Dictionary<string, object?> payload, CancellationToken cancellationToken )
    {
        var options = _options.CurrentValue;
        var eventKind = payload["event"] as string ?? string.Empty;
        var printer = payload["printer"] as string ?? string.Empty;

        if ( string.IsNullOrWhiteSpace( options.NotificationUrl ) )
        {
            return Record( eventKind, printer, false, 0, "No webhook URL is configured.", string.Empty );
        }

        if ( !Uri.TryCreate( options.NotificationUrl, UriKind.Absolute, out var uri ) )
        {
            return Record( eventKind, printer, false, 0, "The webhook URL is not a valid address.", string.Empty );
        }

        // The secret travels in a request header over a path that leaves the
        // building. Downgrading to cleartext silently would be worse than not
        // sending at all, so refuse rather than send.
        if ( uri.Scheme != Uri.UriSchemeHttps )
        {
            return Record( eventKind, printer, false, 0,
                "The webhook URL is not HTTPS, so the secret was not sent.", string.Empty );
        }

        // No timeout is set on this client, deliberately. The default is used
        // until the round trip has actually been measured against a loaded Rock
        // server. The send is fire-and-forget, so a long wait costs printing
        // nothing - whereas a number picked without measuring the thing being
        // bounded is exactly the mistake that got this feature rolled back once
        // already. Put a constant here only once there is a measurement to write
        // into this comment.
        var client = _httpClientFactory.CreateClient( "notifications" );

        try
        {
            var content = new StringContent( JsonSerializer.Serialize( payload ), Encoding.UTF8, "application/json" );

            // Rock's webhook handler decides whether to parse the body with an
            // exact string comparison against "application/json". StringContent
            // appends "; charset=utf-8", which fails that comparison - Rock then
            // leaves the body unparsed and every field in the webhook reads as
            // empty, with no error anywhere. Setting the header explicitly is
            // what makes the payload readable at the far end.
            //
            // Do not simplify this back to the StringContent constructor alone.
            content.Headers.ContentType = new MediaTypeHeaderValue( "application/json" );

            using var request = new HttpRequestMessage( HttpMethod.Post, uri ) { Content = content };

            if ( !string.IsNullOrWhiteSpace( options.NotificationSecret ) )
            {
                // A custom header, not Authorization: Rock strips Authorization
                // and Cookie before the webhook can see them, so the check at the
                // far end would silently never match.
                request.Headers.TryAddWithoutValidation( "X-CloudPrint-Token", options.NotificationSecret );
            }

            using var response = await client.SendAsync( request, cancellationToken );

            var body = await response.Content.ReadAsStringAsync( cancellationToken );

            return Evaluate( eventKind, printer, ( int ) response.StatusCode, body );
        }
        catch ( TaskCanceledException ex ) when ( !cancellationToken.IsCancellationRequested )
        {
            // HttpClient surfaces its own timeout as a cancellation. Kept separate
            // from the case below because "did not answer" and "could not be
            // reached" send somebody to look in two different places.
            return Record( eventKind, printer, false, 0,
                $"Rock did not answer within {client.Timeout.TotalSeconds:0} seconds.", ex.Message );
        }
        catch ( Exception ex )
        {
            return Record( eventKind, printer, false, 0, "The Rock server could not be reached.", ex.Message );
        }
    }

    /// <summary>
    /// Decides whether Rock actually accepted the notification, and says what went
    /// wrong in terms somebody can act on.
    /// </summary>
    private NotificationAttempt Evaluate( string eventKind, string printer, int statusCode, string body )
    {
        var accepted = false;
        var workflowError = string.Empty;

        try
        {
            using var document = JsonDocument.Parse( body );

            if ( document.RootElement.TryGetProperty( "accepted", out var acceptedElement ) )
            {
                accepted = acceptedElement.ValueKind == JsonValueKind.True;
            }

            if ( document.RootElement.TryGetProperty( "workflowError", out var errorElement ) )
            {
                workflowError = errorElement.GetString() ?? string.Empty;
            }
        }
        catch ( JsonException )
        {
            // Not JSON. Expected for every failure: the web server replaces the
            // body of a non-2xx response, so the status code carries the meaning.
        }

        if ( statusCode == 202 && accepted )
        {
            // A workflow that started and then failed inside itself is not a
            // failed notification - Rock took it, and something downstream
            // misbehaved. Carried as detail, not as a failure.
            return Record( eventKind, printer, true, statusCode, "Accepted by Rock.", workflowError );
        }

        // Redirects are not followed, so this stays visible. Rock sends the caller
        // to its own 404 page when no webhook matches the URL, and that page
        // answers 200 - following it would make a wrong URL look like a broken
        // template instead.
        if ( statusCode >= 300 && statusCode < 400 )
        {
            return Record( eventKind, printer, false, statusCode,
                "No webhook in Rock matched that URL, so Rock redirected the request away. Check the webhook URL, and that the webhook still exists.",
                string.Empty );
        }

        var outcome = statusCode switch
        {
            200 => "Rock returned 200 instead of 202, which means the webhook's template failed.",
            202 => "Rock returned 202 but did not confirm it was accepted.",
            400 => "Rock rejected the payload (400). That is a proxy bug, not a configuration problem.",
            401 => "Rock rejected the notification secret (401). It does not match the value held in Rock.",
            403 => "Rock rejected this proxy's address (403). Its public address is not in the allowed list.",
            404 => "No webhook matched that URL (404). Check the URL, or whether the webhook still exists in Rock.",
            500 => "The Rock webhook failed (500). Its template threw, or the workflow type is missing or inactive.",
            _ => $"Rock returned {statusCode}."
        };

        return Record( eventKind, printer, false, statusCode, outcome, string.Empty );
    }

    private NotificationAttempt Record( string eventKind, string printer, bool delivered,
        int statusCode, string outcome, string detail )
    {
        var attempt = new NotificationAttempt
        {
            At = DateTimeOffset.Now,
            Event = eventKind,
            Printer = printer,
            Delivered = delivered,
            StatusCode = statusCode,
            Outcome = outcome,
            Detail = detail
        };

        lock ( _lock )
        {
            _lastAttempt = attempt;
        }

        if ( !delivered )
        {
            _logger.LogError( "Could not report {event} for {printer}. {outcome}{detail}",
                eventKind, printer, outcome,
                string.IsNullOrWhiteSpace( detail ) ? string.Empty : " " + detail );
        }
        else if ( !string.IsNullOrWhiteSpace( detail ) )
        {
            _logger.LogWarning( "Reported {event} for {printer}, but the Rock workflow reported a problem: {detail}",
                eventKind, printer, detail );
        }
        else
        {
            _logger.LogInformation( "Reported {event} for {printer} to Rock.", eventKind, printer );
        }

        return attempt;
    }
}
