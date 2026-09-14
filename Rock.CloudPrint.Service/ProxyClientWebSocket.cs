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
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;

using Rock.CloudPrint.Shared;

namespace Rock.CloudPrint.Service;

/// <summary>
/// Client side implementation of the proxy web socket. This handles all
/// low-level communication between the proxy service and the Rock server.
/// </summary>
class ProxyClientWebSocket : ProxyWebSocket
{
    /// <summary>
    /// The instance to use when logging messages.
    /// </summary>
    private readonly ILogger _logger;

    /// <summary>
    /// The shared proxy status instance.
    /// </summary>
    private readonly ProxyStatus _status;

    /// <summary>
    /// Records the outcome of each print attempt for the web UI.
    /// </summary>
    private readonly PrintMetrics _metrics;

    /// <summary>
    /// How long an attempt may take before Rock is assumed to have given up.
    /// </summary>
    private readonly int _slowPrintMilliseconds;

    /// <summary>
    /// Reports print problems back to the Rock server.
    /// </summary>
    private readonly FailureNotifier _notifier;

    /// <summary>
    /// How long the watchdog waits before treating silence as a dead link.
    /// Held only so the observed ping interval can be logged beside it.
    /// </summary>
    private readonly int _idleTimeoutSeconds;

    /// <summary>
    /// How long a printer has to accept the connection and take the data.
    /// </summary>
    private readonly int _printerConnectTimeoutSeconds;

    /// <summary>
    /// One gate per printer address, so labels for the same printer are sent
    /// one at a time while different printers proceed independently.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> _printerGates = new();

    /// <summary>
    /// When the last ping arrived, used to measure the server's ping interval.
    /// </summary>
    private DateTimeOffset? _lastPingReceivedAt;

    /// <summary>
    /// Whether the measured ping interval has been reported once already.
    /// </summary>
    private bool _pingIntervalReported;

    /// <summary>
    /// Initializes a new instance of the <see cref="ProxyClientWebSocket"/> class.
    /// </summary>
    /// <param name="socket">The <see cref="WebSocket"/> used for communication.</param>
    /// <param name="logger">The instance used for logging.</param>
    /// <param name="status">The shared proxy status instance.</param>
    /// <param name="metrics">Records the outcome of each print attempt.</param>
    /// <param name="slowPrintMilliseconds">The point past which Rock is assumed to have stopped waiting.</param>
    /// <param name="notifier">Reports print problems back to the Rock server.</param>
    public ProxyClientWebSocket( WebSocket socket, ILogger logger, ProxyStatus status, PrintMetrics metrics, int slowPrintMilliseconds, FailureNotifier notifier, int idleTimeoutSeconds, int printerConnectTimeoutSeconds )
        : base( socket )
    {
        _printerConnectTimeoutSeconds = printerConnectTimeoutSeconds;
        _logger = logger;
        _status = status;
        _metrics = metrics;
        _slowPrintMilliseconds = slowPrintMilliseconds;
        _notifier = notifier;
        _idleTimeoutSeconds = idleTimeoutSeconds;
    }

    /// <inheritdoc/>
    protected override async Task OnMessageAsync( CloudPrintMessage message, ReadOnlyMemory<byte> extraData, CancellationToken cancellationToken )
    {
        // Any message at all proves the link is alive. The watchdog in
        // ProxyWorker has nothing else to go on: a half-open connection
        // produces no close frame, so the receive loop simply waits forever.
        _status.RecordMessageReceived();

        if ( message is CloudPrintMessagePing pingMessage )
        {
            // Pings are the only traffic on an idle connection, so their
            // interval is what the watchdog timeout has to clear. Logged once
            // at Information when first measured, then at Debug, because a ping
            // every few seconds would otherwise bury everything else.
            var now = DateTimeOffset.Now;

            if ( _lastPingReceivedAt.HasValue )
            {
                var interval = now - _lastPingReceivedAt.Value;

                if ( !_pingIntervalReported )
                {
                    _pingIntervalReported = true;

                    _logger.LogInformation( "Server ping interval is about {seconds:0.#}s. The connection watchdog gives up after {timeout}s of silence.",
                        interval.TotalSeconds, _idleTimeoutSeconds );
                }
                else
                {
                    _logger.LogDebug( "Ping from server, {seconds:0.#}s since the last one.", interval.TotalSeconds );
                }
            }

            _lastPingReceivedAt = now;

            var pongResponse = new CloudPrintResponsePing
            {
                RequestedAt = pingMessage.SentAt,
                RespondedAt = now
            };

            await PostResponseAsync( message, pongResponse, cancellationToken );
        }
        else if ( message is CloudPrintMessagePrint printMessage )
        {
            // Printing runs on its own task rather than inline.
            //
            // The receive loop awaits this handler, so a print that blocks
            // blocks the loop, and the loop is the only thing reading the
            // socket. One printer that is switched off therefore used to stop
            // every other printer in the building: its connect attempt sat
            // there unanswered while labels for healthy printers queued behind
            // it, undelivered and unlogged.
            //
            // The buffer the caller hands us is reused for the next message, so
            // the data is copied before it leaves this method.
            var payload = extraData.ToArray();

            _ = Task.Run( () => PrintAsync( message, printMessage, payload, cancellationToken ), cancellationToken );
        }
    }

