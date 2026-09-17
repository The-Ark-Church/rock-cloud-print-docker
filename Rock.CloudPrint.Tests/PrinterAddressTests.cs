using System.Net;

using Rock.CloudPrint.Service;

using Xunit;

namespace Rock.CloudPrint.Tests;

/// <summary>
/// Pins the behaviour of the address parsing that both the print path and the
/// web UI's connection test run through.
///
/// Most of this looks like it is testing the obvious. It is not: the last two
/// facts here are the ones that matter. The exception message reaches the Rock
/// server as the print result and is displayed on the check-in screen, and the
/// port fallback is what stops a malformed address from failing in some new
/// way. Both are behaviour inherited from upstream and both must survive any
/// refactoring of this class.
/// </summary>
public class PrinterAddressTests
{
    [Fact]
    public void Parse_WithoutPort_UsesTheRawPrintingDefault()
    {
        var address = PrinterAddress.Parse( "10.0.0.5" );

        Assert.Equal( IPAddress.Parse( "10.0.0.5" ), address.Address );
        Assert.Equal( 9100, address.Port );
    }

    [Theory]
    [InlineData( "10.0.0.5:9100", 9100 )]
    [InlineData( "10.0.0.5:1234", 1234 )]
    public void Parse_WithPort_UsesIt( string input, int expectedPort )
    {
        Assert.Equal( expectedPort, PrinterAddress.Parse( input ).Port );
    }

    [Theory]
    [InlineData( "10.0.0.5:" )]
    [InlineData( "10.0.0.5:abc" )]
    public void Parse_WithAnUnusablePort_FallsBackToTheDefault( string input )
    {
        // Upstream behaviour. A typo in the port silently prints to 9100
        // rather than failing, and changing that would change what happens on
        // a real check-in station.
        Assert.Equal( 9100, PrinterAddress.Parse( input ).Port );
    }

    [Fact]
    public void Parse_KeepsTheAddressExactlyAsSupplied()
    {
        // The original string is what gets logged and reported back, so it
        // must not be normalised on the way through.
        Assert.Equal( "10.0.0.5:1234", PrinterAddress.Parse( "10.0.0.5:1234" ).Original );
    }

    [Fact]
    public void Parse_WithAHostName_ThrowsTheMessageCheckInDisplays()
    {
        // Host names are rejected rather than resolved. This exact string is
        // returned to Rock as the print result and shown to whoever is stood
        // at the kiosk, so it is part of the product's behaviour and not an
        // implementation detail.
        var ex = Assert.Throws<FormatException>( () => PrinterAddress.Parse( "printer.local" ) );

        Assert.Equal( "An invalid IP address was specified.", ex.Message );
    }

    [Fact]
    public void ToEndPoint_WithAnOutOfRangePort_ThrowsThereRatherThanDuringParse()
    {
        // Deliberate, and documented in PrinterAddress: validation happens
        // when the endpoint is built, which is where it happened before the
        // parsing was lifted out of the print path.
        var address = PrinterAddress.Parse( "10.0.0.5:99999" );

        Assert.Equal( 99999, address.Port );
        Assert.Throws<ArgumentOutOfRangeException>( () => address.ToEndPoint() );
    }
}
