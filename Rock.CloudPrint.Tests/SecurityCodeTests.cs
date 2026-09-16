using Rock.CloudPrint.Service;

using Xunit;

namespace Rock.CloudPrint.Tests;

/// <summary>
/// The codes themselves: that a sequential run counts the way somebody reading
/// a stack of labels would expect, and that a random run cannot hand out the
/// same code twice or a character somebody will misread.
/// </summary>
public class SecurityCodeTests
{
    [Fact]
    public void Sequential_CountsUpAndKeepsThePaddingItWasGiven()
    {
        Assert.Equal( new[] { "0998", "0999", "1000" }, SecurityCode.Sequential( "0998", 3 ) );
    }

    [Fact]
    public void Sequential_TreatsThePaddingAsAMinimumWidth()
    {
        // 998 to 1000 at three digits wide. The run does not stop at the width,
        // it just gets one character longer, which is what somebody typing 998
        // and asking for three would expect to see.
        Assert.Equal( new[] { "998", "999", "1000" }, SecurityCode.Sequential( "998", 3 ) );
    }

    [Fact]
    public void Sequential_StartsWhereItIsToldTo()
    {
        Assert.Equal( new[] { "001" }, SecurityCode.Sequential( "001", 1 ) );
        Assert.Equal( new[] { "1", "2", "3" }, SecurityCode.Sequential( "1", 3 ) );
    }

    [Fact]
    public void Sequential_PutsThePrefixInFrontOfEveryCode()
    {
        Assert.Equal( new[] { "A001", "A002" }, SecurityCode.Sequential( "001", 2, "A" ) );
    }

    [Theory]
    [InlineData( null )]
    [InlineData( "" )]
    [InlineData( "12A" )]
    [InlineData( "-1" )]
    [InlineData( "1 2" )]
    [InlineData( "123456789" )]
    public void IsUsableStart_RejectsAnythingThatIsNotACountableNumber( string? start )
    {
        Assert.False( SecurityCode.IsUsableStart( start ) );
    }

    [Fact]
    public void Random_ProducesTheAskedForNumberOfDistinctCodes()
    {
        var codes = SecurityCode.Random( 3, 1000 );

        Assert.Equal( 1000, codes.Count );
        Assert.Equal( 1000, codes.Distinct().Count() );
    }

    [Fact]
    public void Random_NeverUsesACharacterThatGetsMisread()
    {
        var codes = SecurityCode.Random( 3, 1000 );

        foreach ( var code in codes )
        {
            Assert.Equal( 3, code.Length );

            foreach ( var character in code )
            {
                // Zero against capital O, and one against capital I. Somebody
                // reads this off a label and says it out loud at a pickup desk.
                Assert.DoesNotContain( character, "0O1I" );
                Assert.Contains( character, SecurityCode.Alphabet );
            }
        }
    }

    [Fact]
    public void CodeSpace_IsThirtyTwoToThePower()
    {
        Assert.Equal( 32, SecurityCode.CodeSpace( 1 ) );
        Assert.Equal( 1024, SecurityCode.CodeSpace( 2 ) );
        Assert.Equal( 32768, SecurityCode.CodeSpace( 3 ) );
        Assert.Equal( 1099511627776, SecurityCode.CodeSpace( SecurityCode.MaxLength ) );
    }

    [Fact]
    public void HasRoomFor_AllowsAnyRealisticRunAtThreeCharacters()
    {
        // 1000 sets has been printed in practice, and three characters is what
        // is used. There is no quantity limit beyond this arithmetic one.
        Assert.True( SecurityCode.HasRoomFor( 3, 1000 ) );
        Assert.True( SecurityCode.HasRoomFor( 3, 16384 ) );
    }

    [Fact]
    public void HasRoomFor_RefusesARunThatCouldNotSucceed()
    {
        // Two characters is 1,024 codes. A thousand distinct ones is not
        // impossible by a comfortable margin, it is nearly the whole space.
        Assert.False( SecurityCode.HasRoomFor( 2, 1000 ) );
        Assert.False( SecurityCode.HasRoomFor( 1, 100 ) );
        Assert.False( SecurityCode.HasRoomFor( 3, 0 ) );
    }

    [Fact]
    public void Random_RefusesRatherThanSearchingForeverForCodesThatDoNotExist()
    {
        Assert.Throws<ArgumentException>( () => SecurityCode.Random( 2, 1000 ) );
    }

    [Theory]
    [InlineData( "" )]
    [InlineData( null )]
    [InlineData( "A" )]
    [InlineData( "ARK-" )]
    [InlineData( "K_9" )]
    public void IsUsablePrefix_AcceptsSomethingThatWillPrintAsText( string? prefix )
    {
        Assert.True( SecurityCode.IsUsablePrefix( prefix ) );
    }

    [Theory]
    [InlineData( "^FS" )]
    [InlineData( "~JA" )]
    [InlineData( "A^B" )]
    [InlineData( "has space" )]
    [InlineData( "toolongprefix" )]
    public void IsUsablePrefix_RejectsAnythingAPrinterWouldReadAsACommand( string prefix )
    {
        // A caret or tilde inside field data starts a command. A prefix
        // containing either would not print - it would change the label.
        Assert.False( SecurityCode.IsUsablePrefix( prefix ) );
    }
}
