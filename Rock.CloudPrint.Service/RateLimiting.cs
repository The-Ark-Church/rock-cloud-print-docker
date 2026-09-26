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
using System.Globalization;
using System.Net;
using System.Threading.RateLimiting;

namespace Rock.CloudPrint.Service;

/// <summary>
/// Decides which bucket a request is counted in, so that one client using up
/// its allowance does not use up everybody else's.
/// </summary>
internal static class RateLimitKeys
{
    /// <summary>
    /// The key used when the connection has no remote address, which happens
    /// for in-process test hosts and some unusual transports. Everything
    /// without an address shares it, which is the safe way round: it cannot
    /// be used to escape a limit, only to share one.
    /// </summary>
    internal const string Unknown = "unknown";

    /// <summary>
    /// The client's address as seen on the socket.
    ///
    /// <para>
    /// X-Forwarded-For is deliberately not read. The container runs with host
    /// networking and is normally reached directly, so the socket address is
    /// the real client - and a header is something the client writes itself,
    /// so trusting it would let anybody choose a fresh bucket for every
    /// request. Somebody who puts the proxy behind a reverse proxy would need
    /// to configure ForwardedHeaders deliberately, with that proxy listed as
    /// known, before the address here meant anything other than the proxy.
    /// </para>
    /// </summary>
    internal static string ForClient( HttpContext context ) =>
        ForAddress( context.Connection.RemoteIpAddress );

    /// <summary>
    /// Normalises an address into a partition key. An IPv4 client reached over
    /// a dual-stack socket shows up as ::ffff:a.b.c.d, and without mapping it
    /// back the same machine could be counted in two buckets depending on how
    /// it connected.
    /// </summary>
    internal static string ForAddress( IPAddress? address )
    {
        if ( address is null )
            return Unknown;

        if ( address.IsIPv4MappedToIPv6 )
            address = address.MapToIPv4();

        return address.ToString();
    }
}

/// <summary>
/// What a rejected request is told, in words the UI can show as they are.
/// </summary>
internal static class RateLimitRejection
{
    /// <summary>
    /// Whole seconds for a Retry-After header, rounded up so that a client
    /// which waits exactly that long is not rejected again.
    /// </summary>
    internal static int RetryAfterSeconds( TimeSpan retryAfter ) =>
        Math.Max( 1, ( int ) Math.Ceiling( retryAfter.TotalSeconds ) );

    internal static string RetryAfterHeader( TimeSpan retryAfter ) =>
        RetryAfterSeconds( retryAfter ).ToString( CultureInfo.InvariantCulture );

    /// <summary>
    /// A fixed window reports its whole length as the retry time rather than
    /// what is left of it, so this errs towards "a minute" when it may be
    /// less. Telling somebody to wait slightly too long is better than telling
    /// them to try again and having it fail.
    /// </summary>
    internal static string Message( TimeSpan? retryAfter )
    {
        if ( retryAfter is not TimeSpan wait || wait <= TimeSpan.FromSeconds( 2 ) )
            return "Too many attempts - wait a moment and try again.";

        var seconds = RetryAfterSeconds( wait );

        if ( seconds == 60 )
            return "Too many attempts - wait a minute and try again.";

        return seconds < 60
            ? $"Too many attempts - wait {seconds} seconds and try again."
            : $"Too many attempts - wait {( seconds + 59 ) / 60} minutes and try again.";
    }
}

/// <summary>
/// Counts a request against its own client's limiter first and then against a
/// limiter every client shares, and admits it only if both allow it.
///
/// <para>
/// The order matters. A client that has used up its own allowance is turned
/// away before the shared limiter is asked, so one noisy client cannot drain
/// the shared allowance and lock everybody else out - it only ever spends its
/// own share of it.
/// </para>
///
/// <para>
/// Both limiters are expected to be fixed windows with automatic
/// replenishment. A partitioned limiter only replenishes limiters it can see
/// are replenishing ones, and this wrapper is not one, so the inner limiters
/// must run their own timers. A fixed window also never gives permits back
/// when a lease is released, which is why releasing the client's lease when
/// the shared limiter refuses is harmless: the attempt still counts against
/// that client, as it should.
/// </para>
/// </summary>
internal sealed class ClientThenSharedLimiter : RateLimiter
{
    private readonly RateLimiter _client;
    private readonly RateLimiter _shared;

    /// <param name="client">Owned by this limiter and disposed with it.</param>
    /// <param name="shared">Not owned. It outlives every client's limiter.</param>
    public ClientThenSharedLimiter( RateLimiter client, RateLimiter shared )
    {
        _client = client;
        _shared = shared;
    }

    // The partitioned limiter drops a client's limiter once this says it has
    // been idle a while. Only the client's own limiter decides that; the
    // shared one is never idle for long enough to matter and is not ours.
    public override TimeSpan? IdleDuration => _client.IdleDuration;

    public override RateLimiterStatistics? GetStatistics() => _client.GetStatistics();

    protected override RateLimitLease AttemptAcquireCore( int permitCount )
    {
        var clientLease = _client.AttemptAcquire( permitCount );

        if ( !clientLease.IsAcquired )
            return clientLease;

        var sharedLease = _shared.AttemptAcquire( permitCount );

        return Combine( clientLease, sharedLease );
    }

    protected override async ValueTask<RateLimitLease> AcquireAsyncCore( int permitCount, CancellationToken cancellationToken )
    {
        var clientLease = await _client.AcquireAsync( permitCount, cancellationToken );

        if ( !clientLease.IsAcquired )
            return clientLease;

        var sharedLease = await _shared.AcquireAsync( permitCount, cancellationToken );

        return Combine( clientLease, sharedLease );
    }

    private static RateLimitLease Combine( RateLimitLease clientLease, RateLimitLease sharedLease )
    {
        if ( sharedLease.IsAcquired )
            return new BothLease( clientLease, sharedLease );

        // The shared lease is returned so its Retry-After reaches the caller.
        clientLease.Dispose();
        return sharedLease;
    }

    protected override void Dispose( bool disposing )
    {
        if ( disposing )
            _client.Dispose();
    }

    protected override ValueTask DisposeAsyncCore() => _client.DisposeAsync();

    private sealed class BothLease : RateLimitLease
    {
        private readonly RateLimitLease _first;
        private readonly RateLimitLease _second;

        public BothLease( RateLimitLease first, RateLimitLease second )
        {
            _first = first;
            _second = second;
        }

        public override bool IsAcquired => true;

        public override IEnumerable<string> MetadataNames =>
            _first.MetadataNames.Concat( _second.MetadataNames ).Distinct();

        public override bool TryGetMetadata( string metadataName, out object? metadata ) =>
            _first.TryGetMetadata( metadataName, out metadata )
            || _second.TryGetMetadata( metadataName, out metadata );

        protected override void Dispose( bool disposing )
        {
            if ( disposing )
            {
                _first.Dispose();
                _second.Dispose();
            }
        }
    }
}
