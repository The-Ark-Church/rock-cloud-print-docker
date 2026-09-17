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
using System.Text.RegularExpressions;

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
    /// Also accepted, but only when it is the whole of a field.
    ///
    /// <para>
    /// This is what Rock's own legacy check-in labels use as their security
    /// code placeholder, so it is what somebody designing a label in Rock
    /// reaches for. Recognising it means a label authored there works without
    /// being edited afterwards.
    /// </para>
    ///
    /// <para>
    /// Whole field only, and that restriction is the point. Three letters are
    /// far likelier to appear by accident than three question marks - a field
    /// reading <c>www.example.com</c> would otherwise have a security code
    /// substituted into the middle of it. Nothing legitimate has a field
    /// containing only these three letters except a placeholder.
    /// </para>
    /// </summary>
    public const string LegacyCodeToken = "WWW";

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
            if ( IsCodePosition( text.Substring( field.Start, field.Length ) ) )
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

            var value = text.Substring( field.Start, field.Length );

            // A field that is nothing but the legacy token is replaced whole.
            // Anything else has the token substituted where it appears, which
            // leaves the rest of the field - captions, punctuation, a prefix
            // somebody typed around it - exactly as written.
            builder.Append( IsWholeFieldLegacyToken( value )
                ? code
                : value.Replace( CodeToken, code, StringComparison.Ordinal ) );

            copied = field.Start + field.Length;
        }

        builder.Append( text, copied, text.Length - copied );

        return ByteEncoding.GetBytes( builder.ToString() );
    }

    /// <summary>
    /// Whether a field is somewhere a security code goes.
    /// </summary>
    private static bool IsCodePosition( string value )
    {
        return value.Contains( CodeToken, StringComparison.Ordinal )
            || IsWholeFieldLegacyToken( value );
    }

    /// <summary>
    /// Whether the whole of a field is the legacy token and nothing else.
    /// </summary>
    private static bool IsWholeFieldLegacyToken( string value )
    {
        // ZPL writes a line break in field data as \&, and a rendered label
        // can carry one either side of the value, so those come off first.
        var trimmed = value.Replace( "\\&", string.Empty ).Trim();

        return trimmed.Equals( LegacyCodeToken, StringComparison.Ordinal );
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
    /// One <c>^FD…^FS</c> field of a label, as a person needs to see it in
    /// order to say which one holds the security code.
    /// </summary>
    /// <param name="Index">Its position in the label, counting from zero.</param>
    /// <param name="Text">What it prints. Empty is normal and meaningful.</param>
    /// <param name="FontHeight">
    /// The height in dots of the font set before it, or zero if none was.
    /// This is usually what identifies the code: it is the one thing on a
    /// check-in label printed large, so on a real label it stands out from the
    /// captions by a factor of three or four.
    /// </param>
    public record ZplField( int Index, string Text, int FontHeight );

    /// <summary>
    /// Every field in the label, in order.
    ///
    /// <para>
    /// Not deduplicated and not filtered, unlike an earlier version of this.
    /// Both mattered: a receipt torn in half carries the same code twice and
    /// both must be selectable, and a field can legitimately be empty - which
    /// is exactly what a security code looks like on a label Rock rendered
    /// without an attendance behind it.
    /// </para>
    /// </summary>
    public static IReadOnlyList<ZplField> Fields( ReadOnlySpan<byte> content )
    {
        var text = ByteEncoding.GetString( content );
        var fields = new List<ZplField>();
        var searchedTo = 0;

        foreach ( var field in DataFields( text ) )
        {
            var value = text.Substring( field.Start, field.Length );

            // ZPL writes a line break inside field data as \&. It carries no
            // meaning here and makes an empty field look like it holds
            // something, so it is taken out for display only.
            var display = value.Replace( "\\&", " " ).Replace( "\\*", " " ).Trim();

            fields.Add( new ZplField( fields.Count, display, FontHeightBefore( text, searchedTo, field.Start ) ) );

            searchedTo = field.Start + field.Length;
        }

        return fields;
    }

    /// <summary>
    /// Replaces the contents of the given fields with the code token, so a
    /// captured label becomes a template.
    ///
    /// <para>
    /// By position rather than by matching text. Matching text cannot work on a
    /// captured label: the security code field is often empty, and whatever
    /// stands in for empty appears in other fields too - so replacing it would
    /// rewrite the wrong ones. Position is unambiguous, and it lets both halves
    /// of a torn receipt be marked.
    /// </para>
    /// </summary>
    /// <param name="content">The captured label.</param>
    /// <param name="indexes">Which fields hold the security code.</param>
    public static byte[] MarkCodeFields( ReadOnlySpan<byte> content, IReadOnlyCollection<int> indexes )
    {
        if ( indexes.Count == 0 )
        {
            return content.ToArray();
        }

        var text = ByteEncoding.GetString( content );
        var builder = new StringBuilder( text.Length + 16 );
        var copied = 0;
        var index = 0;

        foreach ( var field in DataFields( text ) )
        {
            builder.Append( text, copied, field.Start - copied );

            // The whole field becomes the token. What was there was either a
            // rendered value from whatever Rock printed, or nothing at all;
            // neither belongs on a blank.
            builder.Append( indexes.Contains( index )
                ? CodeToken
                : text.Substring( field.Start, field.Length ) );

            copied = field.Start + field.Length;
            index++;
        }

        builder.Append( text, copied, text.Length - copied );

        return ByteEncoding.GetBytes( builder.ToString() );
    }

    /// <summary>
    /// The height of the last font command before a field, in dots.
    ///
    /// <para>
    /// Matches <c>^A0N,128,112</c> and its relatives - a font letter, an
    /// optional orientation, then height and width. Only what falls between
    /// the previous field and this one is searched, so each field reports the
    /// font actually in force for it.
    /// </para>
    /// </summary>
    private static int FontHeightBefore( string text, int searchFrom, int fieldStart )
    {
        if ( fieldStart <= searchFrom )
        {
            return 0;
        }

        var between = text.Substring( searchFrom, fieldStart - searchFrom );
        var matches = FontCommand.Matches( between );

        return matches.Count > 0 && int.TryParse( matches[^1].Groups[1].Value, out var height )
            ? height
            : 0;
    }

    /// <summary>
    /// A scalable or bitmap font selection: <c>^A</c>, the font, an optional
    /// orientation letter, then height and width.
    /// </summary>
    private static readonly Regex FontCommand = new( @"\^A[0-9A-Za-z]?[NRIB]?,(\d+),(\d+)", RegexOptions.Compiled );

    /// <summary>
    /// Ends a label with a cut, or with the cut suppressed, instead of
    /// whatever the template said.
    ///
    /// <para>
    /// This is how Rock does it, and the commands are taken from Rock's own
    /// <c>LabelPrintProvider</c> so that a printer receives exactly what it
    /// receives during check-in. The trailing <c>^XZ</c> is replaced with
    /// <c>^MMC^XZ</c> to cut, or <c>^XB^XZ</c> to suppress the backfeed - which
    /// suppresses the cut with it - on every label of a copy but the last.
    /// </para>
    ///
    /// <para>
    /// Appending rather than rewriting means the template's own <c>^MMT</c> or
    /// <c>^MMC</c> does not have to be found or removed: ZPL takes commands in
    /// order, so whichever comes last wins, and this comes last.
    /// </para>
    ///
    /// <para>
    /// Rock's version sizes its buffer one byte short and overwrites the
    /// character before the final caret, which on a label ending <c>^FS^XZ</c>
    /// clips the <c>S</c>. That is not reproduced here.
    /// </para>
    /// </summary>
    /// <param name="content">The resolved label.</param>
    /// <param name="cut">Whether this label should end with a cut.</param>
    public static byte[] AmendForCutter( byte[] content, bool cut )
    {
        var text = ByteEncoding.GetString( content );
        var end = text.LastIndexOf( "^XZ", StringComparison.Ordinal );

        if ( end < 0 )
        {
            // Nothing that looks like the end of a label. Send it untouched
            // rather than appending commands to something not understood.
            return content;
        }

        var replacement = ByteEncoding.GetBytes( ( cut ? "^MMC^XZ" : "^XB^XZ" ) + "\r\n" );
        var amended = new byte[end + replacement.Length];

        Array.Copy( content, 0, amended, 0, end );
        Array.Copy( replacement, 0, amended, end, replacement.Length );

        return amended;
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
