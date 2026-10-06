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
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.Sockets;

using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace Rock.CloudPrint.Service;

/// <summary>
/// Works out which client a request really came from when the web UI is
/// reached through a reverse proxy, and nothing more.
///
/// <para>
/// The rate limits count per client address. Reached directly, the
/// connection's address is the client and nothing here is needed. Behind a
/// reverse proxy, every connection comes from the proxy, so every person
/// would share one allowance. The proxy records who it forwarded for in
/// <c>X-Forwarded-For</c>, but that header is just text, and anybody can send
/// it - so it is believed only on a connection that comes from an address
/// somebody configured as the proxy, and only its last entry, which is the
/// one that proxy added itself. Everything further left was written by
/// whoever made the request and proves nothing.
/// </para>
///
/// <para>
/// ASP.NET Core's own forwarded headers middleware is not used because its
/// options are fixed when the application starts, and these settings are
/// changed from the web UI and expected to apply straight away.
/// </para>
/// </summary>
internal static class ReverseProxyAddress
{
    /// <summary>The environment variable that turns the setting on.</summary>
    internal const string TrustReverseProxyVariable = "TrustReverseProxy";

    /// <summary>The environment variable that names the proxy's address.</summary>
    internal const string TrustedProxyVariable = "TrustedProxy";

    /// <summary>
    /// Longest list of proxy addresses accepted. Room for a handful of IPv6
    /// addresses with commas, which is far more than anybody puts in front
    /// of a print proxy.
    /// </summary>
    internal const int MaxListLength = 500;

    /// <summary>How many proxy addresses may be listed.</summary>
    internal const int MaxListEntries = 10;

    /// <summary>
    /// Whether either setting comes from an environment variable. When it
    /// does, docker-compose.yml is in charge of both and the web UI shows them
    /// without letting them be changed, the same way it treats a PIN set with
    /// <c>Password</c>.
    /// </summary>
    internal static bool SetByEnvironment =>
        !string.IsNullOrWhiteSpace( Environment.GetEnvironmentVariable( TrustReverseProxyVariable ) )
        || !string.IsNullOrWhiteSpace( Environment.GetEnvironmentVariable( TrustedProxyVariable ) );

    /// <summary>
    /// The two settings as the environment gives them, for adding to the
    /// configuration after the settings file so that they win over it.
    ///
    /// <para>
    /// The settings file is added last of all the configuration sources, so
    /// without this a value saved in it would quietly beat the environment
    /// variable that is supposed to control it. Only these two keys are
    /// re-applied, so nothing else changes how it is resolved.
    /// </para>
    ///
    /// <para>
    /// A <c>TrustReverseProxy</c> that is not <c>true</c> or <c>false</c> is
    /// treated as <c>false</c> and reported in <paramref name="invalidSwitch"/>.
    /// Left as it was, the configuration binder would throw on it every time
    /// the options were read, which would take the whole web UI down over one
    /// typo. Off is the safe way to be wrong: everybody shares the proxy's
    /// allowance, as they did before the setting existed.
    /// </para>
    /// </summary>
    internal static Dictionary<string, string?> EnvironmentOverrides( out string? invalidSwitch )
    {
        invalidSwitch = null;
        var overrides = new Dictionary<string, string?>();

        var trust = Environment.GetEnvironmentVariable( TrustReverseProxyVariable );

        if ( !string.IsNullOrWhiteSpace( trust ) )
        {
            if ( bool.TryParse( trust.Trim(), out var on ) )
            {
                overrides[nameof( CloudPrintOptions.TrustReverseProxy )] = on ? "true" : "false";
            }
            else
            {
                invalidSwitch = trust;
                overrides[nameof( CloudPrintOptions.TrustReverseProxy )] = "false";
            }
        }

        var proxies = Environment.GetEnvironmentVariable( TrustedProxyVariable );

        if ( !string.IsNullOrWhiteSpace( proxies ) )
        {
            overrides[nameof( CloudPrintOptions.TrustedProxy )] = proxies.Trim();
        }

        return overrides;
    }

    /// <summary>
    /// The client the request should be counted as, or <c>null</c> to leave
    /// the connection's address as it is.
    ///
    /// <para>
    /// Null whenever there is any doubt: the setting is off, the proxy list
    /// does not parse, the connection is not from a listed proxy, or the
    /// header is missing or its last entry is not an address. Each of those
    /// falls back to the connection's own address, which is what this proxy
    /// did before the setting existed - never to an entry further left.
    /// </para>
    /// </summary>
    internal static IPAddress? ForwardedClient( IPAddress? remoteAddress, StringValues forwardedFor, bool trustReverseProxy, string? trustedProxies )
    {
        if ( !trustReverseProxy || remoteAddress is null )
            return null;

        if ( !TryParseList( trustedProxies, out var trusted, out _ ) )
            return null;

        var remote = Normalise( remoteAddress );

        if ( !trusted.Any( proxy => Normalise( proxy ).Equals( remote ) ) )
            return null;

        var last = LastEntry( forwardedFor );

        return last is not null && TryParseForwardedEntry( last, out var client ) ? client : null;
    }

