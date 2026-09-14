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
using System.Text.Json.Serialization;

using Microsoft.Extensions.Options;

namespace Rock.CloudPrint.Service;

/// <summary>
/// The outcome of one attempt to tell the Rock server about a print problem.
/// Kept so the dashboard can say what went wrong rather than "it failed".
/// </summary>
internal class NotificationAttempt
{
    public DateTimeOffset At { get; init; }

    /// <summary>The event kind: <c>failed</c>, <c>slow</c> or <c>test</c>.</summary>
    public string Event { get; init; } = string.Empty;

    /// <summary>The printer the notification was about.</summary>
    public string Printer { get; init; } = string.Empty;

    /// <summary>
    /// Whether the server accepted it. Deliberately narrow: HTTP 202 <b>and</b> a
    /// body saying <c>accepted: true</c>. A bare 200 is what a Lava error returns,
    /// so treating any 2xx as success would report a broken webhook as healthy.
    /// </summary>
    public bool Delivered { get; init; }

    /// <summary>The HTTP status, or zero when the request never completed.</summary>
    public int StatusCode { get; init; }

    /// <summary>A sentence naming the fault, suitable for the dashboard.</summary>
    public string Outcome { get; init; } = string.Empty;

    /// <summary>
    /// Extra detail: the workflow's own error when it started but something in it
    /// failed, or the transport error when the request never arrived.
    /// </summary>
    public string Detail { get; init; } = string.Empty;
}

/// <summary>
/// Reports print failures to the Rock server so somebody can be told.
///
/// Two things this must never do: block the print path, and claim success it did
/// not have. The print path is inline on the socket's receive loop, so anything
/// slow here stops the proxy answering the server; every send is therefore
/// dispatched to a background task and never awaited by the caller.
/// </summary>
internal class FailureNotifier
{
    /// <summary>What is known about one printer, for debouncing and counting.</summary>
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

    private static readonly JsonSerializerOptions PayloadOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

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
    /// dashboard shows a banner while this is a failure.
    /// </summary>
    public NotificationAttempt? LastAttempt
    {
        get { lock ( _lock ) { return _lastAttempt; } }
    }

    /// <summary>Printers that have failed and not printed since.</summary>
    public int PrintersFailing
    {
        get { lock ( _lock ) { return _printers.Values.Count( p => p.IsFailing ); } }
    }

    /// <summary>
    /// Called for every completed print attempt. Updates what is known about the
    /// printer, decides whether this is worth reporting, and if so dispatches the
    /// notification without waiting for it.
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

            eventKind = ClassifyAndUpdate( state, printEvent, options.NotificationCooldownMinutes );

