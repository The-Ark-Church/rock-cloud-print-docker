using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Rock.CloudPrint.Service;

using Xunit;

namespace Rock.CloudPrint.Tests;

/// <summary>
/// Pins the fixes from a review of 1.6.0-rc1.
///
/// <para>
/// Not run alongside other test classes: the socket test counts the whole
/// process's open file handles, and sockets opened by other tests at the same
/// moment would be counted against it.
/// </para>
/// </summary>
[Collection( nameof( ReviewFixTests ) )]
public class ReviewFixTests : IDisposable
{
    private readonly string _root = Path.Combine( Path.GetTempPath(), "review-" + Guid.NewGuid().ToString( "n" ) );

    public ReviewFixTests()
    {
        Directory.CreateDirectory( _root );
    }

    public void Dispose()
    {
        try { Directory.Delete( _root, true ); } catch { }
    }

    private sealed class Lifetime : Microsoft.Extensions.Hosting.IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }

    private sealed class Monitor : IOptionsMonitor<CloudPrintOptions>
    {
        public CloudPrintOptions CurrentValue { get; set; } = new();
        public CloudPrintOptions Get( string? name ) => CurrentValue;
        public IDisposable? OnChange( Action<CloudPrintOptions, string?> listener ) => null;
    }

    private sealed class CannedHandler( HttpStatusCode code, string body ) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync( HttpRequestMessage request, CancellationToken cancellationToken ) =>
            Task.FromResult( new HttpResponseMessage( code ) { Content = new StringContent( body ) } );
    }

    private sealed class Factory( HttpMessageHandler handler ) : IHttpClientFactory
    {
        public HttpClient CreateClient( string name ) => new( handler, disposeHandler: false );
    }

    // A line break in an address used to be accepted, because anything after a
    // colon that is not a number falls back to the default port, and so it
    // reached the log and the saved printer list.
    [Theory]
    [InlineData( "192.0.2.5:\nwarn: forged line" )]
    [InlineData( "192.0.2.5\r" )]
    [InlineData( "192.0.2.5:9100\t" )]
    public void Parse_RefusesControlCharacters( string address )
    {
        Assert.Throws<FormatException>( () => PrinterAddress.Parse( address ) );
        Assert.DoesNotContain( "\n", PrinterAddress.ForLog( address ) );
    }

    [Fact]
    public void ForLog_LeavesAnOrdinaryAddressAlone()
    {
        Assert.Equal( "192.0.2.5:9100", PrinterAddress.ForLog( "192.0.2.5:9100" ) );
    }

    // Rock answered, so an unexpected JSON shape must not be reported as the
    // server being unreachable.
    [Theory]
    [InlineData( "{\"accepted\":true,\"workflowError\":42}" )]
    [InlineData( "[]" )]
    public async Task Notifier_DoesNotCallAnAnswerUnreachable( string body )
    {
        var monitor = new Monitor { CurrentValue = new CloudPrintOptions { NotificationUrl = "https://example.com/hook" } };
        var notifier = new FailureNotifier( new Factory( new CannedHandler( HttpStatusCode.Accepted, body ) ), monitor,
            NullLogger<FailureNotifier>.Instance, "test" );

        var attempt = await notifier.SendTestAsync( CancellationToken.None );

        Assert.NotEqual( "The Rock server could not be reached.", attempt.Outcome );
        Assert.Equal( 202, attempt.StatusCode );
    }

    // The codes for a run are held in memory before printing starts, so a huge
    // quantity used to be able to exhaust the process's memory.
    [Theory]
    [InlineData( BlankLabelRunner.MaxQuantity + 1 )]
    [InlineData( int.MaxValue )]
    public void BlankRun_RefusesAQuantityOverTheLimit( int quantity )
    {
        var labels = new LabelStore( Path.Combine( _root, "labels" ), NullLogger<LabelStore>.Instance );
        Assert.Equal( LabelSaveOutcome.Saved, labels.Save( "L", Encoding.ASCII.GetBytes( "^XA^FD???^FS^XZ" ) ) );
        var state = new BlankLabelStateStore( Path.Combine( _root, "state.json" ), NullLogger<BlankLabelStateStore>.Instance );
        var runner = new BlankLabelRunner( labels, state, new Lifetime(), NullLogger<BlankLabelRunner>.Instance );

        var result = runner.Start( new BlankRunRequest { Address = "192.0.2.5", Labels = new[] { "L" }, Quantity = quantity, Mode = "random" } );

        Assert.Equal( BlankRunStartOutcome.BadQuantity, result.Outcome );
    }

    // A failed connect on the print path used to leave its socket for the
    // garbage collector, one file handle per unreachable printer.
    [Fact]
    public async Task PrintPath_DisposesASocketThatFailedToConnect()
    {
        var probe = new TcpListener( IPAddress.Loopback, 0 );
        probe.Start();
        var port = ( ( IPEndPoint ) probe.LocalEndpoint ).Port;
        probe.Stop();

        var open = typeof( ProxyClientWebSocket ).GetMethod( "OpenSocketAsync", BindingFlags.NonPublic | BindingFlags.Static )!;

        static int OpenHandles() => Directory.GetFiles( "/proc/self/fd" ).Length;

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assert.True( GC.TryStartNoGCRegion( 100_000_000 ) );
        var before = OpenHandles();

        for ( var i = 0; i < 200; i++ )
        {
            try { await ( Task<Socket> ) open.Invoke( null, new object[] { $"127.0.0.1:{port}", CancellationToken.None } )!; }
            catch ( SocketException ) { }
        }

        var after = OpenHandles();

        if ( System.Runtime.GCSettings.LatencyMode == System.Runtime.GCLatencyMode.NoGCRegion )
            GC.EndNoGCRegion();

        Assert.True( after - before < 10, $"{after - before} handles left open after 200 refused connects" );
    }
}

[CollectionDefinition( nameof( ReviewFixTests ), DisableParallelization = true )]
public class ReviewFixTestsCollection
{
}
