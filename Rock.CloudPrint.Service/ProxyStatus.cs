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

class ProxyStatus
{
    private readonly object _lock = new();

    public DateTimeOffset StartedDateTime { get; set; } = DateTimeOffset.Now;

    public DateTimeOffset? ConnectedDateTime { get; set; }

    public bool IsConnected { get; private set; }

    public int TotalPrinted { get; private set; }

    /// <summary>
    /// When the server was last heard from. A socket can stay open long after
    /// the other end has gone - no close frame arrives, so the receive loop
    /// waits forever - and this is the only evidence that has happened.
    /// </summary>
    public DateTimeOffset? LastMessageReceivedDateTime { get; private set; }

    /// <summary>
    /// How long the connection has been silent, or <c>null</c> when nothing has
    /// been received yet.
    /// </summary>
    public TimeSpan? IdleTime
    {
        get
        {
            lock ( _lock )
            {
                return LastMessageReceivedDateTime.HasValue
                    ? DateTimeOffset.Now - LastMessageReceivedDateTime.Value
                    : null;
            }
        }
    }

    public void SetConnected( bool connected )
    {
        lock ( _lock )
        {
            IsConnected = connected;

            if ( connected )
            {
                ConnectedDateTime = DateTimeOffset.Now;

                // Treat connecting as activity, so a fresh connection is not
                // immediately judged idle by the watchdog.
                LastMessageReceivedDateTime = DateTimeOffset.Now;
            }
            else
            {
                ConnectedDateTime = null;
                LastMessageReceivedDateTime = null;
            }
        }
    }

    /// <summary>
    /// Records that something arrived from the server. Called for every message,
    /// not just pings, so any traffic counts as proof the link is alive.
    /// </summary>
    public void RecordMessageReceived()
    {
        lock ( _lock )
        {
            LastMessageReceivedDateTime = DateTimeOffset.Now;
        }
    }

    public void AddLabels( int count )
    {
        lock ( _lock )
        {
            TotalPrinted += count;
        }
    }
}
