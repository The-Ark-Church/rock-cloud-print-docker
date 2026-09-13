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
namespace Rock.CloudPrint.Service;

/// <summary>
/// A single completed print attempt.
/// </summary>
internal class PrintEvent
{
    /// <summary>The printer address the labels were sent to.</summary>
    public string Address { get; init; } = string.Empty;

    /// <summary>
    /// The failure reason, or an empty string when the print succeeded. This is
    /// the exact value returned to the Rock server as the print response, so
    /// whatever Rock displays on the check-in screen and whatever is recorded
    /// here are the same string.
    /// </summary>
    public string Reason { get; init; } = string.Empty;

    /// <summary>How long the attempt took, from first byte of work to result.</summary>
    public long ElapsedMilliseconds { get; init; }

    /// <summary>When the attempt completed.</summary>
    public DateTimeOffset Timestamp { get; init; }

    /// <summary>
    /// <c>true</c> when the attempt took longer than the configured threshold,
    /// which means Rock had almost certainly already stopped waiting and shown
    /// the operator its own generic timeout message rather than this result.
    /// </summary>
    public bool ExceededRockTimeout { get; init; }

    /// <summary>Whether the labels reached the printer.</summary>
    public bool Succeeded => string.IsNullOrEmpty( Reason );
}

/// <summary>
/// Tracks the outcome of print attempts so the web UI can show whether printing
/// is actually working, and so a failure carries enough detail to act on.
///
/// This is deliberately separate from <see cref="ProxyStatus"/>, which is kept
/// byte-for-byte identical to the upstream Rock Cloud Print source.
/// </summary>
internal class PrintMetrics
{
    private readonly object _lock = new();

    private int _successfulLabels;
    private int _failedLabels;
    private int _slowPrints;
    private PrintEvent? _lastFailure;
    private PrintEvent? _lastSlowPrint;

    /// <summary>Labels that reached a printer.</summary>
    public int SuccessfulLabels
    {
        get { lock ( _lock ) { return _successfulLabels; } }
    }

    /// <summary>Labels that did not reach a printer.</summary>
    public int FailedLabels
    {
        get { lock ( _lock ) { return _failedLabels; } }
    }

    /// <summary>
    /// Attempts that finished after Rock had stopped waiting, whether they
    /// eventually succeeded or failed. The operator saw a timeout either way.
    /// </summary>
    public int SlowPrints
    {
        get { lock ( _lock ) { return _slowPrints; } }
    }

    /// <summary>The most recent failed attempt, or <c>null</c> if none.</summary>
    public PrintEvent? LastFailure
    {
        get { lock ( _lock ) { return _lastFailure; } }
    }

    /// <summary>The most recent attempt that outran Rock, or <c>null</c> if none.</summary>
    public PrintEvent? LastSlowPrint
    {
        get { lock ( _lock ) { return _lastSlowPrint; } }
    }

    /// <summary>
    /// Records the outcome of one print attempt and returns the resulting event
    /// so the caller can log it at an appropriate level.
    /// </summary>
    /// <param name="labelCount">Number of labels in the attempt.</param>
    /// <param name="address">The printer address.</param>
    /// <param name="reason">The failure reason, or an empty string on success.</param>
    /// <param name="elapsed">How long the attempt took.</param>
    /// <param name="slowThresholdMilliseconds">
    /// The point past which Rock is assumed to have given up waiting.
    /// </param>
    public PrintEvent Record( int labelCount, string address, string reason, TimeSpan elapsed, int slowThresholdMilliseconds )
    {
        var elapsedMilliseconds = ( long ) elapsed.TotalMilliseconds;

        var printEvent = new PrintEvent
        {
            Address = address,
            Reason = reason ?? string.Empty,
            ElapsedMilliseconds = elapsedMilliseconds,
            Timestamp = DateTimeOffset.Now,
            ExceededRockTimeout = slowThresholdMilliseconds > 0
                && elapsedMilliseconds >= slowThresholdMilliseconds
        };

        lock ( _lock )
        {
            if ( printEvent.Succeeded )
            {
                _successfulLabels += labelCount;
            }
            else
            {
                _failedLabels += labelCount;
                _lastFailure = printEvent;
            }

            if ( printEvent.ExceededRockTimeout )
            {
                _slowPrints++;
                _lastSlowPrint = printEvent;
            }
        }

        return printEvent;
    }
}
