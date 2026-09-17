using Microsoft.Extensions.Logging.Abstractions;

using Rock.CloudPrint.Service;

using Xunit;

namespace Rock.CloudPrint.Tests;

/// <summary>
/// The printers somebody has named.
///
/// <para>
/// This is a convenience and nothing in the print path reads it, so the tests
/// worth having are about the two ways it could quietly mislead: offering an
/// address that cannot be printed to, and losing track of which name belongs
/// to which machine.
/// </para>
/// </summary>
public class PrinterBookTests : IDisposable
{
    private readonly string _root = Path.Combine( Path.GetTempPath(), "printerbook-" + Guid.NewGuid().ToString( "n" ) );

    private string Path_ => System.IO.Path.Combine( _root, "config", "printers.json" );

    private PrinterBook NewBook() => new( Path_, NullLogger<PrinterBook>.Instance );

    public void Dispose()
    {
        if ( Directory.Exists( _root ) )
        {
            Directory.Delete( _root, recursive: true );
        }

        GC.SuppressFinalize( this );
    }

    [Fact]
    public void ANewInstallHasNoPrinters()
    {
        Assert.Empty( NewBook().List() );
    }

    [Fact]
    public void WhatIsSavedComesBackAfterARestart()
    {
        Assert.Equal( PrinterSaveOutcome.Saved, NewBook().Save( "Kids Check-in", "10.0.40.20", true ) );

        var printer = Assert.Single( NewBook().List() );

        Assert.Equal( "Kids Check-in", printer.Name );
        Assert.Equal( "10.0.40.20", printer.Address );
        Assert.True( printer.HasCutter );
    }

    [Fact]
    public void TheCutterIsRememberedPerPrinterAndNotShared()
    {
        var book = NewBook();

        book.Save( "Has one", "10.0.40.20", true );
        book.Save( "Has none", "10.0.40.21", false );

        // The whole reason it is stored here: somebody picking a printer by
        // name is exactly the person who would not know which is which.
        Assert.True( book.List().Single( p => p.Name == "Has one" ).HasCutter );
        Assert.False( book.List().Single( p => p.Name == "Has none" ).HasCutter );
    }

    [Fact]
    public void SavingTheSameNameAgainReplacesIt()
    {
        var book = NewBook();

        book.Save( "Welcome Desk", "10.0.40.20", false );
        book.Save( "Welcome Desk", "10.0.40.99", true );

        var printer = Assert.Single( book.List() );

        Assert.Equal( "10.0.40.99", printer.Address );
        Assert.True( printer.HasCutter );
    }

    [Fact]
    public void ANameThatDiffersOnlyByCaseIsTheSamePrinter()
    {
        var book = NewBook();

        book.Save( "Welcome Desk", "10.0.40.20", false );
        book.Save( "welcome desk", "10.0.40.99", false );

        Assert.Single( book.List() );
    }

    [Fact]
    public void PrintersComeBackInNameOrder()
    {
        var book = NewBook();

        book.Save( "Office Zebra", "10.0.40.9", false );
        book.Save( "Kids Check-in", "10.0.40.20", false );
        book.Save( "Welcome Desk", "10.0.40.21", false );

        Assert.Equal(
            new[] { "Kids Check-in", "Office Zebra", "Welcome Desk" },
            book.List().Select( p => p.Name ).ToArray() );
    }

    [Fact]
    public void DeletingForgetsIt()
    {
        var book = NewBook();

        book.Save( "Kids Check-in", "10.0.40.20", false );

        Assert.True( book.Delete( "kids check-in" ) );
        Assert.Empty( NewBook().List() );
    }

    [Fact]
    public void DeletingSomethingThatIsNotThereSaysSo()
    {
        Assert.False( NewBook().Delete( "Never existed" ) );
    }

