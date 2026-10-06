using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Threading.RateLimiting;

using Microsoft.Extensions.Logging.Abstractions;

using Rock.CloudPrint.Service;

using Xunit;

namespace Rock.CloudPrint.Tests;

/// <summary>
/// Every call to Labelary waits for its own permit from one shared limiter, so
/// a preview of several labels cannot take the proxy past the rate Labelary
/// asks for. Nothing here calls the real service.
/// </summary>
public class LabelPreviewTests
{
    private static readonly byte[] Template = Encoding.ASCII.GetBytes( "^XA^FD???^FS^XZ" );

    /// <summary>Stands in for Labelary: answers every call with a "picture" and notes when it came.</summary>
    private sealed class FakeLabelary : HttpMessageHandler
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();

        public ConcurrentQueue<TimeSpan> Calls { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync( HttpRequestMessage request, CancellationToken cancellationToken )
        {
            Calls.Enqueue( _clock.Elapsed );

            return Task.FromResult( new HttpResponseMessage( HttpStatusCode.OK ) { Content = new ByteArrayContent( new byte[] { 0x89, 0x50, 0x4E, 0x47 } ) } );
        }
    }

    private sealed class Factory( HttpMessageHandler handler ) : IHttpClientFactory
    {
        public HttpClient CreateClient( string name ) => new( handler, disposeHandler: false );
    }

    /// <summary>A limiter whose permits the test hands out, or refuses, one at a time.</summary>
    private sealed class HandOutLimiter : RateLimiter
    {
        private readonly ConcurrentQueue<TaskCompletionSource<RateLimitLease>> _waiting = new();
        private int _asked;

        public int Asked => Volatile.Read( ref _asked );

        public override TimeSpan? IdleDuration => null;

        public override RateLimiterStatistics? GetStatistics() => null;

        protected override RateLimitLease AttemptAcquireCore( int permitCount ) =>
            throw new NotSupportedException( "Previews should wait for a permit, not try for one." );

        protected override ValueTask<RateLimitLease> AcquireAsyncCore( int permitCount, CancellationToken cancellationToken )
        {
            var waiting = new TaskCompletionSource<RateLimitLease>( TaskCreationOptions.RunContinuationsAsynchronously );

            cancellationToken.Register( () => waiting.TrySetCanceled( cancellationToken ) );
            _waiting.Enqueue( waiting );
            Interlocked.Increment( ref _asked );

            return new ValueTask<RateLimitLease>( waiting.Task );
        }

        public void Allow() => Next().TrySetResult( new Lease( true ) );

        public void Refuse() => Next().TrySetResult( new Lease( false ) );

        private TaskCompletionSource<RateLimitLease> Next()
        {
            Assert.True( _waiting.TryDequeue( out var next ), "Nothing was waiting for a permit." );

            return next!;
        }

        private sealed class Lease( bool acquired ) : RateLimitLease
        {
            public override bool IsAcquired => acquired;

            public override IEnumerable<string> MetadataNames => Array.Empty<string>();

            public override bool TryGetMetadata( string metadataName, out object? metadata )
            {
                metadata = null;
                return false;
            }
        }
    }

    private static async Task WaitFor( Func<bool> condition )
    {
        var deadline = DateTime.UtcNow.AddSeconds( 10 );

        while ( !condition() )
        {
            Assert.True( DateTime.UtcNow < deadline, "Timed out waiting." );
            await Task.Delay( 5 );
        }
    }

    [Fact]
    public async Task RenderAsync_DoesNotCallLabelaryUntilItHasAPermit()
    {
        var labelary = new FakeLabelary();
        var limiter = new HandOutLimiter();
        var preview = new LabelPreview( new Factory( labelary ), NullLogger<LabelPreview>.Instance, limiter );

        var rendering = preview.RenderAsync( Template, "123", CancellationToken.None );

        await WaitFor( () => limiter.Asked == 1 );
        await Task.Delay( 50 );
        Assert.Empty( labelary.Calls );
        Assert.False( rendering.IsCompleted );

        limiter.Allow();
        var result = await rendering;

        Assert.True( result.Ok );
        Assert.Single( labelary.Calls );
    }

