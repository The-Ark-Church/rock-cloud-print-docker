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
using System.Net.Sockets;

namespace Rock.CloudPrint.Service;

/// <summary>
/// The outcome of a printer connection test.
/// </summary>
internal class PrinterTestResult
{
    /// <summary>Whether the printer could be reached.</summary>
    public bool Ok { get; init; }

    /// <summary>The address as supplied.</summary>
    public string Address { get; init; } = string.Empty;

    /// <summary>The port used, or zero when the address could not be parsed.</summary>
    public int Port { get; init; }

    /// <summary>How long the attempt took.</summary>
    public long ElapsedMilliseconds { get; init; }

    /// <summary>Bytes written to the printer. Zero for a connection-only test.</summary>
    public int BytesSent { get; init; }

    /// <summary>The failure reason, or an empty string when the test succeeded.</summary>
    public string Error { get; init; } = string.Empty;

    /// <summary>
    /// Whether the attempt was abandoned because it hit this test's own time
    /// limit. The proxy imposes no such limit, so a timeout here does not prove
    /// a real print would have failed - it only proves it would have been slow.
    /// </summary>
    public bool TimedOut { get; init; }
}

/// <summary>
/// Tests whether a printer can be reached, using the same address parsing and
/// the same kind of connection the print path uses.
/// </summary>
internal class PrinterTester
{
    private readonly ILogger<PrinterTester> _logger;

    public PrinterTester( ILogger<PrinterTester> logger )
    {
        _logger = logger;
    }

    /// <summary>
    /// Opens a connection to the printer and reports what happened.
    /// </summary>
    /// <param name="address">The printer address, in <c>host</c> or <c>host:port</c> notation.</param>
    /// <param name="payload">
    /// Optional data to write once connected. Always <see langword="null"/>
    /// today - this is the seam for sending a stored test label later, so that
    /// when it arrives it travels the same route a real label travels rather
    /// than a second code path.
    /// </param>
    /// <param name="timeout">How long to wait before giving up.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    public async Task<PrinterTestResult> TestAsync( string address, ReadOnlyMemory<byte>? payload, TimeSpan timeout, CancellationToken cancellationToken )
    {
        // Logged so that a burst of tests is visible in the log panel rather
        // than being an invisible way to probe the network.
        _logger.LogInformation( "Testing connection to {address}.", address );

        var startedAt = Stopwatch.GetTimestamp();

        PrinterAddress printerAddress;

        try
        {
            printerAddress = PrinterAddress.Parse( address );
        }
        catch ( Exception ex )
        {
            // Same message a real print would have returned to the server.
            return Failed( address, 0, startedAt, ex.Message, timedOut: false );
        }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource( cancellationToken );
        timeoutSource.CancelAfter( timeout );

        try
        {
            // Shared with blank label printing so both open a printer socket
            // the same way. The print path keeps its own copy on purpose - see
            // PrinterSocket.
            using var socket = await PrinterSocket.OpenAsync( printerAddress, timeoutSource.Token );

            var bytesSent = 0;

            if ( payload.HasValue && payload.Value.Length > 0 )
            {
                using var stream = new NetworkStream( socket );

                await stream.WriteAsync( payload.Value, timeoutSource.Token );

                bytesSent = payload.Value.Length;
            }

            return new PrinterTestResult
            {
                Ok = true,
                Address = address,
                Port = printerAddress.Port,
                ElapsedMilliseconds = ElapsedMilliseconds( startedAt ),
                BytesSent = bytesSent
            };
        }
        catch ( OperationCanceledException ) when ( !cancellationToken.IsCancellationRequested )
        {
            return Failed( address, printerAddress.Port, startedAt,
                $"No response within {timeout.TotalSeconds:0.#} seconds.", timedOut: true );
        }
        catch ( Exception ex )
        {
            return Failed( address, printerAddress.Port, startedAt, ex.Message, timedOut: false );
        }
    }

    private static long ElapsedMilliseconds( long startedAt )
    {
        return ( long ) Stopwatch.GetElapsedTime( startedAt ).TotalMilliseconds;
    }

    private static PrinterTestResult Failed( string address, int port, long startedAt, string error, bool timedOut )
    {
        return new PrinterTestResult
        {
            Ok = false,
            Address = address,
            Port = port,
            ElapsedMilliseconds = ElapsedMilliseconds( startedAt ),
            BytesSent = 0,
            Error = error,
            TimedOut = timedOut
        };
    }
}
