using System.Net;
using System.Net.Sockets;
using System.Text;

using Microsoft.Extensions.Logging.Abstractions;

using Rock.CloudPrint.Service;

using Xunit;

namespace Rock.CloudPrint.Tests;

/// <summary>
/// Two sequential runs started close together must never be handed the same
/// security codes.
/// </summary>
public class BlankLabelRunnerConcurrencyTests : IDisposable
{
    private readonly string _root = Path.Combine( Path.GetTempPath(), "blank-race-" + Guid.NewGuid().ToString( "n" ) );

    public BlankLabelRunnerConcurrencyTests()
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

    // A sequential run used to read where to start, and work out its codes,
    // before taking the runner's lock; the reservation came later, under it.
    // A second run that reserved, printed and finished in between left the
    // first one finding nothing running, so both printed from the same start.
    //
    // The hook puts the second run in exactly that gap every time, rather than
    // hoping a large run and a short sleep line up.
    [Fact]
    public async Task SequentialRuns_NeverShareCodes_WhenOneFinishesWhileTheOtherIsWorkingOutItsCodes()
    {
        var labels = new LabelStore( Path.Combine( _root, "labels" ), NullLogger<LabelStore>.Instance );
        Assert.Equal( LabelSaveOutcome.Saved, labels.Save( "L", Encoding.ASCII.GetBytes( "^XA^FD???^FS^XZ" ) ) );

        var state = new BlankLabelStateStore( Path.Combine( _root, "state.json" ), NullLogger<BlankLabelStateStore>.Instance );
        var runner = new BlankLabelRunner( labels, state, new Lifetime(), NullLogger<BlankLabelRunner>.Instance );

        state.Update( s => s with { SequentialNext = "0001" } );

        // A printer that takes whatever it is sent.
        var listener = new TcpListener( IPAddress.Loopback, 0 );
        listener.Start();
        var port = ( ( IPEndPoint ) listener.LocalEndpoint ).Port;

        _ = Task.Run( async () =>
        {
            while ( true )
            {
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync(); } catch { return; }
                _ = Task.Run( async () => { using ( client ) { await client.GetStream().CopyToAsync( Stream.Null ); } } );
            }
        } );

        BlankRunRequest Request( int quantity ) => new()
        {
            Address = $"127.0.0.1:{port}",
            Labels = new[] { "L" },
            Quantity = quantity,
            Mode = "sequential"
        };

        var second = new TaskCompletionSource<(BlankRunStartOutcome Outcome, BlankRunStatus? Run, string? Detail)>(
            TaskCreationOptions.RunContinuationsAsynchronously );
        var hooked = 0;

        runner.AfterSequentialCodesWorkedOut = () =>
        {
            // Only for the first run; the second passes through here too.
            if ( Interlocked.Exchange( ref hooked, 1 ) == 1 )
                return;

            // A thread of its own rather than a task, so that waiting on it
            // below can never run it inline on this thread - which may hold
            // the runner's lock, and would let it straight back in.
            new Thread( () => second.SetResult( runner.Start( Request( 1 ) ) ) ) { IsBackground = true }.Start();

            // Before the fix the second run got all the way through here and
            // was given its codes, so this lets it finish printing too. With
            // the fix it is waiting for the lock this run holds, and the wait
            // simply runs out.
            if ( second.Task.Wait( TimeSpan.FromSeconds( 1 ) ) && second.Task.Result.Outcome == BlankRunStartOutcome.Started )
                WaitUntilFinished( runner, second.Task.Result.Run!.Id );
        };

        var first = runner.Start( Request( 3 ) );
        var other = await second.Task.WaitAsync( TimeSpan.FromSeconds( 10 ) );

        Assert.Equal( BlankRunStartOutcome.Started, first.Outcome );

        // The second run either found the first still printing or started
        // after it. Whichever, any codes it was given must be new ones.
        if ( other.Outcome == BlankRunStartOutcome.Started )
        {
            var a = Range( first.Run! );
            var b = Range( other.Run! );

            Assert.True( a.Last < b.First || b.Last < a.First,
                $"Both runs were given codes from the same range: {first.Run!.FirstCode}-{first.Run.LastCode} and {other.Run!.FirstCode}-{other.Run.LastCode}." );
        }
        else
        {
            Assert.Equal( BlankRunStartOutcome.AlreadyRunning, other.Outcome );
        }

        // And the record has moved past everything handed out.
        var highest = new[] { first, other }
            .Where( r => r.Outcome == BlankRunStartOutcome.Started )
            .Max( r => Range( r.Run! ).Last );

        Assert.True( long.Parse( state.Current.SequentialNext! ) > highest );

        await Task.Run( () => WaitUntilFinished( runner, runner.Current!.Id ) );
        listener.Stop();
    }

    private static (long First, long Last) Range( BlankRunStatus run ) =>
        (long.Parse( run.FirstCode! ), long.Parse( run.LastCode! ));

    private static void WaitUntilFinished( BlankLabelRunner runner, string id )
    {
        var deadline = DateTime.UtcNow.AddSeconds( 10 );

        while ( runner.Current is { Status: "running" } current && current.Id == id )
        {
            Assert.True( DateTime.UtcNow < deadline, "The run did not finish." );
            Thread.Sleep( 5 );
        }
    }
}