            consecutiveFailures = state.ConsecutiveFailures;
            printersFailing = _printers.Values.Count( p => p.IsFailing );
        }

        if ( eventKind == null || !options.NotificationsEnabled )
        {
            return;
        }

        var payload = BuildPayload( eventKind, printEvent, labelCount, consecutiveFailures, printersFailing );

        // Never awaited. See the class summary.
        _ = Task.Run( () => SendAsync( payload, CancellationToken.None ) );
    }

    /// <summary>
    /// Updates the printer's state and returns the event kind worth reporting,
    /// or <c>null</c> when there is nothing new to say.
    /// </summary>
    private static string? ClassifyAndUpdate( PrinterState state, PrintEvent printEvent, int cooldownMinutes )
    {
        var now = DateTimeOffset.Now;
        var cooldown = TimeSpan.FromMinutes( Math.Max( 0, cooldownMinutes ) );

        if ( !printEvent.Succeeded )
        {
            state.IsFailing = true;
            state.ConsecutiveFailures++;

            if ( IsInCooldown( state.FailureReportedAt, now, cooldown ) )
            {
                return null;
            }

            state.FailureReportedAt = now;

            return "failed";
        }

        // It printed, so the printer is not failing and the run of failures ends -
        // whether or not it was late.
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

        // Printed, but too late for the server to report it - the operator saw a
        // timeout. Worth saying, on its own cooldown.
        //
        // This deliberately does NOT clear the cooldowns. A slow success is still
        // a success, so clearing them would let the very next slow print report
        // again, and a persistently slow printer would notify on every label.
        if ( IsInCooldown( state.SlowReportedAt, now, cooldown ) )
        {
            return null;
        }

        state.SlowReportedAt = now;

        return "slow";
    }

    private static bool IsInCooldown( DateTimeOffset? lastReportedAt, DateTimeOffset now, TimeSpan cooldown )
    {
        return lastReportedAt.HasValue && now - lastReportedAt.Value < cooldown;
    }

    private Dictionary<string, object?> BuildPayload( string eventKind, PrintEvent printEvent,
        int labelCount, int consecutiveFailures, int printersFailing )
    {
        var options = _options.CurrentValue;

        return new Dictionary<string, object?>
        {
            ["schema"] = 1,
            ["event"] = eventKind,
            ["printer"] = printEvent.Address,
            ["reason"] = printEvent.Reason,
            ["labelCount"] = labelCount,
            ["elapsedMs"] = printEvent.ElapsedMilliseconds,
            ["occurredAt"] = printEvent.Timestamp.ToUniversalTime().ToString( "yyyy-MM-ddTHH:mm:ssZ" ),
            ["proxyName"] = string.IsNullOrWhiteSpace( options.Name ) ? Environment.MachineName : options.Name,
            ["proxyId"] = options.Id,
            ["proxyVersion"] = _proxyVersion,
            ["consecutiveFailures"] = consecutiveFailures,
            ["printersFailing"] = printersFailing
        };
    }

    /// <summary>
    /// Sends a synthetic notification so the whole chain can be proved at
    /// configuration time rather than during an outage. Unlike the automatic
    /// path this one is awaited, because a person is waiting for the answer.
    /// </summary>
    public Task<NotificationAttempt> SendTestAsync( CancellationToken cancellationToken )
    {
        var options = _options.CurrentValue;

        var payload = new Dictionary<string, object?>
        {
            ["schema"] = 1,
            ["event"] = "test",
            ["printer"] = "test",
            ["reason"] = string.Empty,
            ["labelCount"] = 0,
            ["elapsedMs"] = 0,
            ["occurredAt"] = DateTimeOffset.UtcNow.ToString( "yyyy-MM-ddTHH:mm:ssZ" ),
            ["proxyName"] = string.IsNullOrWhiteSpace( options.Name ) ? Environment.MachineName : options.Name,
            ["proxyId"] = options.Id,
            ["proxyVersion"] = _proxyVersion,
            ["consecutiveFailures"] = 0,
            ["printersFailing"] = 0
        };

        return SendAsync( payload, cancellationToken );
    }

    private async Task<NotificationAttempt> SendAsync( Dictionary<string, object?> payload, CancellationToken cancellationToken )
    {
        var options = _options.CurrentValue;
        var eventKind = payload["event"] as string ?? string.Empty;
        var printer = payload["printer"] as string ?? string.Empty;

        if ( string.IsNullOrWhiteSpace( options.NotificationUrl ) )
        {
            return Record( eventKind, printer, false, 0,
                "No webhook URL is configured.", string.Empty );
        }

        if ( !Uri.TryCreate( options.NotificationUrl, UriKind.Absolute, out var uri ) )
        {
            return Record( eventKind, printer, false, 0,
                "The webhook URL is not a valid address.", string.Empty );
        }

        // The secret travels in a header, over a path that leaves the building.
        // Downgrading to cleartext silently would be worse than not sending.
        if ( uri.Scheme != Uri.UriSchemeHttps )
        {
            return Record( eventKind, printer, false, 0,
                "The webhook URL is not HTTPS, so the secret was not sent.", string.Empty );
        }

        try
        {
            var client = _httpClientFactory.CreateClient( "notifications" );
            client.Timeout = TimeSpan.FromSeconds( 10 );

            var content = new StringContent( JsonSerializer.Serialize( payload, PayloadOptions ),
                Encoding.UTF8, "application/json" );

            // Rock's Lava webhook handler decides whether to parse the body with
            // an EXACT string comparison: request.ContentType == "application/json".
            // StringContent appends "; charset=utf-8", which fails that test, so
            // Rock leaves Body empty and every field in the webhook reads as null.
            // Setting the header without the charset parameter is what makes the
            // payload readable at the far end. Verified: with the charset the
            // webhook returns 400, without it 202.
            //
            // Do not simplify this back to the StringContent constructor alone.
            content.Headers.ContentType = new MediaTypeHeaderValue( "application/json" );

            using var request = new HttpRequestMessage( HttpMethod.Post, uri ) { Content = content };

            if ( !string.IsNullOrWhiteSpace( options.NotificationSecret ) )
            {
                // A custom header, not Authorization: Rock strips Authorization
                // and Cookie before the webhook's Lava can see them, so the check
                // would silently never match.
                request.Headers.TryAddWithoutValidation( "X-CloudPrint-Token", options.NotificationSecret );
            }

            using var response = await client.SendAsync( request, cancellationToken );

            var body = await response.Content.ReadAsStringAsync( cancellationToken );

            return Evaluate( eventKind, printer, ( int ) response.StatusCode, body );
        }
        catch ( Exception ex )
        {
            return Record( eventKind, printer, false, 0,
                "The Rock server could not be reached.", ex.Message );
        }
    }

    /// <summary>
    /// Decides whether the server actually accepted the notification, and says
    /// what went wrong in terms somebody can act on.
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
            // Not JSON. Non-2xx bodies are replaced by the web server, so this is
            // expected for every failure - the status code carries the meaning.
        }

        if ( statusCode == 202 && accepted )
        {
            return Record( eventKind, printer, true, statusCode, "Accepted by Rock.", workflowError );
        }

        // 3xx means Rock redirected us, which it does when no webhook matches the
        // URL - it sends the caller to its own 404 page. Redirects are not
        // followed, so this is visible rather than showing up as a 200.
        if ( statusCode >= 300 && statusCode < 400 )
        {
            return Record( eventKind, printer, false, statusCode,
                "No webhook in Rock matched that URL, so Rock redirected the request away. Check the webhook URL, and that the webhook still exists.",
                string.Empty );
        }

        var outcome = statusCode switch
        {
            200 => "Rock returned 200 instead of 202, which means the webhook's Lava template failed.",
            202 => "Rock returned 202 but did not confirm it was accepted.",
            400 => "Rock rejected the payload (400). This is a proxy bug, not a configuration problem.",
            401 => "Rock rejected the notification secret (401). It does not match the value held in Rock.",
            403 => "Rock rejected this proxy's address (403). Its public IP is not in the allowed list.",
            404 => "No webhook matched that URL (404). Check the URL, or whether the webhook still exists in Rock.",
            500 => "The Rock webhook failed (500). Its Lava template threw, or the workflow type is missing or inactive.",
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

        if ( delivered )
        {
            // A workflow that started and then failed internally is not a failed
            // notification. Rock took it; something downstream misbehaved.
            if ( !string.IsNullOrWhiteSpace( detail ) )
            {
                _logger.LogWarning( "Reported {event} for {printer}, but the Rock workflow reported a problem: {detail}",
                    eventKind, printer, detail );
            }
            else
            {
                _logger.LogInformation( "Reported {event} for {printer} to Rock.", eventKind, printer );
            }
        }
        else
        {
            _logger.LogError( "Could not report {event} for {printer}. {outcome}{detail}",
                eventKind, printer, outcome,
                string.IsNullOrWhiteSpace( detail ) ? string.Empty : " " + detail );
        }

        return attempt;
    }
}
