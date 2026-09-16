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
using System.Net;
using System.Net.Sockets;

namespace Rock.CloudPrint.Service;

/// <summary>What the capture is doing.</summary>
internal enum CaptureState
{
    /// <summary>Not listening.</summary>
    Idle,

    /// <summary>Listening, waiting for one label.</summary>
    Waiting,

    /// <summary>Holding a label that has not been saved or discarded yet.</summary>
    Captured
}

/// <summary>Capture as the web UI sees it.</summary>
internal sealed record CaptureSnapshot
{
    public string State { get; init; } = string.Empty;

    public int Port { get; init; }

    public int Bytes { get; init; }

    public DateTimeOffset? CapturedAt { get; init; }

    /// <summary>Who connected. Worth showing, so a capture from somewhere unexpected is visible.</summary>
    public string? From { get; init; }

    /// <summary>
    /// The distinct <c>^FD…^FS</c> contents of the captured label, so a person
    /// can pick out which one holds the security code.
    /// </summary>
    public IReadOnlyList<string> Fields { get; init; } = Array.Empty<string>();

    public string? Error { get; init; }
}

/// <summary>
/// Captures a label that Rock prints, by being the printer.
///
/// <para>
/// Rock will not hand out the ZPL for a label designed in its own designer -
/// the only render path is behind a guard that refuses anything that is not
/// already raw ZPL. But the proxy sits in the middle of every print, so a label
/// Rock prints arrives here as raw ZPL whatever it was authored as. That is the
/// only route to a designed label, and it is what this is for.
/// </para>
///
/// <para>
/// It works by being an ordinary printer. Arm it, then point a Device in Rock
/// at this proxy's own address and the capture port, and print to it. Rock
/// sends the job down the WebSocket it already has, the proxy opens a TCP
/// connection to what it believes is a printer, and this answers. Nothing in
/// the print path changes, or knows: the bytes that land here are byte for byte
/// the bytes a printer would have been given.
/// </para>
///
/// <para>
/// It listens only while armed and stops after one label. Arming it and
/// forgetting cannot quietly record a real child's label during a service,
/// because after the first job it is not listening any more.
/// </para>
///
/// <para>
/// <strong>What this is for, and what it is not.</strong> A label designed for
/// check-in does not become a useful blank. Designed labels write text straight
/// into fields and carry no rules or boxes to write on, because nothing is ever
/// hand-written on them - so blanking one leaves empty space with no indication
/// of what goes where. The workflow runs the other way: design a label
/// <em>intended as a blank</em>, with lines to write on and a recognisable
/// placeholder where the code goes, and use this to get it out of Rock.
/// </para>
/// </summary>
internal sealed class LabelCapture : IDisposable
{
    /// <summary>
    /// Not 9100. That is where real printers listen, and on a proxy using the
    /// host's network taking it would shadow a printer on this machine.
    /// </summary>
    public const int DefaultPort = 9101;

    /// <summary>
    /// A label is a few hundred bytes to a few kilobytes. This is the same
    /// ceiling an upload gets, and it bounds what an unexpected caller can make
    /// this hold in memory.
    /// </summary>
    public const int MaxCaptureBytes = LabelStore.MaxContentBytes;

    private readonly ILogger<LabelCapture> _logger;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly object _gate = new();

    private TcpListener? _listener;
    private CancellationTokenSource? _cancellation;
    private CaptureState _state = CaptureState.Idle;
    private byte[]? _captured;
    private DateTimeOffset? _capturedAt;
    private string? _from;
    private string? _error;
    private int _port;

    public LabelCapture( IHostApplicationLifetime lifetime, ILogger<LabelCapture> logger )
    {
        _lifetime = lifetime;
        _logger = logger;
    }

    /// <summary>The captured bytes, or null.</summary>
    public byte[]? Captured
    {
        get
        {
            lock ( _gate )
            {
                return _captured;
            }
        }
    }

    /// <summary>What to show in the web UI.</summary>
    public CaptureSnapshot Snapshot()
    {
        lock ( _gate )
        {
            return new CaptureSnapshot
            {
                State = _state.ToString().ToLowerInvariant(),
                Port = _port,
                Bytes = _captured?.Length ?? 0,
                CapturedAt = _capturedAt,
                From = _from,
                Fields = _captured == null ? Array.Empty<string>() : FieldsIn( _captured ),
                Error = _error
            };
        }
    }

