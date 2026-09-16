using System.Security.Cryptography;
using System.Text;

using Microsoft.Extensions.Logging.Abstractions;

using Rock.CloudPrint.Service;

using Xunit;

namespace Rock.CloudPrint.Tests;

/// <summary>
/// The template store: what it will accept as a name, what it will accept as a
/// label, and when it seeds the demos.
/// </summary>
public class LabelStoreTests : IDisposable
{
    /// <summary>
    /// The exact bytes of the three demo templates, pinned.
    ///
    /// <para>
    /// These are hashes of the real templates The Ark prints its blanks from.
    /// They are pinned rather than recomputed because the two ways this can go
    /// wrong are both silent: the build could alter a file on its way into the
    /// assembly, or somebody could edit a template without realising it is the
    /// thing that gets printed. Either shows up here as a failure rather than
    /// as a stack of unusable labels.
    /// </para>
    /// </summary>
    private static readonly Dictionary<string, string> DemoChecksums = new()
    {
        ["Demo-Child-Label"]    = "a67a7de0eb37bb799bd64ddb6877aa6df14d9d70fc4b591f4b8b99c2d10fe064",
        ["Demo-Parent-Receipt"] = "e9b2eec4092683e20313bb7b4ca33d25c69a1fb87b8b714e78290ccf083dfa9b",
        ["Demo-Roster-Label"]   = "23ba776496af526b6176eb501511be99ae71648c6a5c6f9f5e64abb379d159a6",
    };

    private readonly string _root = Path.Combine( Path.GetTempPath(), "labelstore-" + Guid.NewGuid().ToString( "n" ) );

    private LabelStore NewStore() => new( Path.Combine( _root, "labels" ), NullLogger<LabelStore>.Instance );

    public void Dispose()
    {
        if ( Directory.Exists( _root ) )
        {
            Directory.Delete( _root, recursive: true );
        }

        GC.SuppressFinalize( this );
    }

    // ── Names ───────────────────────────────────────────────────────────────

    [Theory]
    [InlineData( "Demo-Child-Label" )]
    [InlineData( "Ark Child 3x2" )]
    [InlineData( "a" )]
    [InlineData( "label.v2" )]
    [InlineData( "under_score" )]
    public void IsValidName_AcceptsOrdinaryNames( string name )
    {
        Assert.True( LabelStore.IsValidName( name ) );
    }

    [Theory]
    [InlineData( null )]
    [InlineData( "" )]
    [InlineData( ".." )]
    [InlineData( "../appsettings" )]
    [InlineData( "..\\appsettings" )]
    [InlineData( "sub/label" )]
    [InlineData( "sub\\label" )]
    [InlineData( "/etc/passwd" )]
    [InlineData( "label\0hidden" )]
    [InlineData( "  " )]
    [InlineData( "." )]
    [InlineData( " leading" )]
    [InlineData( "trailing " )]
    [InlineData( "colon:name" )]
    [InlineData( "star*name" )]
    public void IsValidName_RejectsAnythingThatCouldLeaveTheDirectory( string? name )
    {
        Assert.False( LabelStore.IsValidName( name ) );
    }

    [Fact]
    public void IsValidName_RejectsNamesPastTheLimit()
    {
        Assert.True( LabelStore.IsValidName( new string( 'a', LabelStore.MaxNameLength ) ) );
        Assert.False( LabelStore.IsValidName( new string( 'a', LabelStore.MaxNameLength + 1 ) ) );
    }

    // ── Seeding ─────────────────────────────────────────────────────────────

    [Fact]
    public void SeedIfFirstRun_WritesTheDemosByteForByte()
    {
        var store = NewStore();

        store.SeedIfFirstRun();

        var stored = store.List();

        Assert.Equal( DemoChecksums.Count, stored.Count );

        foreach ( var label in stored )
        {
            var content = store.Read( label.Name );

            Assert.NotNull( content );
            Assert.True( DemoChecksums.ContainsKey( label.Name ), $"Unexpected demo label '{label.Name}'." );
            Assert.Equal( DemoChecksums[label.Name], Convert.ToHexString( SHA256.HashData( content! ) ).ToLowerInvariant() );
        }
    }

    [Fact]
    public void SeedIfFirstRun_LeavesADeletedDemoDeleted()
    {
        var store = NewStore();

        store.SeedIfFirstRun();
        Assert.True( store.Delete( "Demo-Roster-Label" ) );

        // Every restart calls this. Putting back a demo somebody deliberately
        // deleted, over and over, is the behaviour the directory-exists check
        // is there to prevent.
        store.SeedIfFirstRun();

        Assert.DoesNotContain( store.List(), label => label.Name == "Demo-Roster-Label" );
        Assert.Equal( DemoChecksums.Count - 1, store.List().Count );
    }

    [Fact]
    public void SeedIfFirstRun_SeedsAgainOnceTheDirectoryIsGone()
    {
        var store = NewStore();

        store.SeedIfFirstRun();
        Directory.Delete( store.Directory, recursive: true );
        store.SeedIfFirstRun();

        Assert.Equal( DemoChecksums.Count, store.List().Count );
    }

    [Fact]
    public void SeedIfFirstRun_DoesNotReplaceAnEditedDemo()
    {
        var store = NewStore();

        store.SeedIfFirstRun();
        Assert.True( store.Delete( "Demo-Child-Label" ) );
        Assert.Equal( LabelSaveOutcome.Saved, store.Save( "Demo-Child-Label", Zpl( "^XA^FD???^FS^XZ" ) ) );

        store.SeedIfFirstRun();

        Assert.Equal( 15, store.Read( "Demo-Child-Label" )!.Length );
    }