    [Theory]
    [InlineData( "" )]
    [InlineData( "   " )]
    [InlineData( "Kids/Check-in" )]
    [InlineData( "Kids\\Check-in" )]
    [InlineData( "Desk?" )]
    public void ANameThatCouldNotTravelInAUrlIsRefused( string name )
    {
        Assert.Equal( PrinterSaveOutcome.InvalidName, NewBook().Save( name, "10.0.40.20", false ) );
    }

    [Fact]
    public void ANameLongerThanTheLimitIsRefused()
    {
        var name = new string( 'a', PrinterBook.MaxNameLength + 1 );

        Assert.Equal( PrinterSaveOutcome.InvalidName, NewBook().Save( name, "10.0.40.20", false ) );
    }

    [Theory]
    [InlineData( "" )]
    [InlineData( "printer.local" )]
    [InlineData( "not an address" )]
    [InlineData( "10.0.40.20:99999" )]
    public void AnAddressThePrintPathCouldNotUseIsRefused( string address )
    {
        // Refused here rather than at the moment somebody presses print, which
        // is the worse of the two places to find out.
        Assert.Equal( PrinterSaveOutcome.InvalidAddress, NewBook().Save( "Kids Check-in", address, false ) );
    }

    [Fact]
    public void AnAddressWithAPortIsKeptAsWritten()
    {
        var book = NewBook();

        Assert.Equal( PrinterSaveOutcome.Saved, book.Save( "On another port", "10.0.40.20:9101", false ) );
        Assert.Equal( "10.0.40.20:9101", Assert.Single( book.List() ).Address );
    }

    [Fact]
    public void NameAndAddressAreTrimmed()
    {
        var book = NewBook();

        book.Save( "  Kids Check-in  ", "  10.0.40.20  ", false );

        var printer = Assert.Single( book.List() );

        Assert.Equal( "Kids Check-in", printer.Name );
        Assert.Equal( "10.0.40.20", printer.Address );
    }

    [Fact]
    public void TheListStopsGrowingAtTheCap()
    {
        var book = NewBook();

        for ( var i = 0; i < PrinterBook.MaxPrinters; i++ )
        {
            Assert.Equal( PrinterSaveOutcome.Saved, book.Save( "Printer " + i, "10.0.40." + i, false ) );
        }

        Assert.Equal( PrinterSaveOutcome.TooMany, book.Save( "One too many", "10.0.41.1", false ) );

        // Replacing one that is already there is not growth, so it still works
        // at the cap. Otherwise a full list could never be corrected.
        Assert.Equal( PrinterSaveOutcome.Saved, book.Save( "Printer 0", "10.0.40.200", false ) );
    }

    [Fact]
    public void AnUnusablePrinterInTheFileIsLeftOutRatherThanOffered()
    {
        Directory.CreateDirectory( System.IO.Path.GetDirectoryName( Path_ )! );

        File.WriteAllText( Path_, """
            [
              { "name": "Good", "address": "10.0.40.20", "hasCutter": true },
              { "name": "Bad address", "address": "printer.local", "hasCutter": false },
              { "name": "Bad/name", "address": "10.0.40.21", "hasCutter": false }
            ]
            """ );

        // The file can be edited by hand. A name or address that cannot be used
        // is worse sitting in a dropdown than absent from it.
        Assert.Equal( "Good", Assert.Single( NewBook().List() ).Name );
    }

    [Fact]
    public void AnUnreadableFileCostsTheListAndNothingElse()
    {
        Directory.CreateDirectory( System.IO.Path.GetDirectoryName( Path_ )! );

        File.WriteAllText( Path_, "{ this is not json" );

        var book = NewBook();

        Assert.Empty( book.List() );

        // Nothing in the print path reads this, so a lost file costs somebody
        // some typing and must not stop them saving a printer again.
        Assert.Equal( PrinterSaveOutcome.Saved, book.Save( "Kids Check-in", "10.0.40.20", false ) );
        Assert.Single( NewBook().List() );
    }
}
