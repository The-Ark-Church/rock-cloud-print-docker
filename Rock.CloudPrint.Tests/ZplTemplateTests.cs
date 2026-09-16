using System.Text;

using Rock.CloudPrint.Service;

using Xunit;

namespace Rock.CloudPrint.Tests;

/// <summary>
/// What the proxy reads out of a label. The important fact here is the last
/// one: the token is only found inside a data field, so a note to a future
/// editor written in a comment is not mistaken for the place the security code
/// goes.
/// </summary>
public class ZplTemplateTests
{
    [Fact]
    public void LooksLikeZpl_NeedsBothEndsOfAFormat()
    {
        Assert.True( ZplTemplate.LooksLikeZpl( Zpl( "^XA^FD???^FS^XZ" ) ) );
        Assert.False( ZplTemplate.LooksLikeZpl( Zpl( "^XA^FD???^FS" ) ) );
        Assert.False( ZplTemplate.LooksLikeZpl( Zpl( "%PDF-1.7" ) ) );
    }

    [Fact]
    public void ContainsCodeToken_FindsTheTokenInADataField()
    {
        Assert.True( ZplTemplate.ContainsCodeToken( Zpl( "^XA^FT10,10^A0N,50,50^FD???^FS^XZ" ) ) );
        Assert.True( ZplTemplate.ContainsCodeToken( Zpl( "^XA^FDCode: ???^FS^XZ" ) ) );
    }

    [Theory]
    [InlineData( "^XA^FDName^FS^XZ" )]
    [InlineData( "^XA^FXthe code ??? goes in the field below^FS^FDName^FS^XZ" )]
    [InlineData( "^XA???^FDName^FS^XZ" )]
    [InlineData( "^XA^FDName^FS ??? ^XZ" )]
    public void ContainsCodeToken_IgnoresTokensOutsideADataField( string content )
    {
        // Only what is inside ^FD…^FS is printed, so only what is inside
        // ^FD…^FS can be the place the code goes.
        Assert.False( ZplTemplate.ContainsCodeToken( Zpl( content ) ) );
    }

    [Fact]
    public void ContainsCodeToken_IgnoresAnUnterminatedField()
    {
        // A printer would not print this either.
        Assert.False( ZplTemplate.ContainsCodeToken( Zpl( "^XA^FD???" ) ) );
    }

    [Fact]
    public void DataFields_FindsEachFieldOnceAndOnlyItsData()
    {
        const string content = "^XA^FDone^FS^FT1,1^FDtwo^FS^XZ";

        var fields = ZplTemplate.DataFields( content )
            .Select( field => content.Substring( field.Start, field.Length ) )
            .ToArray();

        Assert.Equal( new[] { "one", "two" }, fields );
    }

    [Fact]
    public void ReadSize_TakesTheDimensionsTheTemplateDeclares()
    {
        var (width, length) = ZplTemplate.ReadSize( Zpl( "^XA^MMT^PW609^LL0406^LS0^FD???^FS^XZ" ) );

        Assert.Equal( 609, width );
        Assert.Equal( 406, length );
    }

    [Fact]
    public void ReadSize_ReturnsZeroWhenTheTemplateDoesNotSay()
    {
        var (width, length) = ZplTemplate.ReadSize( Zpl( "^XA^FD???^FS^XZ" ) );

        Assert.Equal( 0, width );
        Assert.Equal( 0, length );
    }

    [Fact]
    public void ByteEncoding_RoundTripsEveryByteValue()
    {
        // The whole substitution approach rests on this. A template can carry
        // ^GF graphics and text under ^CI0, neither of which is valid UTF-8, so
        // decoding to text and back has to be lossless for all 256 values or
        // the label that comes out is not the label that went in.
        var all = new byte[256];

        for ( var i = 0; i < 256; i++ )
        {
            all[i] = ( byte ) i;
        }

        Assert.Equal( all, ZplTemplate.ByteEncoding.GetBytes( ZplTemplate.ByteEncoding.GetString( all ) ) );
    }

    private static byte[] Zpl( string content ) => Encoding.Latin1.GetBytes( content );
}
