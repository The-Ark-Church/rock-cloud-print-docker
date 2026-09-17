using System.Text;

using Microsoft.Extensions.Logging.Abstractions;

using Rock.CloudPrint.Service;

using Xunit;

namespace Rock.CloudPrint.Tests;

/// <summary>
/// The record of which security codes have already been printed.
///
/// <para>
/// The test that matters is the corrupt one. A stack printed in March and a
/// stack printed in June carrying the same numbers is the failure this file
/// exists to prevent, and quietly starting again from the beginning after an
/// unreadable file is exactly how that would happen.
/// </para>
/// </summary>
public class BlankLabelStateStoreTests : IDisposable
{
    private readonly string _root = Path.Combine( Path.GetTempPath(), "blankstate-" + Guid.NewGuid().ToString( "n" ) );

    private string Path_ => System.IO.Path.Combine( _root, "config", "blank-labels.json" );

    private BlankLabelStateStore NewStore() => new( Path_, NullLogger<BlankLabelStateStore>.Instance );

    public void Dispose()
    {
        if ( Directory.Exists( _root ) )
        {
            Directory.Delete( _root, recursive: true );
        }

        GC.SuppressFinalize( this );
    }

    [Fact]
    public void ANewInstallHasNoStateAndThatIsNotAProblem()
    {
        var store = NewStore();

        // Never having run is not the same as having lost the record, and only
        // one of those is worth telling anyone about.
        Assert.False( store.SequentialStateUnreadable );
        Assert.Null( store.Current.SequentialNext );
        Assert.Empty( store.Current.History );
    }

    [Fact]
    public void WhatIsWrittenComesBackAfterARestart()
    {
        NewStore().Update( state => state with
        {
            SequentialNext = "1001",
            SequentialReservedThrough = "1000"
        } );

        var reopened = NewStore();

        Assert.Equal( "1001", reopened.Current.SequentialNext );
        Assert.Equal( "1000", reopened.Current.SequentialReservedThrough );
    }

    [Fact]
    public void AnUnreadableRecordIsReportedRatherThanReplacedWithAGuess()
    {
        Directory.CreateDirectory( System.IO.Path.GetDirectoryName( Path_ )! );
        File.WriteAllText( Path_, "{ this is not json" );

        var store = NewStore();

        Assert.True( store.SequentialStateUnreadable );

        // The important half. Defaulting this to 1 would reissue every code
        // already printed, silently.
        Assert.Null( store.Current.SequentialNext );
    }

    [Fact]
    public void AnUnreadableRecordIsKeptSoSomebodyCanReadItThemselves()
    {
        Directory.CreateDirectory( System.IO.Path.GetDirectoryName( Path_ )! );
        File.WriteAllText( Path_, "{\"sequentialNext\": \"1234\" truncated" );

        NewStore();

        var kept = System.IO.Path.Combine( _root, "config", "blank-labels.unreadable.json" );

        Assert.True( File.Exists( kept ) );
        Assert.Contains( "1234", File.ReadAllText( kept ), StringComparison.Ordinal );
    }

    [Fact]
    public void OnceThereIsAStartingValueAgainTheWarningStops()
    {
        Directory.CreateDirectory( System.IO.Path.GetDirectoryName( Path_ )! );
        File.WriteAllText( Path_, "not json at all" );

        var store = NewStore();

        Assert.True( store.SequentialStateUnreadable );

        store.Update( state => state with { SequentialNext = "2001" } );

        Assert.False( store.SequentialStateUnreadable );
    }

    [Fact]
    public void HistoryKeepsTheMostRecentRunsAndDropsTheRest()
    {
        var store = NewStore();

        for ( var i = 1; i <= BlankLabelStateStore.MaxHistory + 5; i++ )
        {
            var run = new BlankRunRecord { Id = $"run-{i}", Status = "completed" };

            store.Update( state => state with { History = new[] { run }.Concat( state.History ).ToArray() } );
        }

        var history = NewStore().Current.History;

        Assert.Equal( BlankLabelStateStore.MaxHistory, history.Count );
        Assert.Equal( $"run-{BlankLabelStateStore.MaxHistory + 5}", history[0].Id );
    }

    [Fact]
    public void ARunRecordSurvivesBeingWrittenAndReadBack()
    {
        var store = NewStore();
        var started = DateTimeOffset.UtcNow;

        store.Update( state => state with
        {
            History = new[]
            {
                new BlankRunRecord
                {
                    Id = "abc",
                    StartedAt = started,
                    Status = "failed",
                    Printer = "10.0.0.5:9100",
                    Mode = "sequential",
                    Labels = new[] { "Demo-Child-Label", "Demo-Roster-Label" },
                    Quantity = 30,
                    CopiesHandedToPrinter = 12,
                    FirstCode = "1001",
                    LastCode = "1030",
                    Error = "Connection reset by peer"
                }
            }
        } );

        var run = NewStore().Current.History.Single();

        Assert.Equal( "abc", run.Id );
        Assert.Equal( "failed", run.Status );
        Assert.Equal( 30, run.Quantity );
        Assert.Equal( 12, run.CopiesHandedToPrinter );
        Assert.Equal( new[] { "Demo-Child-Label", "Demo-Roster-Label" }, run.Labels );
        Assert.Equal( "1001", run.FirstCode );
        Assert.Equal( "1030", run.LastCode );
        Assert.Equal( "Connection reset by peer", run.Error );
    }

    [Fact]
    public void AWriteLeavesNoTemporaryFileBehind()
    {
        var store = NewStore();

        store.Update( state => state with { SequentialNext = "5" } );

        // The temp-then-move is what stops a power cut leaving a half-written
        // record. What it must not do is litter the config directory.
        Assert.Empty( Directory.GetFiles( System.IO.Path.GetDirectoryName( Path_ )!, "*.tmp" ) );
    }

    [Fact]
    public void TheFileIsSomethingAPersonCanReadOverSsh()
    {
        NewStore().Update( state => state with { SequentialNext = "1001" } );

        var written = File.ReadAllText( Path_, Encoding.UTF8 );

        Assert.Contains( "\"sequentialNext\": \"1001\"", written, StringComparison.Ordinal );
    }
}
