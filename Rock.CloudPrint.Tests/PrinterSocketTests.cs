using System.Net;
using System.Net.Sockets;

using Rock.CloudPrint.Service;

using Xunit;

namespace Rock.CloudPrint.Tests;

/// <summary>
/// The shared way of opening a printer connection. Two facts, because there
/// are only two outcomes: a socket, or the reason there isn't one.
/// </summary>
public class PrinterSocketTests
{
    [Fact]
    public async Task OpenAsync_WhenSomethingIsListening_ReturnsAConnectedSocket()
    {
        var listener = new TcpListener( IPAddress.Loopback, 0 );
        listener.Start();

        try
        {
            var port = ( ( IPEndPoint ) listener.LocalEndpoint ).Port;

            using var socket = await PrinterSocket.OpenAsync(
                PrinterAddress.Parse( $"127.0.0.1:{port}" ),
                CancellationToken.None );

            Assert.True( socket.Connected );
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task OpenAsync_WhenNothingIsListening_ThrowsRatherThanReturningADeadSocket()
    {
        // Bind to get a port the operating system says is free, then let it go.
        // Anything connecting to it now is refused.
        var listener = new TcpListener( IPAddress.Loopback, 0 );
        listener.Start();
        var port = ( ( IPEndPoint ) listener.LocalEndpoint ).Port;
        listener.Stop();

        var ex = await Assert.ThrowsAsync<SocketException>( () =>
            PrinterSocket.OpenAsync( PrinterAddress.Parse( $"127.0.0.1:{port}" ),
                                     CancellationToken.None ) );

        // The message travels: for the connection test it is shown in the web
        // UI, and for a print it is what the check-in screen displays.
        Assert.Equal( SocketError.ConnectionRefused, ex.SocketErrorCode );
    }
}
