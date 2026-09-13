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

namespace Rock.CloudPrint.Service;

/// <summary>
/// A printer address in the <c>host</c> or <c>host:port</c> notation the Rock
/// server sends, resolved to an endpoint the proxy can connect to.
///
/// This parsing used to live inside the print path. It was lifted out so the
/// web UI's connection test runs the *same* code the proxy runs — a test that
/// reimplements the parsing can drift from reality and start reporting
/// something other than what a real print would do.
///
/// Behaviour here is deliberately identical to what the print path did before,
/// including the parts that look like shortcomings:
///
/// <list type="bullet">
///   <item>Splitting on <c>:</c> means IPv6 literals are not supported.</item>
///   <item>A missing, empty, or non-numeric port falls back to 9100.</item>
///   <item>A host name is rejected by <see cref="IPAddress.Parse"/>, not
///   resolved - the message it throws reaches the Rock server as the print
///   response and is shown on the check-in screen, so it must not change.</item>
/// </list>
/// </summary>
internal sealed class PrinterAddress
{
    /// <summary>The default raw printing port when none is given.</summary>
    public const int DefaultPort = 9100;

    /// <summary>The parsed IP address.</summary>
    public IPAddress Address { get; }

    /// <summary>The port to connect on.</summary>
    public int Port { get; }

    /// <summary>The address exactly as it was supplied.</summary>
    public string Original { get; }

    private PrinterAddress( IPAddress address, int port, string original )
    {
        Address = address;
        Port = port;
        Original = original;
    }

    /// <summary>
    /// Parses an address in <c>0.0.0.0</c> or <c>0.0.0.0:1234</c> notation.
    /// </summary>
    /// <param name="address">The address to parse.</param>
    /// <returns>The parsed address.</returns>
    /// <exception cref="FormatException">
    /// Thrown when the host portion is not a valid IP address.
    /// </exception>
    public static PrinterAddress Parse( string address )
    {
        int printerPort = DefaultPort;
        var printerIpAddress = address;

        // If the user specified in 0.0.0.0:1234 syntax then pull out the IP and port numbers.
        if ( printerIpAddress.Contains( ':' ) )
        {
            var segments = printerIpAddress.Split( ':' );

            printerIpAddress = segments[0];
            if ( !int.TryParse( segments[1], out printerPort ) )
            {
                printerPort = DefaultPort;
            }
        }

        return new PrinterAddress( IPAddress.Parse( printerIpAddress ), printerPort, address );
    }

    /// <summary>
    /// Builds the endpoint to connect to.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when the supplied port is outside the valid range. This is left
    /// to surface here rather than being validated during parsing, so that the
    /// behaviour matches what the print path did before.
    /// </exception>
    public IPEndPoint ToEndPoint()
    {
        return new IPEndPoint( Address, Port );
    }
}
