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
using System.Text.Json;

namespace Rock.CloudPrint.Service;

/// <summary>
/// A printer somebody has named, so it can be chosen without knowing where it
/// is.
/// </summary>
internal sealed record SavedPrinter
{
    /// <summary>What it is called. Unique, ignoring case.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Its address, in the same notation the print path takes.</summary>
    public string Address { get; init; } = string.Empty;

    /// <summary>
    /// Whether it has a cutter.
    ///
    /// <para>
    /// A property of the printer rather than of a run, which is why it is kept
    /// here. Somebody printing on a machine they have never seen should not
    /// have to know, and getting it wrong is not a small thing: without it a
    /// copy's labels come off as one uncut strip, and with it on a printer that
    /// has no cutter every label stops to wait for a cut that never happens.
    /// </para>
    /// </summary>
    public bool HasCutter { get; init; }
}

/// <summary>Why a printer could not be saved.</summary>
internal enum PrinterSaveOutcome
{
    /// <summary>Stored.</summary>
    Saved,

    /// <summary>The name is empty, too long, or has characters that are not allowed.</summary>
    InvalidName,

    /// <summary>The address is not one the print path could connect to.</summary>
    InvalidAddress,

    /// <summary>There are already as many saved printers as are kept.</summary>
    TooMany
}

/// <summary>
/// The printers somebody has named, in <c>config/printers.json</c>.
///
/// <para>
/// This exists because the address used to live in the browser's local
/// storage, which meant it was remembered for exactly one person on exactly
/// one machine. The second administrator to print blanks got an empty box and
/// had to go and find an IP address. Keeping the list on the proxy makes it
/// the same list for everybody who opens the page.
/// </para>
///
/// <para>
/// It is a convenience and nothing more. Nothing in the print path reads it -
/// a run is still given an address - so a lost or malformed file costs a
/// person some typing and cannot stop anything printing.
/// </para>
/// </summary>
internal sealed class PrinterBook
{
    /// <summary>
    /// Long enough for "Ark Kids Check-in Desk" and short enough to sit in a
    /// dropdown without being cut off.
    /// </summary>
    public const int MaxNameLength = 40;

    /// <summary>
    /// Bounds the address before it is parsed, so a large body costs a length
    /// check rather than the parser's time.
    /// </summary>
    public const int MaxAddressLength = 64;

    /// <summary>
    /// A church has a handful of label printers, not hundreds. The cap is here
    /// so a stuck script cannot grow the file without limit, not because
    /// anybody will reach it.
    /// </summary>
    public const int MaxPrinters = 24;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,