    // The bug this pins: the limit used to be on preview requests, and a
    // preview of eight labels is eight calls. Each label must cost a permit.
    [Fact]
    public async Task APreviewOfSeveralLabels_TakesOnePermitForEachCall()
    {
        var labelary = new FakeLabelary();
        var limiter = new HandOutLimiter();
        var preview = new LabelPreview( new Factory( labelary ), NullLogger<LabelPreview>.Instance, limiter );

        // The same shape as the preview endpoint: one label after another.
        var previewing = Task.Run( async () =>
        {
            for ( var label = 0; label < 3; label++ )
                Assert.True( ( await preview.RenderAsync( Template, "123", CancellationToken.None ) ).Ok );
        } );

        for ( var permit = 1; permit <= 3; permit++ )
        {
            await WaitFor( () => limiter.Asked == permit );
            Assert.Equal( permit - 1, labelary.Calls.Count );

            limiter.Allow();
            await WaitFor( () => labelary.Calls.Count == permit );
        }

        await previewing;

        Assert.Equal( 3, limiter.Asked );
        Assert.Equal( 3, labelary.Calls.Count );
    }

    [Fact]
    public async Task RenderAsync_WhenTooManyCallsAreWaiting_SaysBusyAndDoesNotCallLabelary()
    {
        var labelary = new FakeLabelary();
        var limiter = new HandOutLimiter();
        var preview = new LabelPreview( new Factory( labelary ), NullLogger<LabelPreview>.Instance, limiter );

        var rendering = preview.RenderAsync( Template, "123", CancellationToken.None );

        await WaitFor( () => limiter.Asked == 1 );
        limiter.Refuse();
        var result = await rendering;

        Assert.False( result.Ok );
        Assert.True( result.Busy );
        Assert.False( string.IsNullOrWhiteSpace( result.Error ) );
        Assert.Empty( labelary.Calls );
    }

    [Fact]
    public async Task RenderAsync_CancelledWhileWaiting_GivesUpWithoutCallingLabelary()
    {
        var labelary = new FakeLabelary();
        var limiter = new HandOutLimiter();
        var preview = new LabelPreview( new Factory( labelary ), NullLogger<LabelPreview>.Instance, limiter );
        using var cancellation = new CancellationTokenSource();

        var rendering = preview.RenderAsync( Template, "123", cancellation.Token );

        await WaitFor( () => limiter.Asked == 1 );
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>( () => rendering );
        Assert.Empty( labelary.Calls );
    }

    // The limiter the service actually uses, against the clock. Two previews
    // at once, as two people pressing Preview together would make: however
    // the calls fall, no four of them may land inside one second.
    [Fact]
    public async Task TheServicesLimiter_NeverLetsMoreThanThreeCallsIntoOneSecond()
    {
        var labelary = new FakeLabelary();
        var preview = new LabelPreview( new Factory( labelary ), NullLogger<LabelPreview>.Instance );

        async Task Preview( int labels )
        {
            for ( var label = 0; label < labels; label++ )
                Assert.True( ( await preview.RenderAsync( Template, "123", CancellationToken.None ) ).Ok );
        }

        await Task.WhenAll( Preview( 3 ), Preview( 3 ) ).WaitAsync( TimeSpan.FromSeconds( 30 ) );

        var calls = labelary.Calls.OrderBy( t => t ).ToArray();

        Assert.Equal( 6, calls.Length );

        // A little slack for the gap between a permit being given and the
        // call being noted. Without the limiter these are a millisecond or
        // two apart, nowhere near.
        for ( var i = 0; i + 3 < calls.Length; i++ )
        {
            var span = calls[i + 3] - calls[i];

            Assert.True( span >= TimeSpan.FromMilliseconds( 900 ), $"Calls {i + 1} to {i + 4} all landed within {span.TotalMilliseconds:0} ms." );
        }
    }

    [Fact]
    public async Task TheServicesLimiter_TurnsCallsAwayOnceTheQueueIsFull()
    {
        var labelary = new FakeLabelary();
        var preview = new LabelPreview( new Factory( labelary ), NullLogger<LabelPreview>.Instance );
        using var cancellation = new CancellationTokenSource();

        // Far more than can wait: one goes straight through, a queue's worth
        // wait their turn, and the rest are told it is busy.
        var renders = Enumerable.Range( 0, LabelPreview.MaxCallsWaiting + 10 )
            .Select( _ => preview.RenderAsync( Template, "123", cancellation.Token ) )
            .ToArray();

        var busy = renders.Count( r => r.IsCompletedSuccessfully && r.Result.Busy );

        // Cancelling the rest rather than waiting the dozen seconds they would
        // take to drain.
        cancellation.Cancel();

        foreach ( var render in renders )
        {
            try { await render; } catch ( OperationCanceledException ) { }
        }

        Assert.True( busy >= 1, "Nothing was turned away." );
        Assert.True( labelary.Calls.Count <= 3, $"{labelary.Calls.Count} calls went through." );
    }
}
