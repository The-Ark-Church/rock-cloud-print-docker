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
using System.Security.Cryptography;
using System.Text;

using Microsoft.Extensions.Options;

namespace Rock.CloudPrint.Service;

/// <summary>
/// Manages bearer tokens for the web UI authentication layer.
/// Tokens live in memory only — they are invalidated on container restart
/// or whenever the PIN/password is changed via <see cref="RevokeAll"/>.
///
/// <para>
/// A token also ends on its own. It lapses once it has gone unused for
/// <see cref="CloudPrintOptions.SessionIdleMinutes"/>, and whatever its use,
/// once it is <see cref="CloudPrintOptions.SessionMaxHours"/> old. Without
/// that, a token copied out of a browser - or left in one on a shared machine
/// - stayed good until the container next restarted, which on a print server
/// can be months.
/// </para>
/// </summary>
internal class AuthService
{
    /// <summary>
    /// When a token was issued and when it was last accepted. A class rather
    /// than a record struct so the last-used time can be moved on in place.
    /// </summary>
    private sealed class Session
    {
        public required DateTimeOffset IssuedAt { get; init; }

        public DateTimeOffset LastUsedAt { get; set; }
    }

    private readonly Dictionary<string, Session> _sessions = new();
    private readonly object _lock = new();
    private readonly TimeProvider _time;

    /// <summary>
    /// Creates the service. The clock is injected so the tests can move time
    /// forward rather than wait for a session to lapse.
    /// </summary>
    public AuthService( TimeProvider time )
    {
        _time = time;
    }

    /// <summary>
    /// The number of tokens currently held, expired or not. Exposed for the
    /// tests, which check that lapsed tokens do not pile up.
    /// </summary>
    public int SessionCount
    {
        get { lock ( _lock ) { return _sessions.Count; } }
    }

    /// <summary>
    /// Returns <c>true</c> if a PIN or password has been configured via
    /// either the settings file or an environment variable.
    /// </summary>
    public bool IsPasswordConfigured( IOptionsMonitor<CloudPrintOptions> options ) =>
        !string.IsNullOrWhiteSpace( options.CurrentValue.Password );

    /// <summary>
    /// Returns <c>true</c> if <paramref name="password"/> matches the
    /// currently configured value. Always returns <c>false</c> when no
    /// password is configured.
    /// </summary>
    /// <remarks>
    /// Compared in constant time, so how long a wrong guess takes to reject
    /// says nothing about how much of it was right. Both sides are hashed
    /// first because <see cref="CryptographicOperations.FixedTimeEquals"/>
    /// returns at once when the lengths differ, which would give away the
    /// length of the PIN.
    /// </remarks>
    public bool ValidatePassword( string password, IOptionsMonitor<CloudPrintOptions> options )
    {
        var configured = options.CurrentValue.Password;

        if ( string.IsNullOrWhiteSpace( configured ) ) return false;

        var given    = SHA256.HashData( Encoding.UTF8.GetBytes( password ?? string.Empty ) );
        var expected = SHA256.HashData( Encoding.UTF8.GetBytes( configured ) );

        return CryptographicOperations.FixedTimeEquals( given, expected );
    }

    /// <summary>
    /// Generates a cryptographically random 64-character hex bearer token,
    /// registers it as valid, and returns it.
    /// </summary>
    public string IssueToken( IOptionsMonitor<CloudPrintOptions> options )
    {
        var token = Convert.ToHexString( RandomNumberGenerator.GetBytes( 32 ) ).ToLower();
        var now   = _time.GetUtcNow();

        lock ( _lock )
        {
            // Issuing is the only thing that adds to the dictionary, so
            // clearing out lapsed tokens here is enough to keep it bounded:
            // it can never hold more than the logins made within one
            // session's lifetime, and the login rate limit caps those.
            PruneExpired( now, options.CurrentValue );

            _sessions[token] = new Session { IssuedAt = now, LastUsedAt = now };
        }

        return token;
    }

    /// <summary>
    /// Returns <c>true</c> if the token was issued by this instance, has not
    /// been revoked, and has not lapsed. Accepting a token counts as using
    /// it, so a session in use stays alive until its absolute limit.
    /// </summary>
    public bool ValidateToken( string token, IOptionsMonitor<CloudPrintOptions> options )
    {
        if ( string.IsNullOrWhiteSpace( token ) ) return false;

        var now = _time.GetUtcNow();

        lock ( _lock )
        {
            if ( !_sessions.TryGetValue( token, out var session ) ) return false;

            if ( IsExpired( session, now, options.CurrentValue ) )
            {
                _sessions.Remove( token );
                return false;
            }

            session.LastUsedAt = now;
            return true;
        }
    }

    /// <summary>Revokes a single token (used on explicit logout).</summary>
    public void RevokeToken( string token )
    {
        lock ( _lock ) { _sessions.Remove( token ); }
    }

    /// <summary>
    /// Revokes all active tokens. Called whenever the PIN changes so that
    /// existing sessions must re-authenticate with the new value.
    /// </summary>
    public void RevokeAll()
    {
        lock ( _lock ) { _sessions.Clear(); }
    }

    /// <summary>
    /// Removes every lapsed token. Must be called with the lock held.
    /// </summary>
    private void PruneExpired( DateTimeOffset now, CloudPrintOptions options )
    {
        foreach ( var (token, session) in _sessions )
        {
            if ( IsExpired( session, now, options ) )
            {
                // Removing during enumeration is allowed for Dictionary
                // since .NET Core 3.0, so no copy of the keys is needed.
                _sessions.Remove( token );
            }
        }
    }

    /// <summary>
    /// Whether a session has lapsed. The limits are read on every check
    /// rather than stamped on the session when it is issued, so shortening
    /// them in the settings file takes effect on sessions already open.
    /// Zero, or anything below it, switches a limit off.
    /// </summary>
    private static bool IsExpired( Session session, DateTimeOffset now, CloudPrintOptions options )
    {
        // Compared as numbers rather than by building a TimeSpan from the
        // setting. TimeSpan.FromHours throws past about 256 million hours, so
        // somebody writing a huge number to mean "never" - instead of zero -
        // would otherwise turn every signed-in request into a 500.
        if ( options.SessionIdleMinutes > 0 &&
             ( now - session.LastUsedAt ).TotalMinutes >= options.SessionIdleMinutes )
        {
            return true;
        }

        if ( options.SessionMaxHours > 0 &&
             ( now - session.IssuedAt ).TotalHours >= options.SessionMaxHours )
        {
            return true;
        }

        return false;
    }
}
