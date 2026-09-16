using System.Reflection;
using System.Text;

using Rock.CloudPrint.Service;

using Xunit;

namespace Rock.CloudPrint.Tests;

/// <summary>
/// Resolving a template must change the token and nothing else.
///
/// <para>
/// This is the fact the whole feature rests on. The label that reaches the
/// printer has to be the label its author designed - same size, same cut
/// behaviour, same quantity command, same graphics - with one field filled in.
/// So these tests do not check that the output "looks right"; they compare it
/// to the input byte by byte and require every difference to fall inside a
/// token.
/// </para>
/// </summary>
public class ZplResolveTests
{
    [Theory]
    [InlineData( "Demo-Child-Label.zpl" )]
    [InlineData( "Demo-Parent-Receipt.zpl" )]
    [InlineData( "Demo-Roster-Label.zpl" )]
    public void Resolve_ChangesTheTokensAndNotOneOtherByte( string resource )
    {
        var template = DemoTemplate( resource );

        // A code the same length as the token, so the two byte arrays line up
        // and any drift shows as a difference at a known offset rather than as
        // an unhelpful length mismatch.
        var resolved = ZplTemplate.Resolve( template, "XYZ" );

        Assert.Equal( template.Length, resolved.Length );

        // Found by searching the raw bytes, deliberately not by asking the
        // code under test where it thinks the tokens are.
        var tokens = TokenOffsets( template );

        Assert.NotEmpty( tokens );

        for ( var i = 0; i < template.Length; i++ )
        {
            if ( tokens.Any( start => i >= start && i < start + 3 ) )
            {
                continue;
            }

            Assert.True( template[i] == resolved[i],
                $"Byte {i} changed outside a token: 0x{template[i]:x2} became 0x{resolved[i]:x2}." );
        }

        foreach ( var start in tokens )
        {
            Assert.Equal( "XYZ", Encoding.Latin1.GetString( resolved, start, 3 ) );
        }
    }

    [Fact]
    public void Resolve_LeavesTheCommandsThatDecideHowALabelPrints()
    {
        // The three that would quietly ruin a run if anything rewrote them:
        // quantity, cut mode, and the label size.
        var roster = Encoding.Latin1.GetString( ZplTemplate.Resolve( DemoTemplate( "Demo-Roster-Label.zpl" ), "ABC" ) );

        Assert.Contains( "^PQ1,0,1,Y", roster, StringComparison.Ordinal );
        Assert.Contains( "^MMC", roster, StringComparison.Ordinal );
        Assert.Contains( "^PW609", roster, StringComparison.Ordinal );
        Assert.Contains( "^LL0406", roster, StringComparison.Ordinal );

        // And the parent receipt genuinely has no ^PQ, which is why raising a
        // quantity command rather than repeating the label would not even be
        // consistent across the set.
        var parent = Encoding.Latin1.GetString( ZplTemplate.Resolve( DemoTemplate( "Demo-Parent-Receipt.zpl" ), "ABC" ) );

        Assert.DoesNotContain( "^PQ", parent, StringComparison.Ordinal );
    }

    [Fact]
    public void Resolve_SurvivesEveryHighByteOutsideAField()
    {
        // ^GF graphics and text under ^CI0 both put bytes above 0x7F into a
        // template. Decoding as UTF-8 anywhere along the way would turn these
        // into replacement characters and corrupt the label.
        var high = new byte[128];

        for ( var i = 0; i < high.Length; i++ )
        {
            high[i] = ( byte ) ( 0x80 + i );
        }

        var template = Concat( Latin1( "^XA^FX" ), high, Latin1( "^FS^FT10,10^FD???^FS" ), high, Latin1( "^XZ" ) );
        var resolved = ZplTemplate.Resolve( template, "ABC" );

        var expected = Concat( Latin1( "^XA^FX" ), high, Latin1( "^FS^FT10,10^FDABC^FS" ), high, Latin1( "^XZ" ) );

        Assert.Equal( expected, resolved );
    }

    [Fact]
    public void Resolve_SurvivesHighBytesInsideTheFieldItIsEditing()
    {
        var template = Latin1( "^XA^FDé???ÿ^FS^XZ" );

        Assert.Equal( Latin1( "^XA^FDéABCÿ^FS^XZ" ), ZplTemplate.Resolve( template, "ABC" ) );
    }