    /// <summary>
    /// Records the outcome of a print attempt and logs anything the operator
    /// would not otherwise learn from the existing success and failure lines.
    /// </summary>
    /// <param name="address">The printer address.</param>
    /// <param name="labelCount">The number of labels in the attempt.</param>
    /// <param name="printResult">The value returned to the server: empty on success, otherwise the failure reason.</param>
    /// <param name="elapsed">How long the attempt took.</param>
    private void RecordPrintResult( string address, int labelCount, string printResult, TimeSpan elapsed )
    {
        var printEvent = _metrics.Record( address: address,
            labelCount: labelCount,
            reason: printResult,
            elapsed: elapsed,
            slowThresholdMilliseconds: _slowPrintMilliseconds );

        // Decides whether this is worth reporting and, if so, dispatches it to a
        // background task. It must not be awaited: this runs inline on the
        // socket's receive loop, so blocking here stops the proxy answering the
        // server at all - exactly when something is already wrong.
        _notifier.OnPrintResult( printEvent, labelCount );

        if ( !printEvent.ExceededRockTimeout )
        {
            return;
        }

        // Rock's check-in kiosk stops waiting after a few seconds and shows its
        // own generic timeout message. Anything that lands after that point was
        // never seen by the operator, whether it eventually worked or not, so
        // it is worth calling out separately from a plain success or failure.
        if ( printEvent.Succeeded )
        {
            _logger.LogWarning( "Printed to {address} after {elapsed}ms, too late for the server to report it. Check-in most likely showed a timeout even though the labels printed.",
                address, printEvent.ElapsedMilliseconds );
        }
        else
        {
            _logger.LogWarning( "Print to {address} failed after {elapsed}ms, too late for the server to report it. Check-in most likely showed a timeout rather than this reason.",
                address, printEvent.ElapsedMilliseconds );
        }
    }

    /// <summary>
    /// Handles one print request, off the receive loop.
    ///
    /// Work for different printers runs concurrently, which is the point: a
    /// printer that is off must not delay one that is working. Work for the
    /// same printer is serialised, because two labels interleaved down one
    /// connection produce garbage.
    /// </summary>
    private async Task PrintAsync( CloudPrintMessage message, CloudPrintMessagePrint printMessage, byte[] payload, CancellationToken cancellationToken )
    {
        var labelCount = printMessage.Count > 0 ? printMessage.Count : 1;
        var address = printMessage.Address ?? string.Empty;

        _status.AddLabels( labelCount );

        var gate = _printerGates.GetOrAdd( address, _ => new SemaphoreSlim( 1, 1 ) );

        try
        {
            await gate.WaitAsync( cancellationToken );

            try
            {
                var startedAt = Stopwatch.GetTimestamp();
                var printResult = await SendPrintDataAsync( address, payload, cancellationToken );
                var elapsed = Stopwatch.GetElapsedTime( startedAt );

                // Respond first so our own bookkeeping never delays the server.
                await PostResponseAsync( message, printResult, cancellationToken );

                RecordPrintResult( address, labelCount, printResult, elapsed );
            }
            finally
            {
                gate.Release();
            }
        }
        catch ( Exception ex )
        {
            // Nothing above may escape: this runs on its own task, where an
            // unhandled exception would take the process down rather than fail
            // one label.
            _logger.LogError( ex, "Print handling failed for {address}.", address );
        }
    }

    /// <summary>
    /// Sends the requested data to the printer to be printed.
    /// </summary>
    /// <param name="address">The address (and optional port) to connect to.</param>
    /// <param name="data">The data to be sent.</param>
    /// <param name="cancellationToken">A token that indicates if the operation should be cancelled.</param>
    /// <returns>An empty string if everything worked or an error message.</returns>
    private async Task<string> SendPrintDataAsync( string address, ReadOnlyMemory<byte> data, CancellationToken cancellationToken )
    {
        // Bounded, because an unreachable printer otherwise sits here for as
        // long as the operating system is willing to retry - far longer than
        // the server waits, and long enough to hold up the next label for this
        // same printer. The error text is left exactly as the socket reports
        // it, since that string is what the server displays at check-in.
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource( cancellationToken );

        if ( _printerConnectTimeoutSeconds > 0 )
        {
            timeoutSource.CancelAfter( TimeSpan.FromSeconds( _printerConnectTimeoutSeconds ) );
        }

        try
        {
            using var socket = await OpenSocketAsync( address, timeoutSource.Token );
            using var ns = new NetworkStream( socket );

            await ns.WriteAsync( data, timeoutSource.Token );

            _logger.LogInformation( "Printed {bytes} bytes to {address}.", data.Length, address );

            return string.Empty;
        }
        catch ( OperationCanceledException ) when ( !cancellationToken.IsCancellationRequested )
        {
            var message = $"Printer did not respond within {_printerConnectTimeoutSeconds} seconds.";

            _logger.LogError( "Failed to print to device {address}. {message}", address, message );

            return message;
        }
        catch ( Exception ex )
        {
            _logger.LogError( ex, "Failed to print to device {address}.", address );

            return ex.Message;
        }
    }

    /// <summary>
    /// Opens a socket to the IP Address.
    /// </summary>
    /// <param name="ipAddress">The ip address and optional port number.</param>
    /// <param name="cancellationToken">The token that lets us know when to abort the connection attempt.</param>
    /// <returns>A new isntance of <see cref="Socket"/>.</returns>
    private static async Task<Socket> OpenSocketAsync( string ipAddress, CancellationToken cancellationToken )
    {
        // Parsing lives in PrinterAddress so the web UI's connection test runs
        // exactly this code rather than its own copy of it. Behaviour, including
        // which exceptions escape and what they say, is unchanged.
        var printerEndpoint = PrinterAddress.Parse( ipAddress ).ToEndPoint();
        var socket = new Socket( AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp );

        await socket.ConnectAsync( printerEndpoint, cancellationToken );

        return socket;
    }
}
