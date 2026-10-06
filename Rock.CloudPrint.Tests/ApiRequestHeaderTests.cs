using System.Text.Json;

using Microsoft.AspNetCore.Http;

using Rock.CloudPrint.Service;

using Xunit;

namespace Rock.CloudPrint.Tests;

/// <summary>
/// The header that stops another web page from having a visitor's browser
/// restart the proxy, send a test notification, or change anything else.
///
/// <para>
/// Two ways this goes wrong: an endpoint that changes something is reachable
/// without the header, which reopens the hole; or something that should never
/// need it - the page, its scripts, the health check, any read - starts
/// needing it, which breaks the web UI or Docker's health check.
/// </para>
/// </summary>
public class ApiRequestHeaderTests
{
    [Theory]
    [InlineData( "POST", "/api/restart" )]
    [InlineData( "POST", "/api/notifications/test" )]
    [InlineData( "POST", "/api/labels/capture/disarm" )]
    [InlineData( "POST", "/api/labels/capture/discard" )]
    [InlineData( "POST", "/api/settings" )]
    [InlineData( "DELETE", "/api/printers/front-desk" )]
    [InlineData( "POST", "/api/auth/logout" )]
    [InlineData( "POST", "/api/auth/login" )]
    public void RefusesAChangeWithoutTheHeader( string method, string path )
    {
        Assert.True( ApiRequestHeader.IsRefused( method, path, hasHeader: false ) );
    }

    [Theory]
    [InlineData( "POST", "/api/restart" )]
    [InlineData( "DELETE", "/api/labels/sample" )]
    [InlineData( "POST", "/api/auth/login" )]
    public void AllowsAChangeWithTheHeader( string method, string path )
    {
        Assert.False( ApiRequestHeader.IsRefused( method, path, hasHeader: true ) );
    }

    [Theory]
    [InlineData( "PUT" )]
    [InlineData( "PATCH" )]
    [InlineData( "OPTIONS" )]
    [InlineData( "PROPFIND" )]
    public void RefusesMethodsNothingUsesYet( string method )
    {
        // Refusing everything but GET and HEAD, rather than listing the methods
        // to check, means an endpoint added later with another method is
        // covered without anyone remembering to add it here. OPTIONS in
        // particular must be refused: that is the preflight, and refusing it is
        // what makes the browser abandon the real request.
        Assert.True( ApiRequestHeader.IsRefused( method, "/api/restart", hasHeader: false ) );
    }

    [Theory]
    [InlineData( "GET", "/api/status" )]
    [InlineData( "GET", "/api/auth/config" )]
    [InlineData( "HEAD", "/api/status" )]
    [InlineData( "get", "/api/status" )]
    public void LeavesReadsAlone( string method, string path )
    {
        Assert.False( ApiRequestHeader.IsRefused( method, path, hasHeader: false ) );
    }

    [Theory]
    [InlineData( "GET", "/healthz" )]
    [InlineData( "GET", "/" )]
    [InlineData( "GET", "/app.js" )]
    [InlineData( "POST", "/healthz" )]
    [InlineData( "POST", "/apiary" )]
    public void LeavesEverythingOutsideTheApiAlone( string method, string path )
    {
        // "/apiary" is there because a prefix match on the string would catch
        // it; the check must match whole path segments, as the PIN check does.
        Assert.False( ApiRequestHeader.IsRefused( method, path, hasHeader: false ) );
    }

    [Theory]
    [InlineData( "post", "/api/restart" )]
    [InlineData( "POST", "/API/restart" )]
    [InlineData( "POST", "/Api/Restart" )]
    public void IgnoresCase( string method, string path )
    {
        // Routing matches /API/restart to the same endpoint as /api/restart,
        // so the check has to as well or changing the case would get round it.
        Assert.True( ApiRequestHeader.IsRefused( method, path, hasHeader: false ) );
    }

    [Fact]
    public async Task Middleware_AnswersForbiddenWithAReasonAndGoesNoFurther()
    {
        var reached = false;
        var middleware = new ApiRequestHeaderMiddleware( _ =>
        {
            reached = true;
            return Task.CompletedTask;
        } );

        var context = Request( "POST", "/api/restart" );
        context.Request.ContentType = "text/plain";

        await middleware.InvokeAsync( context );

        Assert.False( reached );
        Assert.Equal( StatusCodes.Status403Forbidden, context.Response.StatusCode );

        context.Response.Body.Position = 0;
        using var body = await JsonDocument.ParseAsync( context.Response.Body );
        Assert.Contains( ApiRequestHeader.Name, body.RootElement.GetProperty( "error" ).GetString() );
    }

    [Theory]
    [InlineData( "1" )]
    [InlineData( "" )]
    [InlineData( "anything" )]
    public async Task Middleware_PassesARequestWithTheHeaderWhateverItsValue( string value )
    {
        // Only a page on this proxy's own address can send the header at all,
        // so its value proves nothing more and is not looked at.
        var reached = false;
        var middleware = new ApiRequestHeaderMiddleware( _ =>
        {
            reached = true;
            return Task.CompletedTask;
        } );

        var context = Request( "POST", "/api/restart" );
        context.Request.Headers[ApiRequestHeader.Name] = value;

        await middleware.InvokeAsync( context );

        Assert.True( reached );
        Assert.Equal( StatusCodes.Status200OK, context.Response.StatusCode );
    }

    [Fact]
    public async Task Middleware_PassesAReadWithoutTheHeader()
    {
        var reached = false;
        var middleware = new ApiRequestHeaderMiddleware( _ =>
        {
            reached = true;
            return Task.CompletedTask;
        } );

        await middleware.InvokeAsync( Request( "GET", "/api/status" ) );

        Assert.True( reached );
    }

    private static DefaultHttpContext Request( string method, string path )
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();
        return context;
    }
}