    [Fact]
    public void Resolve_LeavesATokenInAComment()
    {
        // A comment is a note to whoever edits the template next. Substituting
        // into one would rewrite their note and, worse, suggest the code lands
        // somewhere it does not.
        var template = Latin1( "^XA^FXthe code ??? goes below^FS^FT1,1^FD???^FS^XZ" );

        Assert.Equal( Latin1( "^XA^FXthe code ??? goes below^FS^FT1,1^FDABC^FS^XZ" ),
                      ZplTemplate.Resolve( template, "ABC" ) );
    }

    [Fact]
    public void Resolve_ReplacesEveryTokenInTheSameCopyWithTheSameCode()
    {
        // The parent receipt carries the code twice, once for each half of a
        // torn receipt. Both halves have to match or the receipt is useless.
        var resolved = Encoding.Latin1.GetString( ZplTemplate.Resolve( DemoTemplate( "Demo-Parent-Receipt.zpl" ), "K7M" ) );

        Assert.Equal( 2, CountOf( resolved, "^FDK7M^FS" ) );
        Assert.DoesNotContain( "???", resolved, StringComparison.Ordinal );
    }

    [Fact]
    public void Resolve_HandlesACodeThatIsNotThreeCharacters()
    {
        Assert.Equal( Latin1( "^XA^FD12345678^FS^XZ" ),
                      ZplTemplate.Resolve( Latin1( "^XA^FD???^FS^XZ" ), "12345678" ) );

        Assert.Equal( Latin1( "^XA^FDA^FS^XZ" ),
                      ZplTemplate.Resolve( Latin1( "^XA^FD???^FS^XZ" ), "A" ) );
    }

    [Fact]
    public void Resolve_ReturnsATemplateWithNoTokenUnchanged()
    {
        var template = Latin1( "^XA^FDName^FS^XZ" );

        Assert.Equal( template, ZplTemplate.Resolve( template, "ABC" ) );
    }

    /// <summary>
    /// A real Next-Gen label, as Rock actually printed it and this captured it.
    ///
    /// <para>
    /// Every awkward thing about capture is in here, which is why it is the
    /// fixture rather than something tidier. The two security code fields are
    /// <strong>empty</strong>, because Rock renders them from an attendance and
    /// a test print has none. Empty is written "\&", which also appears at the
    /// end of the two title fields - so replacing by text rewrites the wrong
    /// ones. And there are two codes, not one, because the receipt is torn in
    /// half.
    /// </para>
    /// </summary>
    private const string CapturedReceipt =
        "^XA^CI28^PW609^LL406\r\n" +
        "^FO12,322^FB279,2,0,L^A0N,33,33^FDPlease keep to pick up your child^FS\r\n" +
        "^FO0,76^GB608,199,199,B,0^FS\r\n" +
        "^FO318,321^FB279,2,0,L^A0N,33,33^FDPlease keep to pick up your child^FS\r\n" +
        "^FO304,2^GD0,73,3,B,L^FS\r\n" +
        "^FO0,126^FR^FB304,1,0,C^A0N,128,112^FD\\&^FS\r\n" +
        "^FO304,126^FR^FB304,1,0,C^A0N,128,112^FD\\&^FS\r\n" +
        "^FO4,12^FB295,1,0,C^A0N,39,39^FDChild Receipt\\&^FS\r\n" +
        "^FO309,13^FB296,1,0,C^A0N,39,39^FDChild Receipt\\&^FS\r\n" +
        "^FO304,76^GB2,200,2,W^FS\r\n" +
        "^FO304,275^GD0,130,3,B,L^FS^MMC^XZ";

    [Fact]
    public void Fields_ListsEveryFieldInOrderIncludingTheEmptyOnes()
    {
        var fields = ZplTemplate.Fields( Latin1( CapturedReceipt ) );

        Assert.Equal( 6, fields.Count );
        Assert.Equal( new[] { 0, 1, 2, 3, 4, 5 }, fields.Select( f => f.Index ) );

        // The two codes. Empty, and that is the normal case rather than a fault.
        Assert.Equal( "", fields[2].Text );
        Assert.Equal( "", fields[3].Text );

        Assert.Equal( "Please keep to pick up your child", fields[0].Text );
        Assert.Equal( "Child Receipt", fields[4].Text );
    }

    [Fact]
    public void Fields_ReportsTheFontHeightThatMakesTheCodeObvious()
    {
        var fields = ZplTemplate.Fields( Latin1( CapturedReceipt ) );

        // With both code fields empty there is nothing to read, so size is what
        // tells somebody which is which: the code is printed four times the
        // height of the caption beneath it.
        Assert.Equal( new[] { 33, 33, 128, 128, 39, 39 }, fields.Select( f => f.FontHeight ) );
    }

