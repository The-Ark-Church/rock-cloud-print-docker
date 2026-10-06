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
    /// Reports print problems to the Rock server. Never blocks the print path.
    /// </summary>
    private readonly FailureNotifier _notifier;

    /// <summary>
    /// The most recent print queued for each printer address. Each new print
    /// for an address waits for the one before it, so labels for the same
    /// printer are sent one at a time and in the order the server sent them,
    /// while different printers proceed independently. Guarded by locking on
    /// itself.
    ///
    /// <para>
    /// The order is fixed here, on the receive loop, as each request arrives.
    /// Starting each print on its own task and having it take a lock there
    /// would leave the order to whichever task the thread pool ran first.
    /// </para>
    ///
    /// <para>
    /// Static because a new instance of this class is created for every
    /// connection to the server. A print still waiting on a sleeping printer
    /// when the connection is rebuilt carries on, and the next label for that
    /// printer must still queue behind it rather than open a second connection
    /// to it alongside.
    /// </para>
    /// </summary>
    private static readonly Dictionary<string, Task> _printerQueues = new( StringComparer.OrdinalIgnoreCase );

    /// <summary>
    /// Initializes a new instance of the <see cref="ProxyClientWebSocket"/> class.
    /// </summary>
    /// <param name="socket">The <see cref="WebSocket"/> used for communication.</param>
    /// <param name="logger">The instance used for logging.</param>
    /// <param name="status">The shared proxy status instance.</param>
    /// <param name="metrics">Records the outcome of each print attempt.</param>
    /// <param name="slowPrintMilliseconds">The point past which Rock is assumed to have stopped waiting.</param>
    /// <param name="notifier">Reports print problems to the Rock server.</param>
    public ProxyClientWebSocket( WebSocket socket, ILogger logger, ProxyStatus status, PrintMetrics metrics, int slowPrintMilliseconds, FailureNotifier notifier )
        : base( socket )
    {
        _logger = logger;
        _status = status;
        _metrics = metrics;
        _slowPrintMilliseconds = slowPrintMilliseconds;
        _notifier = notifier;
    }

    /// <inheritdoc/>
    protected override async Task OnMessageAsync( CloudPrintMessage message, ReadOnlyMemory<byte> extraData, CancellationToken cancellationToken )
    {
        if ( message is CloudPrintMessagePing pingMessage )
        {
            var pongResponse = new CloudPrintResponsePing
            {
                RequestedAt = pingMessage.SentAt,
                RespondedAt = DateTimeOffset.Now
            };

            await PostResponseAsync( message, pongResponse, cancellationToken );
        }
        else if ( message is CloudPrintMessagePrint printMessage )
        {
            // Printing runs on its own task rather than inline.
            //
            // The receive loop awaits this handler, and the receive loop is the
            // only thing reading the socket. Printed inline, a printer that is
            // asleep or switched off - whose connect attempt can sit unanswered
            // for two minutes - stopped every other printer in the building,
            // and stopped the proxy answering the server's pings, until the
            // server gave up on the connection.
            //
            // There is deliberately no time limit here. A printer waking from
            // sleep can take well over a minute to answer, and a guessed limit
            // on this path was rolled back once already for failing exactly
            // that printer. A slow printer now delays only its own labels.
            //
            // The data is copied so it cannot depend on how long the caller
            // keeps its buffer alive.
            // Rock sends a null address for a printer device with no IP set.
            // The queue below is keyed by address, and a null key would throw
            // here on the receive loop and drop the connection for every
            // printer. As an empty string it fails to parse in PrintAsync and
            // that one print is answered with the reason, as before.
            printMessage.Address ??= string.Empty;

            var payload = extraData.ToArray();
            var receivedAt = Stopwatch.GetTimestamp();
            var labelCount = printMessage.Count > 0 ? printMessage.Count : 1;

            _status.AddLabels( labelCount );

            lock ( _printerQueues )
            {
                var previous = _printerQueues.TryGetValue( printMessage.Address, out var queued )
                    ? queued
                    : Task.CompletedTask;

                _printerQueues[printMessage.Address] = Task.Run( () => PrintAsync( previous, printMessage, labelCount, payload, receivedAt, cancellationToken ) );
            }
        }
    }

    /// <summary>
    /// Handles one print request, off the receive loop, once the print queued
    /// ahead of it for the same printer has finished.
    /// </summary>
    /// <param name="previous">The print queued ahead of this one for the same printer.</param>
    /// <param name="printMessage">The print request.</param>
    /// <param name="labelCount">The number of labels in the request.</param>
    /// <param name="payload">The data to send to the printer.</param>
    /// <param name="receivedAt">When the request arrived, as a <see cref="Stopwatch"/> timestamp.</param>
    /// <param name="cancellationToken">A token that indicates if the operation should be cancelled.</param>
    private async Task PrintAsync( Task previous, CloudPrintMessagePrint printMessage, int labelCount, byte[] payload, long receivedAt, CancellationToken cancellationToken )
    {
        var address = printMessage.Address;

        try
        {
            // Never throws: the task ahead is another call to this method, which
            // catches everything.
            await previous;

            var printResult = await SendPrintDataAsync( address, payload, cancellationToken );

            // Measured from when the request arrived, not from when this
            // printer's turn came, because time spent queued behind an
            // earlier label is time the server spent waiting too.
            var elapsed = Stopwatch.GetElapsedTime( receivedAt );

            try
            {
                // Respond first so our own bookkeeping never delays the server.
                await PostResponseAsync( printMessage, printResult, cancellationToken );
            }
            finally
            {
                // In a finally because that response write throws when the server
                // has already aborted the connection - which is exactly when this
                // attempt most needs recording and reporting. Without it such a
                // print was counted neither as failed nor as slow, the label
                // total disagreed with the failure total, and no notification
                // was sent for the very case most worth hearing about.
                RecordPrintResult( address, labelCount, printResult, elapsed );
            }
        }
        catch ( Exception ex )
        {
            // Nothing may escape: this runs on its own task, where an exception
            // would go unobserved. The usual one is the response write failing
            // because the connection it arrived on has since closed - the label
            // has already been printed and recorded by then.
            _logger.LogError( ex, "Print handling failed for {address}.", address );
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
        PrintEvent printEvent;

        try
        {
            printEvent = _metrics.Record( address: address,
                labelCount: labelCount,
                reason: printResult,
                elapsed: elapsed,
                slowThresholdMilliseconds: _slowPrintMilliseconds );

            // Decides for itself whether this is worth reporting, and dispatches
            // to a background task. Never awaited: anything slow here delays
            // the next label queued for this printer.
            _notifier.OnPrintResult( printEvent, labelCount );
        }
        catch ( Exception ex )
        {
            // Bookkeeping must never take down the receive loop, and - because
            // this runs in a finally - must never replace the exception that
            // brought us here.
            _logger.LogError( ex, "Failed to record the outcome of a print to {address}.", address );

            return;
        }

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
    /// Sends the requested data to the printer to be printed.
    /// </summary>
    /// <param name="address">The address (and optional port) to connect to.</param>
    /// <param name="data">The data to be sent.</param>
    /// <param name="cancellationToken">A token that indicates if the operation should be cancelled.</param>
    /// <returns>An empty string if everything worked or an error message.</returns>
    private async Task<string> SendPrintDataAsync( string address, ReadOnlyMemory<byte> data, CancellationToken cancellationToken )
    {
        try
        {
            using var socket = await OpenSocketAsync( address, cancellationToken );
            using var ns = new NetworkStream( socket );

            await ns.WriteAsync( data, cancellationToken );

            // Logged at Information, not Debug: this is the only record that a label
            // actually reached a printer. At Debug it never appeared at the default
            // log level, so print failures were visible but successes were not.
            _logger.LogInformation( "Printed {bytes} bytes to {address}.", data.Length, address );

            return string.Empty;
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

        try
        {
            await socket.ConnectAsync( printerEndpoint, cancellationToken );
        }
        catch
        {
            // The caller never gets a socket that failed to connect, so it
            // cannot dispose it. Without this, every unreachable printer held
            // a file handle until the garbage collector got round to it.
            socket.Dispose();

            throw;
        }

        return socket;
    }
}
