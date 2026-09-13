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

    /// <summary>
    /// Adds a new log entry, evicting the oldest entry when at capacity.
    /// </summary>
    public void Add( LogEntry entry )
    {
        lock ( _lock )
        {
            _entries.Enqueue( entry );

            if ( _entries.Count > MaxEntries )
            {
                _entries.Dequeue();
            }
        }
    }

    /// <summary>
    /// Returns a snapshot of buffered entries in chronological order, newest last.
    /// </summary>
    /// <param name="limit">
    /// Maximum number of entries to return, taken from the end of the buffer.
    /// Pass <c>null</c> for the whole buffer. The web UI polls this endpoint every
    /// few seconds, so it asks for a small window rather than the full capacity.
    /// </param>
    public IReadOnlyList<LogEntry> GetEntries( int? limit = null )
    {
        lock ( _lock )
        {
            if ( limit is null || limit >= _entries.Count )
            {
                return _entries.ToList();
            }

            return _entries.Skip( _entries.Count - Math.Max( 0, limit.Value ) ).ToList();
        }
    }
}

/// <summary>
/// Represents a single captured log entry.
/// </summary>
internal class LogEntry
{
    public DateTimeOffset Timestamp { get; init; }

    public string Level { get; init; } = string.Empty;

    public string Category { get; init; } = string.Empty;

    public string Message { get; init; } = string.Empty;
}
