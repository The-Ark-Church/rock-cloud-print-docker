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
using System.Net.Sockets;

namespace Rock.CloudPrint.Service;

/// <summary>
/// Opens a raw TCP connection to a label printer.
///
/// <para>
/// This exists so the parts of the service a person starts by hand - the web
/// UI's connection test, and blank label printing - open a printer socket the
/// same way instead of each keeping its own copy of three lines that are easy
/// to get subtly different.
/// </para>
///
/// <para>
/// <see cref="ProxyClientWebSocket"/> keeps its own private copy and is left
/// alone deliberately. It sits on the print path, carrying real check-in
/// traffic, and is tracked byte-for-byte against upstream. Sharing a helper
/// with it would mean editing it for the benefit of a feature it plays no part
/// in, which is the one thing that path must never be edited for.
/// </para>
///
/// <para>
/// There is no time limit here, and there should never be one. How long an
/// attempt is worth waiting for is the caller's decision and the two callers
/// answer it differently: the connection test gives up after a few seconds so
/// the UI has something to say, and a print run does not give up at all,
/// because a printer that has paused for more labels has not failed.
/// </para>
/// </summary>
internal static class PrinterSocket
{
    /// <summary>
    /// Connects to the printer at the given address.
    /// </summary>
    /// <param name="address">The parsed printer address.</param>
    /// <param name="cancellationToken">Abandons the attempt.</param>
    /// <returns>A connected socket, which the caller owns and must dispose.</returns>
    public static async Task<Socket> OpenAsync( PrinterAddress address, CancellationToken cancellationToken )
    {
        var socket = new Socket( AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp );

        try
        {
            await socket.ConnectAsync( address.ToEndPoint(), cancellationToken );
        }
        catch
        {
            // The caller cannot dispose a socket it was never handed, so a
            // failed attempt has to clean up after itself. Before this was
            // lifted out, the caller's own using statement did the job.
            // Without it every unreachable printer would cost a file handle,
            // which on a proxy that runs for months is a slow leak rather than
            // an obvious bug.
            socket.Dispose();

            throw;
        }

        return socket;
    }
}
