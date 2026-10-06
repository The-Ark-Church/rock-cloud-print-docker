// <copyright>
// Copyright by the Spark Development Network
//
// Licensed under the Rock Community License (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
// http://www.rockrms.com/license
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.
// </copyright>
//
namespace Rock.CloudPrint.Service;

/// <summary>
/// Stops another web page from making the proxy do things.
///
/// <para>
/// A page on any site can have a visitor's browser send a "simple" POST to
/// any address - one with a text/plain, form or multipart body - without
/// asking first, and the browser sends it even though the page is not
/// allowed to read the answer. The JSON endpoints already turn those away
/// (a text/plain body is not JSON, so they answer 415), but the endpoints
/// that take no body do not look at one: restart, send a test notification,
/// disarm or discard a capture. With no PIN set, any page somebody on the
/// network happened to open could restart the proxy in the middle of a
/// check-in.
/// </para>
///
/// <para>
/// Requiring a header of our own closes that for every endpoint at once,
/// including ones added later. A browser will only add a custom header to a
/// request to another site after a CORS preflight, and the proxy does not
/// answer preflights, so the request is never sent. The value is not looked
/// at: being able to send the header at all is the proof. This is not
/// authentication - anything that is not a browser can send it - which is
/// why the PIN still matters.
/// </para>
/// </summary>
internal static class ApiRequestHeader
{
    /// <summary>
    /// The header the web UI, and anything scripting the API, sends with
    /// every request that is not a GET or HEAD.
    /// </summary>
    public const string Name = "X-CloudPrint-Request";

    /// <summary>
    /// Whether a request must be turned away for want of the header.
    /// </summary>
    /// <param name="method">The request's HTTP method.</param>
    /// <param name="path">The request's path.</param>
    /// <param name="hasHeader">Whether the request carries <see cref="Name"/>, with any value.</param>
    public static bool IsRefused( string method, PathString path, bool hasHeader )
    {
        // Static files, the page itself and /healthz sit outside /api and are
        // only ever read, so they are left exactly as they were. The match is
        // the same one the PIN check uses, so whatever that treats as the API
        // this does too.
        if ( !path.StartsWithSegments( "/api" ) )
            return false;

        // A GET or HEAD changes nothing, and a page that has the browser make
        // one cannot read the reply, so there is nothing to protect. Everything
        // else is refused rather than a list of methods allowed, so a method
        // nobody thought of is not a way around it. That includes OPTIONS:
        // refusing a preflight is what makes the browser give up.
        if ( HttpMethods.IsGet( method ) || HttpMethods.IsHead( method ) )
            return false;

        return !hasHeader;
    }
}

/// <summary>
/// Answers 403 to an API request that would change something but does not
/// carry <see cref="ApiRequestHeader.Name"/>. See <see cref="ApiRequestHeader"/>
/// for why.
/// </summary>
internal sealed class ApiRequestHeaderMiddleware
{
    private readonly RequestDelegate _next;

    public ApiRequestHeaderMiddleware( RequestDelegate next )
    {
        _next = next;
    }

    public async Task InvokeAsync( HttpContext context )
    {
        var hasHeader = context.Request.Headers.ContainsKey( ApiRequestHeader.Name );

        if ( ApiRequestHeader.IsRefused( context.Request.Method, context.Request.Path, hasHeader ) )
        {
            // Worded for whoever is scripting the API and wonders why a call
            // that works from the web UI does not work from theirs; a browser
            // that was refused never shows it to anyone.
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync( new
            {
                error = $"Requests that change something must include the {ApiRequestHeader.Name} header (any value). The web UI sends it; add it to your own scripts."
            } );
            return;
        }

        await _next( context );
    }
}
