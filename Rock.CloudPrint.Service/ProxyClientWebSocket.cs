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
    /// Initializes a new instance of the <see cref="ProxyClientWebSocket"/> class.
    /// </summary>
    /// <param name="socket">The <see cref="WebSocket"/> used for communication.</param>
    /// <param name="logger">The instance used for logging.</param>
    /// <param name="status">The shared proxy status instance.</param>
    /// <param name="metrics">Records the outcome of each print attempt.</param>
    /// <param name="slowPrintMilliseconds">The point past which Rock is assumed to have stopped waiting.</param>
    public ProxyClientWebSocket( WebSocket socket, ILogger logger, ProxyStatus status, PrintMetrics metrics, int slowPrintMilliseconds )
        : base( socket )
    {
        _logger = logger;
        _status = status;
        _metrics = metrics;
        _slowPrintMilliseconds = slowPrintMilliseconds;
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
            var labelCount = printMessage.Count > 0 ? printMessage.Count : 1;

            _status.AddLabels( labelCount );

            var startedAt = Stopwatch.GetTimestamp();
            var printResult = await SendPrintDataAsync( printMessage.Address, extraData, cancellationToken );
            var elapsed = Stopwatch.GetElapsedTime( startedAt );

            // Respond first so our own bookkeeping never delays the server.
            await PostResponseAsync( message, printResult, cancellationToken );

            RecordPrintResult( printMessage.Address, labelCount, printResult, elapsed );
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

        await socket.ConnectAsync( printerEndpoint, cancellationToken );

        return socket;
    }
}
