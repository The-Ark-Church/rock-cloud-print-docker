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
using System.Text.Json;

namespace Rock.CloudPrint.Service;

/// <summary>
/// What happened on one run of blank labels.
/// </summary>
internal sealed record BlankRunRecord
{
    public string Id { get; init; } = string.Empty;

    public DateTimeOffset StartedAt { get; init; }

    public DateTimeOffset? FinishedAt { get; init; }

    /// <summary>running, completed, failed or cancelled.</summary>
    public string Status { get; init; } = string.Empty;

    public string Printer { get; init; } = string.Empty;

    /// <summary>random or sequential.</summary>
    public string Mode { get; init; } = string.Empty;

    /// <summary>The labels making up one copy, in the order they were sent.</summary>
    public IReadOnlyList<string> Labels { get; init; } = Array.Empty<string>();

    /// <summary>How many copies were asked for.</summary>
    public int Quantity { get; init; }

    /// <summary>
    /// How many copies were written to the printer's socket.
    ///
    /// <para>
    /// Not how many printed, and the difference is not pedantry. A write
    /// completing only means the operating system took the bytes; measured on
    /// a development machine, close to a megabyte can sit in buffers after a
    /// printer has stopped reading. So this is what was handed over, and that
    /// is all anything on this side can honestly claim.
    /// </para>
    /// </summary>
    public int CopiesHandedToPrinter { get; init; }

    public string? FirstCode { get; init; }

    public string? LastCode { get; init; }

    public string? Error { get; init; }
}

/// <summary>
/// What the proxy remembers about blank label printing between restarts.
/// </summary>
internal sealed record BlankLabelState
{
    /// <summary>
    /// Where the next sequential run should start. Null when no sequential run
    /// has ever been made, or when the record of them was lost.
    /// </summary>
    public string? SequentialNext { get; init; }

    /// <summary>
    /// The highest sequential code handed out so far. Shown to whoever is
    /// about to start a run below it, so re-using a code is a decision rather
    /// than an accident.
    /// </summary>
    public string? SequentialReservedThrough { get; init; }

    /// <summary>The most recent runs, newest first.</summary>
    public IReadOnlyList<BlankRunRecord> History { get; init; } = Array.Empty<BlankRunRecord>();
}

/// <summary>
/// Keeps <see cref="BlankLabelState"/> in <c>config/blank-labels.json</c>.
///
/// <para>
/// Sequential codes have to survive a restart, or the stack printed in March
/// and the stack printed in June carry the same numbers and a pickup desk has
/// two children with the same code. That is the whole reason this file exists.
/// </para>
///
/// <para>
/// It is deliberately not part of <c>config/appsettings.json</c>. That file is
/// watched for changes and every write to it wakes the code that maintains the
/// server connection. Harmless today, but it would tie which codes have been
/// printed to whether the proxy stays connected, and those two things should
/// have nothing to do with each other.
/// </para>
/// </summary>
internal sealed class BlankLabelStateStore
{
    /// <summary>
    /// How many past runs to keep. Enough to answer "what did we print last
    /// time", and small because whether the history is wanted at all is still
    /// an open question. Raising it later costs one number.
    /// </summary>
    public const int MaxHistory = 10;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,

        // Somebody will read this over SSH on the Pi while working out which
        // codes went out, and that is easier than reading one long line.
        WriteIndented = true
    };

    private readonly string _path;
    private readonly ILogger<BlankLabelStateStore> _logger;
    private readonly object _gate = new();

    private BlankLabelState _state = new();
    private bool _unreadable;

    public BlankLabelStateStore( IWebHostEnvironment environment, ILogger<BlankLabelStateStore> logger )
        : this( Path.Combine( environment.ContentRootPath, "config", "blank-labels.json" ), logger )
    {
    }

    /// <summary>
    /// Initializes a store over a specific file. Used by tests.
    /// </summary>
    public BlankLabelStateStore( string path, ILogger<BlankLabelStateStore> logger )
    {
        _path = Path.GetFullPath( path );
        _logger = logger;

        Load();
    }

    /// <summary>
    /// Whether there was a record of sequential codes and it could not be read.
    ///
    /// <para>
    /// When this is true the proxy does not guess. Starting again from 1 after
    /// a corrupt file would silently reissue every code already printed, which
    /// is the one outcome sequential numbering exists to prevent - so instead
    /// the next run has to be given a starting value by a person who can look
    /// at the last stack and see where it got to.
    /// </para>
    ///
    /// <para>
    /// It stops being true once there is a usable starting value again.
    /// </para>
    /// </summary>
    public bool SequentialStateUnreadable
    {
        get
        {
            lock ( _gate )
            {
                return _unreadable && _state.SequentialNext == null;
            }
        }
    }

    /// <summary>The state as it stands.</summary>
    public BlankLabelState Current
    {
        get
        {
            lock ( _gate )
            {
                return _state;
            }
        }
    }

    /// <summary>
    /// Applies a change and writes it out before treating it as done.
    ///
    /// <para>
    /// The file is written first and the in-memory state only replaced once
    /// that succeeded, so a failed write leaves the proxy believing what is
    /// actually on disk. That matters because the caller reserves a range of
    /// codes through here before opening the printer's socket: if the reservation
    /// cannot be recorded, the run must not happen.
    /// </para>
    /// </summary>
    public BlankLabelState Update( Func<BlankLabelState, BlankLabelState> change )
    {
        lock ( _gate )
        {
            var updated = change( _state );

            if ( updated.History.Count > MaxHistory )
            {
                updated = updated with { History = updated.History.Take( MaxHistory ).ToArray() };
            }

            Write( updated );

            _state = updated;

            return updated;
        }
    }

    private void Write( BlankLabelState state )
    {
        System.IO.Directory.CreateDirectory( Path.GetDirectoryName( _path )! );

        AtomicFile.Write( _path, JsonSerializer.SerializeToUtf8Bytes( state, SerializerOptions ) );
    }

    private void Load()
    {
        if ( !File.Exists( _path ) )
        {
            // Never run before. That is not the same as losing the record, and
            // it is not worth warning about.
            return;
        }

        try
        {
            var state = JsonSerializer.Deserialize<BlankLabelState>( File.ReadAllBytes( _path ), SerializerOptions );

            if ( state != null )
            {
                _state = state;

                return;
            }
        }
        catch ( Exception ex )
        {
            _logger.LogWarning( ex, "Could not read {path}.", _path );
        }

        _unreadable = true;

        _logger.LogWarning( "The record of which security codes have been used could not be read, so the next sequential run needs a starting value. Nothing is assumed, because starting again from the beginning would reissue codes that have already been printed." );

        PreserveUnreadableFile();
    }

    /// <summary>
    /// Keeps a copy of a file that could not be read, before anything
    /// overwrites it. Whoever has to work out where the numbering got to may
    /// well be able to read it themselves even though the parser could not.
    /// </summary>
    private void PreserveUnreadableFile()
    {
        var kept = Path.ChangeExtension( _path, ".unreadable.json" );

        try
        {
            File.Copy( _path, kept, overwrite: true );

            _logger.LogWarning( "A copy of it was kept at {path}.", kept );
        }
        catch ( Exception ex )
        {
            _logger.LogWarning( ex, "A copy of it could not be kept at {path}.", kept );
        }
    }
}
