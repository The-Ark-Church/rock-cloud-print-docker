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

    /// <summary>
    /// When the connection was last lost, or when the proxy started if it has
    /// never connected. Null while connected. The health check measures its
    /// grace period from here, which <see cref="ConnectedDateTime"/> cannot do
    /// because it is cleared the moment the connection drops.
    /// </summary>
    public DateTimeOffset? DisconnectedDateTime { get; private set; }

    public ProxyStatus()
    {
        DisconnectedDateTime = StartedDateTime;
    }

    public bool IsConnected { get; private set; }

    public int TotalPrinted { get; private set; }

    /// <summary>
    /// Labels Rock has sent over the current connection. Rock sends every print
    /// for a Proxy ID to the proxy that connected first, so a proxy that has
    /// been connected a while and has this at zero is most likely standing by
    /// behind another one. The proxy is never told that directly - this is the
    /// closest it can get.
    /// </summary>
    public int LabelsThisConnection { get; private set; }

    /// <summary>
    /// When Rock last sent a print, over this connection or an earlier one.
    /// </summary>
    public DateTimeOffset? LastPrintDateTime { get; private set; }

    public void SetConnected( bool connected )
    {
        lock ( _lock )
        {
            // Reporting a disconnect twice must not restart the grace period, or a
            // proxy that keeps being told it is down would never be reported so.
            if ( !connected && !IsConnected && DisconnectedDateTime.HasValue )
            {
                return;
            }

            IsConnected = connected;
            LabelsThisConnection = 0;

            if ( connected )
            {
                ConnectedDateTime = DateTimeOffset.Now;
                DisconnectedDateTime = null;
            }
            else
            {
                ConnectedDateTime = null;
                DisconnectedDateTime = DateTimeOffset.Now;
            }
        }
    }

    public void AddLabels( int count )
    {
        lock ( _lock )
        {
            TotalPrinted += count;
            LabelsThisConnection += count;
            LastPrintDateTime = DateTimeOffset.Now;
        }
    }
}