    [Fact]
    public void MarkCodeFields_MarksBothHalvesAndLeavesTheTitlesAlone()
    {
        var marked = Encoding.Latin1.GetString(
            ZplTemplate.MarkCodeFields( Latin1( CapturedReceipt ), new[] { 2, 3 } ) );

        Assert.Equal( 2, CountOf( marked, "^FD???^FS" ) );

        // The bug this replaced: the titles end in the same "\\&" the empty code
        // fields contained, so matching by text turned them into
        // "Child Receipt???" on a label somebody would then have printed.
        Assert.Equal( 2, CountOf( marked, "^FDChild Receipt\\&^FS" ) );
        Assert.DoesNotContain( "Child Receipt???", marked, StringComparison.Ordinal );
    }

    [Fact]
    public void MarkCodeFields_ChangesNothingButTheFieldsChosen()
    {
        var original = Latin1( CapturedReceipt );
        var marked = ZplTemplate.MarkCodeFields( original, new[] { 2 } );
        var text = Encoding.Latin1.GetString( marked );

        // Layout, sizes, the cutter command and the graphics all survive.
        foreach ( var expected in new[] { "^PW609", "^LL406", "^MMC", "^FO0,126^FR^FB304,1,0,C^A0N,128,112",
                                          "^GB608,199,199,B,0", "^GD0,130,3,B,L" } )
        {
            Assert.Contains( expected, text, StringComparison.Ordinal );
        }

        Assert.Equal( 1, CountOf( text, "^FD???^FS" ) );
    }

    [Fact]
    public void MarkCodeFields_ProducesSomethingTheStoreWillAccept()
    {
        var marked = ZplTemplate.MarkCodeFields( Latin1( CapturedReceipt ), new[] { 2, 3 } );

        Assert.True( ZplTemplate.LooksLikeZpl( marked ) );
        Assert.True( ZplTemplate.ContainsCodeToken( marked ) );

        // And once stored, a run resolves both halves to the same code.
        var resolved = Encoding.Latin1.GetString( ZplTemplate.Resolve( marked, "K7M" ) );

        Assert.Equal( 2, CountOf( resolved, "^FDK7M^FS" ) );
    }

    [Fact]
    public void MarkCodeFields_WithNothingChosenLeavesTheLabelAlone()
    {
        var original = Latin1( CapturedReceipt );

        Assert.Equal( original, ZplTemplate.MarkCodeFields( original, Array.Empty<int>() ) );
    }

    [Fact]
    public void MarkCodeFields_SurvivesAHighByteLabel()
    {
        var high = new byte[] { 0x80, 0xA9, 0xFF };
        var captured = Concat( Latin1( "^XA^GFA," ), high, Latin1( "^FS^FDWWW^FS^XZ" ) );
        var expected = Concat( Latin1( "^XA^GFA," ), high, Latin1( "^FS^FD???^FS^XZ" ) );

        Assert.Equal( expected, ZplTemplate.MarkCodeFields( captured, new[] { 0 } ) );
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private static byte[] DemoTemplate( string resource )
    {
        using var stream = typeof( ZplTemplate ).Assembly.GetManifestResourceStream( resource )
            ?? throw new InvalidOperationException( $"No embedded resource called {resource}." );

        using var buffer = new MemoryStream();

        stream.CopyTo( buffer );

        return buffer.ToArray();
    }

    private static List<int> TokenOffsets( byte[] content )
    {
        var offsets = new List<int>();
        var text = Encoding.Latin1.GetString( content );
        var at = text.IndexOf( "???", StringComparison.Ordinal );

        while ( at >= 0 )
        {
            offsets.Add( at );
            at = text.IndexOf( "???", at + 3, StringComparison.Ordinal );
        }

        return offsets;
    }

    private static int CountOf( string haystack, string needle )
    {
        var count = 0;
        var at = haystack.IndexOf( needle, StringComparison.Ordinal );

        while ( at >= 0 )
        {
            count++;
            at = haystack.IndexOf( needle, at + needle.Length, StringComparison.Ordinal );
        }

        return count;
    }

    private static byte[] Latin1( string text ) => Encoding.Latin1.GetBytes( text );

    private static byte[] Concat( params byte[][] parts )
    {
        var result = new byte[parts.Sum( part => part.Length )];
        var at = 0;

        foreach ( var part in parts )
        {
            Buffer.BlockCopy( part, 0, result, at, part.Length );
            at += part.Length;
        }

        return result;
    }
}
