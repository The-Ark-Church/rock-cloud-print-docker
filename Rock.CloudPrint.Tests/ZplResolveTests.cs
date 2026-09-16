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

    [Fact]
    public void MarkCodePlaceholder_TurnsACapturedLabelIntoATemplate()
    {
        // What capture produces: a label Rock printed, carrying whatever
        // placeholder its designer put in the security code field.
        var captured = Latin1( "^XA^FT1,1^A0N,135,134^FDWWW^FS^FT4,200^FDName^FS^XZ" );

        Assert.Equal( Latin1( "^XA^FT1,1^A0N,135,134^FD???^FS^FT4,200^FDName^FS^XZ" ),
                      ZplTemplate.MarkCodePlaceholder( captured, "WWW" ) );
    }

    [Fact]
    public void MarkCodePlaceholder_LeavesEverythingOutsideADataFieldAlone()
    {
        // WWW appearing in a command is not a field, and rewriting it would
        // corrupt the label.
        var captured = Latin1( "^XA^FXWWW note^FS^FDWWW^FS^XZ" );

        Assert.Equal( Latin1( "^XA^FXWWW note^FS^FD???^FS^XZ" ),
                      ZplTemplate.MarkCodePlaceholder( captured, "WWW" ) );
    }

    [Fact]
    public void MarkCodePlaceholder_MarksEveryFieldHoldingIt()
    {
        // A receipt torn in half carries the code on both halves.
        var captured = Latin1( "^XA^FDWWW^FS^FDWWW^FS^XZ" );

        Assert.Equal( Latin1( "^XA^FD???^FS^FD???^FS^XZ" ),
                      ZplTemplate.MarkCodePlaceholder( captured, "WWW" ) );
    }

    [Fact]
    public void MarkCodePlaceholder_LeavesALabelAloneWhenThePlaceholderIsNotThere()
    {
        // The save then fails validation for having nowhere to put a code,
        // which is a better answer than silently storing something unusable.
        var captured = Latin1( "^XA^FDName^FS^XZ" );

        Assert.Equal( captured, ZplTemplate.MarkCodePlaceholder( captured, "NOTHERE" ) );
        Assert.False( ZplTemplate.ContainsCodeToken( ZplTemplate.MarkCodePlaceholder( captured, "NOTHERE" ) ) );
    }

    [Fact]
    public void MarkCodePlaceholder_SurvivesAHighByteLabel()
    {
        var high = new byte[] { 0x80, 0xA9, 0xFF };
        var captured = Concat( Latin1( "^XA^GFA," ), high, Latin1( "^FS^FDWWW^FS^XZ" ) );
        var expected = Concat( Latin1( "^XA^GFA," ), high, Latin1( "^FS^FD???^FS^XZ" ) );

        Assert.Equal( expected, ZplTemplate.MarkCodePlaceholder( captured, "WWW" ) );
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