    // ── Saving ──────────────────────────────────────────────────────────────

    [Fact]
    public void Save_StoresTheBytesExactlyAsGiven()
    {
        var store = NewStore();

        // High bytes and CRLF, both of which a real template carries and
        // neither of which may be rewritten on the way to disk.
        var content = Zpl( "^XA\r\n^FH\\^FD???éÿ^FS\r\n^XZ\r\n" );

        Assert.Equal( LabelSaveOutcome.Saved, store.Save( "Round Trip", content ) );
        Assert.Equal( content, store.Read( "Round Trip" ) );
    }

    [Fact]
    public void Save_RefusesToOverwriteAnExistingLabel()
    {
        var store = NewStore();
        var content = Zpl( "^XA^FD???^FS^XZ" );

        Assert.Equal( LabelSaveOutcome.Saved, store.Save( "Once", content ) );

        // Silently replacing a template somebody is about to print a stack
        // from is not a decision this should make on their behalf.
        Assert.Equal( LabelSaveOutcome.AlreadyExists, store.Save( "Once", content ) );
    }

    [Fact]
    public void Save_RejectsSomethingThatIsNotALabel()
    {
        Assert.Equal( LabelSaveOutcome.NotZpl,
            NewStore().Save( "Rejected", Zpl( "%PDF-1.7 not a label at all" ) ) );
    }

    [Fact]
    public void Save_RejectsALabelWithNowhereToPutTheCode()
    {
        // Printing a stack of these would produce blanks carrying no security
        // code, which is the one thing a blank has to have.
        Assert.Equal( LabelSaveOutcome.NoCodeToken,
            NewStore().Save( "Rejected", Zpl( "^XA^FDJust a caption^FS^XZ" ) ) );
    }

    [Fact]
    public void Save_DoesNotCountATokenInAComment()
    {
        // A comment is not printed, so a token in one is a note to an editor
        // rather than the place the code goes.
        Assert.Equal( LabelSaveOutcome.NoCodeToken,
            NewStore().Save( "Rejected", Zpl( "^XA^FXnote: the code ??? goes below^FS^XZ" ) ) );
    }

    [Fact]
    public void Save_RejectsSomethingTooLargeToBeATemplate()
    {
        var store = NewStore();
        var oversized = Zpl( "^XA^FD???^FS" + new string( 'x', LabelStore.MaxContentBytes ) + "^XZ" );

        Assert.Equal( LabelSaveOutcome.TooLarge, store.Save( "Huge", oversized ) );
    }

    [Theory]
    [InlineData( "../appsettings" )]
    [InlineData( "sub/label" )]
    [InlineData( ".." )]
    public void Save_RejectsANameThatWouldEscapeTheDirectory( string name )
    {
        var store = NewStore();

        Assert.Equal( LabelSaveOutcome.InvalidName, store.Save( name, Zpl( "^XA^FD???^FS^XZ" ) ) );
        Assert.Null( store.Read( name ) );
        Assert.False( store.Delete( name ) );
    }

    [Fact]
    public void Save_CannotReachTheSettingsFileNextDoor()
    {
        // The realistic target. config/appsettings.json holds the Rock URL, the
        // proxy id and the PIN, and it sits one directory above the labels.
        var settings = Path.Combine( _root, "appsettings.json" );

        Directory.CreateDirectory( _root );
        File.WriteAllText( settings, "{\"Password\":\"original\"}" );

        var store = NewStore();

        store.SeedIfFirstRun();
        store.Save( "../appsettings", Zpl( "^XA^FD???^FS^XZ" ) );
        store.Delete( "../appsettings" );

        Assert.Equal( "{\"Password\":\"original\"}", File.ReadAllText( settings ) );
    }

    // ── Listing and deleting ────────────────────────────────────────────────

    [Fact]
    public void List_ReportsTheSizeBakedIntoTheTemplate()
    {
        var store = NewStore();

        store.SeedIfFirstRun();

        var child = store.List().Single( label => label.Name == "Demo-Child-Label" );

        // 609 x 406 dots at 203 dpi is the 3x2 stock these are printed on.
        Assert.Equal( 609, child.WidthDots );
        Assert.Equal( 406, child.LengthDots );
        Assert.Equal( 513, child.Bytes );
    }

    [Fact]
    public void Delete_SaysWhetherThereWasAnythingToDelete()
    {
        var store = NewStore();

        store.SeedIfFirstRun();

        Assert.True( store.Delete( "Demo-Child-Label" ) );
        Assert.False( store.Delete( "Demo-Child-Label" ) );
    }

    [Fact]
    public void List_IgnoresAFileDroppedInUnderAnUnusableName()
    {
        var store = NewStore();

        store.SeedIfFirstRun();
        File.WriteAllBytes( Path.Combine( store.Directory, "has:colon.zpl" ), Zpl( "^XA^FD???^FS^XZ" ) );

        // Offering to print something the rest of the class refuses to address
        // would only produce an error later, at the point of printing.
        Assert.Equal( DemoChecksums.Count, store.List().Count );
    }

    private static byte[] Zpl( string content ) => Encoding.Latin1.GetBytes( content );
}
