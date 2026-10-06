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
using System.Text.RegularExpressions;

namespace Rock.CloudPrint.Service;

/// <summary>
/// What <c>/healthz</c> reports. <c>Status</c> is one of a handful of fixed
/// words so the endpoint, which needs no PIN, says nothing about where the
/// proxy connects or what it is called.
/// </summary>
internal record ProxyHealthResult( bool Healthy, string Status, bool Connected );

/// <summary>
/// Decides whether the container is healthy, and how the container's own
/// health check finds the endpoint that says so.
/// </summary>
internal static class ProxyHealth
{
    /// <summary>
    /// How long the proxy may be configured but not connected before it is
    /// reported unhealthy. The reconnect backoff tops out at 60 seconds, so a
    /// Rock restart or a dropped link can take a minute or more to recover by
    /// itself; reporting that as a failure would have an orchestrator restart a
    /// container that was about to reconnect on its own.
    /// </summary>
    public static readonly TimeSpan DisconnectedGracePeriod = TimeSpan.FromMinutes( 2 );

    /// <summary>
    /// Evaluates the proxy's health.
    /// </summary>
    /// <param name="isConfigured">Whether a server URL and proxy ID are set.</param>
    /// <param name="isConnected">Whether the socket to Rock is open.</param>
    /// <param name="disconnectedSince">When the proxy last lost its connection, or started if it never had one.</param>
    /// <param name="now">The current time.</param>
    public static ProxyHealthResult Evaluate( bool isConfigured, bool isConnected, DateTimeOffset? disconnectedSince, DateTimeOffset now )
    {
        if ( isConnected )
        {
            return new ProxyHealthResult( true, "connected", true );
        }

        // A fresh install has nothing to connect to yet. Calling that unhealthy
        // would put a red mark on a container that is working exactly as it
        // should, and on some platforms get it restarted in a loop before
        // anyone has had the chance to open the settings page.
        if ( !isConfigured )
        {
            return new ProxyHealthResult( true, "unconfigured", false );
        }

        // No timestamp should not happen, but if it does the proxy is not
        // connected and nothing says it is about to be.
        if ( disconnectedSince.HasValue && now - disconnectedSince.Value < DisconnectedGracePeriod )
        {
            return new ProxyHealthResult( true, "reconnecting", false );
        }

        return new ProxyHealthResult( false, "disconnected", false );
    }

    /// <summary>
    /// Turns the Kestrel <c>Urls</c> setting into the address the health check
    /// should call. Kestrel accepts wildcard hosts that are fine to listen on
    /// but not to connect to, so those become the loopback address. A plain
    /// HTTP binding is preferred over HTTPS when there are several, because the
    /// probe then has no certificate to worry about.
    /// </summary>
    /// <param name="urls">The <c>Urls</c> setting, possibly several separated by semicolons.</param>
    /// <returns>The URL of the health endpoint.</returns>
    public static Uri ResolveProbeUrl( string? urls )
    {
        var candidates = ( urls ?? string.Empty )
            .Split( ';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries )
            .Select( u => BindingPattern.Match( u ) )
            .Where( m => m.Success )
            .ToList();

        var binding = candidates.FirstOrDefault( m => m.Groups["scheme"].Value.Equals( "http", StringComparison.OrdinalIgnoreCase ) )
            ?? candidates.FirstOrDefault();

        // Nothing usable means Kestrel is on its own default, which is the
        // same port appsettings.json sets.
        if ( binding == null )
        {
            return new Uri( "http://127.0.0.1:8080/healthz" );
        }

        var scheme = binding.Groups["scheme"].Value.ToLowerInvariant();
        var host = binding.Groups["host"].Value;
        var port = binding.Groups["port"].Success
            ? binding.Groups["port"].Value
            : scheme == "https" ? "443" : "80";

        if ( host is "+" or "*" or "0.0.0.0" )
        {
            host = "127.0.0.1";
        }
        else if ( host is "[::]" )
        {
            host = "[::1]";
        }

        return new Uri( $"{scheme}://{host}:{port}/healthz" );
    }

    /// <summary>
    /// One Kestrel binding: scheme, host (a bracketed IPv6 literal or anything
    /// up to the port), and an optional port. <see cref="Uri"/> cannot be used
    /// here because it rejects the <c>+</c> and <c>*</c> wildcard hosts.
    /// </summary>
    private static readonly Regex BindingPattern = new(
        @"^(?<scheme>https?)://(?<host>\[[^\]]*\]|[^:/\[]+)(?::(?<port>\d+))?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant );
}