        // Somebody will eventually edit this by hand over SSH rather than
        // through the page, and one printer per line is what they want to see.
        WriteIndented = true
    };

    private readonly string _path;
    private readonly ILogger<PrinterBook> _logger;

    /// <summary>
    /// Saves and deletions arrive on web requests and can overlap. One lock
    /// over a list of a dozen items is the right size of answer.
    /// </summary>
    private readonly object _gate = new();

    private List<SavedPrinter> _printers = new();

    public PrinterBook( IWebHostEnvironment environment, ILogger<PrinterBook> logger )
        : this( Path.Combine( environment.ContentRootPath, "config", "printers.json" ), logger )
    {
    }

    /// <summary>
    /// Initializes a book over a specific file. Used by tests.
    /// </summary>
    public PrinterBook( string path, ILogger<PrinterBook> logger )
    {
        _path = Path.GetFullPath( path );
        _logger = logger;

        Load();
    }

    /// <summary>
    /// Whether a name may be given to a printer.
    ///
    /// <para>
    /// The same narrow set a label name is held to. A printer name is not a
    /// file name, so it does not have to be this strict - but it does travel
    /// in a URL when one is deleted, and having one rule for both is one thing
    /// to explain instead of two.
    /// </para>
    /// </summary>
    public static bool IsValidName( string? name )
    {
        if ( string.IsNullOrEmpty( name ) || name.Length > MaxNameLength )
        {
            return false;
        }

        foreach ( var character in name )
        {
            var allowed = char.IsAsciiLetterOrDigit( character )
                || character == ' '
                || character == '_'
                || character == '.'
                || character == '-';

            if ( !allowed )
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Whether an address is one a print could actually be sent to.
    ///
    /// <para>
    /// Checked with the parser the print path uses, rather than a pattern that
    /// looks similar. Saving something that cannot be connected to would move
    /// the failure from the moment it is typed to the moment somebody presses
    /// print, which is the worse of the two.
    /// </para>
    /// </summary>
    public static bool IsValidAddress( string? address )
    {
        if ( string.IsNullOrWhiteSpace( address ) || address.Length > MaxAddressLength )
        {
            return false;
        }

        try
        {
            PrinterAddress.Parse( address ).ToEndPoint();

            return true;
        }
        catch ( Exception )
        {
            // Not an address. Which exception says so is the parser's business.
            return false;
        }
    }

    /// <summary>Every saved printer, by name.</summary>
    public IReadOnlyList<SavedPrinter> List()
    {
        lock ( _gate )
        {
            return _printers
                .OrderBy( printer => printer.Name, StringComparer.OrdinalIgnoreCase )
                .ToArray();
        }
    }

    /// <summary>
    /// Stores a printer, replacing any saved under the same name.
    ///
    /// <para>
    /// Replacing rather than refusing, because a printer that has been given a
    /// new address is the ordinary reason to save one twice, and making that
    /// a delete followed by a save would be ceremony for its own sake.
    /// </para>
    /// </summary>
    public PrinterSaveOutcome Save( string? name, string? address, bool hasCutter )
    {
        name = ( name ?? string.Empty ).Trim();
        address = ( address ?? string.Empty ).Trim();

        if ( !IsValidName( name ) )
        {
            return PrinterSaveOutcome.InvalidName;
        }

        if ( !IsValidAddress( address ) )
        {
            return PrinterSaveOutcome.InvalidAddress;
        }

        lock ( _gate )
        {
            var updated = _printers
                .Where( printer => !printer.Name.Equals( name, StringComparison.OrdinalIgnoreCase ) )
                .ToList();

            if ( updated.Count >= MaxPrinters )
            {
                return PrinterSaveOutcome.TooMany;
            }

            updated.Add( new SavedPrinter
            {
                Name = name,
                Address = address,
                HasCutter = hasCutter
            } );

            Write( updated );

            _printers = updated;

            return PrinterSaveOutcome.Saved;
        }
    }

    /// <summary>Forgets a printer. False when there was none by that name.</summary>
    public bool Delete( string? name )
    {
        name = ( name ?? string.Empty ).Trim();

        if ( name.Length == 0 )
        {
            return false;
        }

        lock ( _gate )
        {
            var updated = _printers
                .Where( printer => !printer.Name.Equals( name, StringComparison.OrdinalIgnoreCase ) )
                .ToList();

            if ( updated.Count == _printers.Count )
            {
                return false;
            }

            Write( updated );

            _printers = updated;

            return true;
        }
    }

    /// <summary>Must be called holding the lock.</summary>
    private void Write( List<SavedPrinter> printers )
    {
        Directory.CreateDirectory( Path.GetDirectoryName( _path )! );

        AtomicFile.Write( _path, JsonSerializer.SerializeToUtf8Bytes( printers, SerializerOptions ) );
    }

    private void Load()
    {
        if ( !File.Exists( _path ) )
        {
            // Nobody has saved a printer yet. Not a fault, and not worth a line
            // in the log every time the proxy starts.
            return;
        }

        try
        {
            var printers = JsonSerializer.Deserialize<List<SavedPrinter>>( File.ReadAllBytes( _path ), SerializerOptions );

            if ( printers != null )
            {
                // Anything in the file that would not be accepted through the
                // page is dropped rather than trusted. The file can be edited
                // by hand, and a name or address that cannot be used is worse
                // sitting in a dropdown than absent from it.
                _printers = printers
                    .Where( printer => IsValidName( printer.Name ) && IsValidAddress( printer.Address ) )
                    .GroupBy( printer => printer.Name, StringComparer.OrdinalIgnoreCase )
                    .Select( group => group.First() )
                    .Take( MaxPrinters )
                    .ToList();

                if ( _printers.Count != printers.Count )
                {
                    _logger.LogWarning( "{dropped} of the {total} printers in {path} were not usable and have been left out of the list.",
                        printers.Count - _printers.Count, printers.Count, _path );
                }

                return;
            }
        }
        catch ( Exception ex )
        {
            _logger.LogWarning( ex, "Could not read {path}, so no printers are named. Printing still works by typing an address.", _path );
        }
    }
}