    /// <summary>
    /// Parses the configured proxy addresses: one, or several separated by
    /// commas. Every entry must be an address; one that is not makes the whole
    /// list fail rather than being skipped, so a typo shows up as an error
    /// when saving instead of as one proxy silently not being trusted.
    /// </summary>
    /// <param name="invalidEntry">The first entry that is not an address, for the error message.</param>
    internal static bool TryParseList( string? value, out IReadOnlyList<IPAddress> addresses, out string? invalidEntry )
    {
        addresses = Array.Empty<IPAddress>();
        invalidEntry = null;

        if ( string.IsNullOrWhiteSpace( value ) || value.Length > MaxListLength )
            return false;

        var entries = value.Split( ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries );

        if ( entries.Length == 0 || entries.Length > MaxListEntries )
            return false;

        var parsed = new List<IPAddress>( entries.Length );

        foreach ( var entry in entries )
        {
            if ( !TryParseAddress( entry, out var address ) )
            {
                invalidEntry = entry;
                return false;
            }

            parsed.Add( address );
        }

        addresses = parsed;
        return true;
    }

    /// <summary>
    /// Parses a single IP address, more strictly than
    /// <see cref="IPAddress.TryParse(string?, out IPAddress?)"/> does.
    ///
    /// <para>
    /// That method accepts old shorthand forms of IPv4 - a bare number such
    /// as <c>1234</c>, or fewer than four parts - and turns them into some
    /// other address entirely. Somebody who mistypes the proxy's address
    /// should be told, not have a different address trusted. So an IPv4
    /// address must read back exactly as it was written, and an IPv6 one must
    /// at least contain a colon.
    /// </para>
    /// </summary>
    internal static bool TryParseAddress( string? text, [NotNullWhen( true )] out IPAddress? address )
    {
        address = null;
        text = text?.Trim();

        // The longest textual IPv6 address, with a scope, is well under this.
        if ( string.IsNullOrEmpty( text ) || text.Length > 64 )
            return false;

        if ( !IPAddress.TryParse( text, out var parsed ) )
            return false;

        if ( parsed.AddressFamily == AddressFamily.InterNetwork
            && !string.Equals( parsed.ToString(), text, StringComparison.Ordinal ) )
            return false;

        if ( parsed.AddressFamily == AddressFamily.InterNetworkV6 && !text.Contains( ':' ) )
            return false;

        address = parsed;
        return true;
    }

    /// <summary>
    /// Parses one <c>X-Forwarded-For</c> entry. Most proxies write a bare
    /// address, but some add the client's port - <c>192.0.2.10:51234</c>, or
    /// <c>[2001:db8::10]:51234</c> for IPv6 - so a port is allowed and
    /// dropped.
    /// </summary>
    internal static bool TryParseForwardedEntry( string entry, [NotNullWhen( true )] out IPAddress? address )
    {
        address = null;
        var text = entry.Trim();

        if ( text.StartsWith( '[' ) )
        {
            var close = text.IndexOf( ']' );

            if ( close < 0 )
                return false;

            var rest = text[( close + 1 )..];

            if ( rest.Length > 0 && !IsPort( rest ) )
                return false;

            text = text[1..close];
        }
        else if ( text.Count( c => c == ':' ) == 1 )
        {
            // One colon is an IPv4 address with a port. An IPv6 address has
            // at least two, and without brackets it cannot carry a port.
            var colon = text.IndexOf( ':' );

            if ( !IsPort( text[colon..] ) )
                return false;

            text = text[..colon];
        }

        return TryParseAddress( text, out address );
    }

    /// <summary>
    /// The last entry of the header, which is the one the nearest proxy
    /// added. A proxy may append to the header or add a second copy of it,
    /// and the two mean the same thing, so this is the end of the last copy.
    /// An empty last entry is returned as empty, and then fails to parse,
    /// rather than being skipped to reach the one before it: the one before
    /// it is not the proxy's.
    /// </summary>
    private static string? LastEntry( StringValues forwardedFor )
    {
        if ( forwardedFor.Count == 0 )
            return null;

        var last = forwardedFor[forwardedFor.Count - 1];

        if ( last is null )
            return null;

        var comma = last.LastIndexOf( ',' );

        return comma >= 0 ? last[( comma + 1 )..] : last;
    }

    private static bool IsPort( string text ) =>
        text.Length > 1
        && text[0] == ':'
        && ushort.TryParse( text.AsSpan( 1 ), NumberStyles.None, CultureInfo.InvariantCulture, out _ );

    /// <summary>
    /// An IPv4 proxy reached over a dual-stack socket shows up as
    /// <c>::ffff:a.b.c.d</c>. Folding it back means the address somebody
    /// typed matches however the connection arrived.
    /// </summary>
    private static IPAddress Normalise( IPAddress address ) =>
        address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
}

/// <summary>
/// Replaces the connection's address with the client a trusted reverse proxy
/// says it forwarded for, before the rate limiter decides which bucket the
/// request is counted in. <see cref="ReverseProxyAddress"/> holds the rules.
///
/// <para>
/// The settings are read on every request rather than once, so turning the
/// setting on or changing the proxy's address in the web UI applies to the
/// very next request, without a restart.
/// </para>
/// </summary>
internal sealed class TrustedProxyMiddleware
{
    private const string ForwardedForHeader = "X-Forwarded-For";

    private readonly RequestDelegate _next;
    private readonly IOptionsMonitor<CloudPrintOptions> _options;

    public TrustedProxyMiddleware( RequestDelegate next, IOptionsMonitor<CloudPrintOptions> options )
    {
        _next = next;
        _options = options;
    }

    public Task InvokeAsync( HttpContext context )
    {
        var options = _options.CurrentValue;

        var client = ReverseProxyAddress.ForwardedClient(
            context.Connection.RemoteIpAddress,
            context.Request.Headers[ForwardedForHeader],
            options.TrustReverseProxy,
            options.TrustedProxy );

        if ( client is not null )
            context.Connection.RemoteIpAddress = client;

        return _next( context );
    }
}
