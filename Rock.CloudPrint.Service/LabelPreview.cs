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
using System.Globalization;
using System.Net.Http.Headers;

namespace Rock.CloudPrint.Service;

/// <summary>A rendered label, or why there is not one.</summary>
internal sealed record LabelPreviewResult
{
    public byte[]? Png { get; init; }

    public int WidthDots { get; init; }

    public int LengthDots { get; init; }

    public string? Error { get; init; }

    public bool Ok => Png != null;
}

/// <summary>
/// Turns a stored template into a picture of the label it would print.
///
/// <para>
/// The rendering is done by Labelary, which is the same service Rock's own
/// label designer uses. That does mean this one feature needs the internet
/// while everything else about blank labels deliberately does not - but a
/// preview is a convenience, and if there is no internet there are larger
/// problems than a missing picture. Printing is unaffected either way.
/// </para>
///
/// <para>
/// It is rendered on this side rather than in the browser because the web UI's
/// content security policy allows images only from this service and from
/// <c>data:</c> URLs. Relaxing that to permit a third-party origin would undo
/// a deliberate tightening - the stylesheet was bundled into the image
/// precisely to remove the last external origin - so the picture comes back as
/// bytes and the browser never contacts anyone but the proxy.
/// </para>
/// </summary>
internal sealed class LabelPreview
{
    /// <summary>
    /// The print density the size is read against. 8 dots per millimetre is
    /// 203 dots per inch, which is what the supplied templates and the
    /// printers they are printed on use, and what <c>^PW</c> and <c>^LL</c>
    /// are counted in.
    /// </summary>
    public const int Dpi = 203;

    /// <summary>
    /// Used to make up a sample code. A row of X is unmistakably not a real
    /// code, and X is one of the widest characters in the alphabet, so a code
    /// that fits as X will fit as anything.
    /// </summary>
    public const char SampleCharacter = 'X';

    /// <summary>
    /// What to render when the template does not declare a size. Labelary's
    /// own default, and the commonest label stock.
    /// </summary>
    private const double FallbackWidthInches = 4;
    private const double FallbackHeightInches = 6;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<LabelPreview> _logger;

    public LabelPreview( IHttpClientFactory httpClientFactory, ILogger<LabelPreview> logger )
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// A sample code of the given length.
    /// </summary>
    public static string SampleCode( int length )
    {
        return new string( SampleCharacter, Math.Clamp( length, 1, SecurityCode.MaxLength ) );
    }

    /// <summary>
    /// Renders one label.
    /// </summary>
    /// <param name="template">The stored template.</param>
    /// <param name="code">The code to show in it.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    public async Task<LabelPreviewResult> RenderAsync( byte[] template, string code, CancellationToken cancellationToken )
    {
        var resolved = ZplTemplate.Resolve( template, code );
        var (widthDots, lengthDots) = ZplTemplate.ReadSize( resolved );

        // Only the last format is sent. A template usually opens with a short
        // configuration block - the supplied ones all do - and sending the
        // whole file would leave the renderer's "which label of the file"
        // index meaning something different for every template. One format in
        // means index zero is always the right answer.
        //
        // This is a picture, not the byte stream the printer is given. What is
        // printed is always the whole template.
        var format = ZplTemplate.LastFormat( resolved );

        var url = string.Format( CultureInfo.InvariantCulture,
            "https://api.labelary.com/v1/printers/8dpmm/labels/{0}x{1}/0/",
            Inches( widthDots, FallbackWidthInches ),
            Inches( lengthDots, FallbackHeightInches ) );

        using var request = new HttpRequestMessage( HttpMethod.Post, url );

        // Raw ZPL as the body, which is what the service expects, oddly
        // labelled as a form.
        request.Content = new ByteArrayContent( format );
        request.Content.Headers.ContentType = new MediaTypeHeaderValue( "application/x-www-form-urlencoded" );

        try
        {
            // No time limit is set here, and none should be. The same reasoning
            // as everywhere else in this feature: a person is sat waiting for
            // this, a slow answer costs printing nothing at all, and a number
            // picked for how long is too long would be a guess.
            var client = _httpClientFactory.CreateClient( "labelary" );
            using var response = await client.SendAsync( request, cancellationToken );
            var body = await response.Content.ReadAsByteArrayAsync( cancellationToken );

            if ( !response.IsSuccessStatusCode )
            {
                return new LabelPreviewResult
                {
                    WidthDots = widthDots,
                    LengthDots = lengthDots,
                    Error = Describe( response, body )
                };
            }

            return new LabelPreviewResult
            {
                Png = body,
                WidthDots = widthDots,
                LengthDots = lengthDots
            };
        }
        catch ( OperationCanceledException ) when ( cancellationToken.IsCancellationRequested )
        {
            throw;
        }
        catch ( Exception ex )
        {
            // Named explicitly so that "the preview is broken" and "the proxy
            // cannot reach the internet" are not the same message.
            _logger.LogWarning( ex, "Could not reach Labelary to render a preview." );

            return new LabelPreviewResult
            {
                WidthDots = widthDots,
                LengthDots = lengthDots,
                Error = $"Could not reach Labelary, the service that draws the preview. {ex.Message}"
            };
        }
    }

    /// <summary>
    /// Describes a refusal, preferring the renderer's own explanation - which
    /// is usually something specific about the ZPL and worth passing on.
    /// </summary>
    private static string Describe( HttpResponseMessage response, byte[] body )
    {
        if ( response.Headers.TryGetValues( "X-Error", out var reported ) )
        {
            var message = string.Join( " ", reported ).Trim();

            if ( message.Length > 0 )
            {
                return $"Labelary could not draw this label: {message}";
            }
        }

        var text = ZplTemplate.ByteEncoding.GetString( body ).Trim();

        return text.Length > 0 && text.Length < 300
            ? $"Labelary could not draw this label: {text}"
            : $"Labelary could not draw this label (HTTP {( int ) response.StatusCode}).";
    }

    /// <summary>
    /// Converts a measurement in dots to the inches the renderer asks for.
    /// </summary>
    private static string Inches( int dots, double fallback )
    {
        var inches = dots / ( double ) Dpi;

        if ( inches < 0.1 )
        {
            inches = fallback;
        }

        return Math.Round( inches, 2 ).ToString( "0.##", CultureInfo.InvariantCulture );
    }
}
