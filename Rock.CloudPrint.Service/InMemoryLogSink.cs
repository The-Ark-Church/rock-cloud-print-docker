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
/// A fixed-size circular buffer that stores recent log entries for display
/// in the web UI. This lives in memory only - it is not written to disk and
/// is cleared whenever the container restarts. For durable history use
/// <c>docker logs</c>, which is rotated by the logging options in
/// docker-compose.yml.
/// </summary>
internal class InMemoryLogSink
{
    private readonly Queue<LogEntry> _entries = new();
    private readonly object _lock = new();
    // A reconnect produces a multi-line exception chain, so a small buffer can be
    // wiped by one bad night and lose the print history for a whole service.
    private const int MaxEntries = 2000;

    private long _nextSeq = 1;

    /// <summary>
    /// Identifies this buffer for the lifetime of the process. Sequence numbers
    /// restart from one whenever the service does, so a client holding a
    /// position from a previous run would otherwise wait forever for entries
    /// that will never arrive. A changed value tells the client to start over.
    /// </summary>
    public string InstanceId { get; } = Guid.NewGuid().ToString( "n" );

    /// <summary>
    /// Adds a new log entry, evicting the oldest entry when at capacity.
    /// </summary>
    public void Add( LogEntry entry )
    {
        lock ( _lock )
        {
            entry.Seq = _nextSeq++;

            _entries.Enqueue( entry );

            if ( _entries.Count > MaxEntries )
            {
                _entries.Dequeue();
            }
        }
    }

    /// <summary>
    /// Returns the entries newer than <paramref name="after"/>, in chronological
    /// order, newest last.
    /// </summary>
    /// <param name="after">
    /// The highest sequence number the caller already has. Pass zero for a fresh
    /// start, which returns the tail of the buffer without reporting a gap.
    /// </param>
    /// <param name="limit">
    /// Maximum number of entries to return, taken from the end.
    /// </param>
    public LogTail GetTail( long after, int limit )
    {
        lock ( _lock )
        {
            var lastSeq = _nextSeq - 1;
            var oldestSeq = _entries.Count > 0 ? _entries.Peek().Seq : _nextSeq;
            var isResuming = after > 0;

            long skipped = 0;

            // Entries the caller wanted but that were evicted before it asked.
            if ( isResuming && after < oldestSeq - 1 )
            {
                skipped = oldestSeq - 1 - after;
            }

            var matching = _entries.Where( e => e.Seq > after ).ToList();

            if ( limit > 0 && matching.Count > limit )
            {
                // More arrived than the caller asked for. Only a resuming caller
                // is missing anything; a fresh one simply asked for a tail.
                if ( isResuming )
                {
                    skipped += matching.Count - limit;
                }

                matching = matching.Skip( matching.Count - limit ).ToList();
            }

            return new LogTail
            {
                InstanceId = InstanceId,
                Entries = matching,
                LastSeq = matching.Count > 0 ? matching[^1].Seq : Math.Max( after, lastSeq ),
                Skipped = skipped
            };
        }
    }
}

/// <summary>
/// A page of log entries plus what the caller needs to ask for the next one.
/// </summary>
internal class LogTail
{
    /// <summary>Identifies the buffer these entries came from.</summary>
    public string InstanceId { get; init; } = string.Empty;

    /// <summary>The entries, chronological, newest last.</summary>
    public IReadOnlyList<LogEntry> Entries { get; init; } = Array.Empty<LogEntry>();

    /// <summary>The sequence number to pass as <c>after</c> next time.</summary>
    public long LastSeq { get; init; }

    /// <summary>
    /// How many entries were lost between what the caller had and what it
    /// received, either evicted from the buffer or trimmed by the limit.
    /// </summary>
    public long Skipped { get; init; }
}

/// <summary>
/// Represents a single captured log entry.
/// </summary>
internal class LogEntry
{
    /// <summary>
    /// Monotonic position within this process's buffer, assigned when the entry
    /// is added. Restarts from one when the service restarts.
    /// </summary>
    public long Seq { get; set; }

    public DateTimeOffset Timestamp { get; init; }

    public string Level { get; init; } = string.Empty;

    public string Category { get; init; } = string.Empty;

    public string Message { get; init; } = string.Empty;
}