    /// <summary>
    /// Starts listening for one label. Returns the reason if it could not.
    /// </summary>
    public string? Arm( int port )
    {
        lock ( _gate )
        {
            if ( _state == CaptureState.Waiting )
            {
                return "Capture is already waiting for a label.";
            }

            if ( _state == CaptureState.Captured )
            {
                return "There is already a captured label. Save it or discard it first.";
            }

            if ( port < 1 || port > 65535 )
            {
                return "That is not a port number.";
            }

            try
            {
                // Every interface rather than loopback only. The address typed
                // into Rock is the one the proxy will dial, and somebody will
                // reasonably type this machine's own network address rather
                // than 127.0.0.1 - which a loopback-only listener would refuse,
                // for reasons that would take an hour to work out. The port is
                // open only while armed, and whoever connected is recorded.
                _listener = new TcpListener( IPAddress.Any, port );
                _listener.Start();
            }
            catch ( Exception ex )
            {
                _listener = null;

                _logger.LogWarning( ex, "Could not listen on port {port} to capture a label.", port );

                return $"Could not listen on port {port}. {ex.Message}";
            }

            _cancellation = CancellationTokenSource.CreateLinkedTokenSource( _lifetime.ApplicationStopping );
            _state = CaptureState.Waiting;
            _port = port;
            _error = null;
            _from = null;
            _capturedAt = null;
            _captured = null;

            var listener = _listener;
            var token = _cancellation.Token;

            _logger.LogInformation( "Waiting on port {port} to capture one label.", port );

            _ = Task.Run( () => AcceptOneAsync( listener, token ) );

            return null;
        }
    }

    /// <summary>Stops listening. Keeps anything already captured.</summary>
    public void Disarm()
    {
        lock ( _gate )
        {
            StopListening();

            if ( _state == CaptureState.Waiting )
            {
                _state = CaptureState.Idle;

                _logger.LogInformation( "Stopped waiting to capture a label." );
            }
        }
    }

    /// <summary>Throws away a captured label.</summary>
    public void Discard()
    {
        lock ( _gate )
        {
            StopListening();

            _state = CaptureState.Idle;
            _captured = null;
            _capturedAt = null;
            _from = null;
            _error = null;
        }
    }

    public void Dispose()
    {
        lock ( _gate )
        {
            StopListening();
        }
    }

    /// <summary>
    /// Accepts one connection, reads what it sends, and stops.
    /// </summary>
    private async Task AcceptOneAsync( TcpListener listener, CancellationToken cancellationToken )
    {
        try
        {
            using var client = await listener.AcceptTcpClientAsync( cancellationToken );

            var from = client.Client.RemoteEndPoint?.ToString();

            using var stream = client.GetStream();
            using var buffer = new MemoryStream();

            var chunk = new byte[8192];
            var truncated = false;

            while ( true )
            {
                var read = await stream.ReadAsync( chunk, cancellationToken );

                if ( read <= 0 )
                {
                    break;
                }

                if ( buffer.Length + read > MaxCaptureBytes )
                {
                    truncated = true;

                    break;
                }

                buffer.Write( chunk, 0, read );
            }

            lock ( _gate )
            {
                StopListening();

                _captured = buffer.ToArray();
                _capturedAt = DateTimeOffset.Now;
                _from = from;
                _state = CaptureState.Captured;
                _error = truncated
                    ? $"More than {MaxCaptureBytes} bytes arrived, so this is only the start of it."
                    : null;
            }

            _logger.LogInformation( "Captured {bytes} bytes from {from}.", buffer.Length, from );
        }
        catch ( OperationCanceledException )
        {
            // Disarmed, or the service is stopping. Neither is a fault.
        }
        catch ( ObjectDisposedException )
        {
            // The listener was closed underneath the accept, which is how
            // disarming works.
        }
        catch ( Exception ex )
        {
            _logger.LogWarning( ex, "Capture failed." );

            lock ( _gate )
            {
                StopListening();

                _state = CaptureState.Idle;
                _error = ex.Message;
            }
        }
    }

    /// <summary>Must be called holding the lock.</summary>
    private void StopListening()
    {
        try
        {
            _cancellation?.Cancel();
            _listener?.Stop();
        }
        catch ( Exception ex )
        {
            _logger.LogDebug( ex, "Tidying up the capture listener." );
        }
        finally
        {
            _cancellation?.Dispose();
            _cancellation = null;
            _listener = null;
        }
    }

    /// <summary>
    /// The distinct contents of the label's data fields, longest first.
    ///
    /// <para>
    /// This is how somebody identifies the placeholder they put in the code
    /// field when they designed the label. Showing the fields rather than
    /// guessing is deliberate: which field holds the code is a decision only
    /// the person who designed it can make.
    /// </para>
    /// </summary>
    private static IReadOnlyList<string> FieldsIn( byte[] content )
    {
        var text = ZplTemplate.ByteEncoding.GetString( content );
        var seen = new List<string>();

        foreach ( var field in ZplTemplate.DataFields( text ) )
        {
            var value = text.Substring( field.Start, field.Length ).Trim();

            // Field data can carry a leading ^FH escape marker and similar.
            // What is wanted here is something a person recognises, so empty
            // and duplicate values are dropped rather than presented.
            if ( value.Length == 0 || value.Length > 120 || seen.Contains( value, StringComparer.Ordinal ) )
            {
                continue;
            }

            seen.Add( value );
        }

        return seen;
    }
}
