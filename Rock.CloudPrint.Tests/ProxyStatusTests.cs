using Rock.CloudPrint.Service;

using Xunit;

namespace Rock.CloudPrint.Tests;

/// <summary>
/// The per-connection count is what the dashboard uses to suggest this proxy
/// is standing by behind another, so it has to start again with every
/// connection while the lifetime figures carry on.
/// </summary>
public class ProxyStatusTests
{
    [Fact]
    public void AddLabels_CountsTowardsTheConnectionAndTheTotal()
    {
        var status = new ProxyStatus();
        status.SetConnected( true );

        status.AddLabels( 2 );
        status.AddLabels( 3 );

        Assert.Equal( 5, status.LabelsThisConnection );
        Assert.Equal( 5, status.TotalPrinted );
        Assert.NotNull( status.LastPrintDateTime );
    }

    [Fact]
    public void Reconnecting_StartsTheConnectionCountAgainButKeepsTheRest()
    {
        var status = new ProxyStatus();
        status.SetConnected( true );
        status.AddLabels( 4 );
        var lastPrint = status.LastPrintDateTime;

        status.SetConnected( false );
        status.SetConnected( true );

        Assert.Equal( 0, status.LabelsThisConnection );
        Assert.Equal( 4, status.TotalPrinted );
        Assert.Equal( lastPrint, status.LastPrintDateTime );
    }

    [Fact]
    public void LastPrint_IsEmptyUntilSomethingIsPrinted()
    {
        var status = new ProxyStatus();
        status.SetConnected( true );

        Assert.Null( status.LastPrintDateTime );
        Assert.Equal( 0, status.LabelsThisConnection );
    }
}
