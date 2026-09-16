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
using System.Text;

namespace Rock.CloudPrint.Service;

/// <summary>
/// Reads the few things the proxy needs to know about a stored ZPL label.
///
/// <para>
/// It knows almost nothing about ZPL, on purpose. Labels are authored
/// elsewhere - in Rock's designer, in Zebra's, or in a text editor - and the
/// proxy's job is to store them and send them unchanged. The only structure it
/// has to find is where the security code goes.
/// </para>
/// </summary>
internal static class ZplTemplate
{
    /// <summary>
    /// What a template puts where the security code should appear. The same
    /// token in every template: a per-template token would be configuration to
    /// set, get wrong, and support, and nothing in Phase 1 needs one.
    /// </summary>
    public const string CodeToken = "???";

    /// <summary>
    /// Latin-1 maps every one of the 256 byte values to exactly one character
    /// and back again, so a template can be searched and edited as text with
    /// no byte altered anywhere the edit did not touch. UTF-8 would not: a
    /// template containing <c>^GF</c> graphics, or text under <c>^CI0</c>,
    /// carries bytes that are not valid UTF-8 and would come back as
    /// replacement characters.
    /// </summary>
    public static readonly Encoding ByteEncoding = Encoding.Latin1;

    /// <summary>
    /// Whether the content looks like ZPL at all. A cheap check that catches
    /// the common mistake of uploading a PDF, an image, or a label designer's
    /// own project file.
    /// </summary>
    public static bool LooksLikeZpl( ReadOnlySpan<byte> content )
    {
        var text = ByteEncoding.GetString( content );

        return text.Contains( "^XA", StringComparison.Ordinal )
            && text.Contains( "^XZ", StringComparison.Ordinal );
    }

    /// <summary>
    /// Whether the code token appears somewhere it will actually be printed.
    /// A template without one would print a stack of labels carrying no
    /// security code at all, which is the one thing a blank must have.
    /// </summary>
    public static bool ContainsCodeToken( ReadOnlySpan<byte> content )
    {
        var text = ByteEncoding.GetString( content );

        foreach ( var field in DataFields( text ) )
        {
            if ( text.AsSpan( field.Start, field.Length )
                     .Contains( CodeToken, StringComparison.Ordinal ) )
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Produces the bytes for one copy: the template with the security code
    /// put where the token is.
    ///
    /// <para>
    /// Substitution happens only inside <c>^FD…^FS</c> blocks, and every other
    /// byte of the template is copied across untouched. That is the whole
    /// design: the printer receives the label its author wrote, with one field
    /// filled in. Nothing rewrites <c>^PQ</c>, nothing rewrites <c>^MM</c>,
    /// nothing rewrites the size - so the cut behaviour and the stock the
    /// label was designed for are preserved by construction rather than by
    /// remembering to preserve them.
    /// </para>
    ///
    /// <para>
    /// A template with no token comes back unchanged. Uploads are checked for
    /// one, so that means a file put in place by hand, and printing it as it
    /// stands is more useful than refusing at the moment somebody presses
    /// print.
    /// </para>
    /// </summary>
    /// <param name="content">The stored template.</param>
    /// <param name="code">The security code for this copy.</param>
    public static byte[] Resolve( ReadOnlySpan<byte> content, string code )
    {
        var text = ByteEncoding.GetString( content );
        var builder = new StringBuilder( text.Length + 16 );
        var copied = 0;

        foreach ( var field in DataFields( text ) )
        {
            // Everything between the end of the last field and the start of
            // this one, verbatim.
            builder.Append( text, copied, field.Start - copied );

            builder.Append( text.AsSpan( field.Start, field.Length )
                                .ToString()
                                .Replace( CodeToken, code, StringComparison.Ordinal ) );

            copied = field.Start + field.Length;
        }

        builder.Append( text, copied, text.Length - copied );

        return ByteEncoding.GetBytes( builder.ToString() );
    }

    /// <summary>
    /// The printable width and length in dots, from the last <c>^PW</c> and
    /// <c>^LL</c> in the template. Zero when the template does not say.
    ///
    /// <para>
    /// This is only ever shown to a person, so they can tell a 3x2 template
    /// from a 4x6 before loading the wrong stock. Nothing decides anything on
    /// it, and nothing rewrites it: the size is the label author's business
    /// and is baked into the file.
    /// </para>
    /// </summary>
    public static (int WidthDots, int LengthDots) ReadSize( ReadOnlySpan<byte> content )
    {
        var text = ByteEncoding.GetString( content );

        return (ReadLastNumber( text, "^PW" ), ReadLastNumber( text, "^LL" ));
    }

    /// <summary>
    /// The last <c>^XA…^XZ</c> format in the template, or the whole thing if
    /// there is not a complete one.
    ///
    /// <para>
    /// A ZPL file often opens with a short configuration format before the
    /// label itself - all three supplied templates do - so "the label" is the
    /// last one, not the first. Only the preview uses this. What is sent to a
    /// printer is always the whole template, byte for byte, because those
    /// opening formats are setting the printer up.
    /// </para>
    /// </summary>
    public static byte[] LastFormat( byte[] content )
    {
        var text = ByteEncoding.GetString( content );
        var end = text.LastIndexOf( "^XZ", StringComparison.Ordinal );

        if ( end < 0 )
        {
            return content;
        }

        end += 3;

        var start = text.LastIndexOf( "^XA", end - 3, StringComparison.Ordinal );

        if ( start < 0 )
        {
            return content;
        }

        var format = new byte[end - start];

        Array.Copy( content, start, format, 0, format.Length );

        return format;
    }

    /// <summary>
    /// Locates every <c>^FD…^FS</c> block - the parts of a label that carry
    /// data rather than layout. Substitution happens only inside these, so
    /// nothing else in the template can be altered by accident.
    ///
    /// <para>
    /// A <c>^FX</c> comment is not a data field, so a token written inside one
    /// as a note to a future editor is left alone. That matches how a printer
    /// reads it too: a comment runs until the next caret command, so anything
    /// genuinely inside one is never printed.
    /// </para>
    ///
    /// <para>
    /// Assumes the default <c>^</c> command prefix. A template that changes it
    /// with <c>^CC</c> would need more than this, and nothing in practice
    /// does - the supplied templates set the prefix back to <c>^</c> in their
    /// own preamble.
    /// </para>
    /// </summary>
    /// <param name="text">The template, decoded with <see cref="ByteEncoding"/>.</param>
    public static IEnumerable<(int Start, int Length)> DataFields( string text )
    {
        var index = 0;

        while ( index < text.Length )
        {
            var open = text.IndexOf( "^FD", index, StringComparison.Ordinal );

            if ( open < 0 )
            {
                yield break;
            }

            var dataStart = open + 3;
            var close = text.IndexOf( "^FS", dataStart, StringComparison.Ordinal );

            if ( close < 0 )
            {
                // An unterminated field. The printer would not print it either,
                // so there is nothing sensible to substitute into.
                yield break;
            }

            yield return (dataStart, close - dataStart);

            index = close + 3;
        }
    }

    /// <summary>
    /// Reads the digits following the last occurrence of a command.
    /// </summary>
    private static int ReadLastNumber( string text, string command )
    {
        var at = text.LastIndexOf( command, StringComparison.Ordinal );

        if ( at < 0 )
        {
            return 0;
        }

        var start = at + command.Length;
        var end = start;

        while ( end < text.Length && char.IsAsciiDigit( text[end] ) )
        {
            end++;
        }

        return end > start && int.TryParse( text.AsSpan( start, end - start ), out var value )
            ? value
            : 0;
    }
}
