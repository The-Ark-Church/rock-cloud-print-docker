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
/// One stored label, as the web UI sees it.
/// </summary>
/// <param name="Name">The name it is stored and printed under.</param>
/// <param name="Bytes">Size of the template on disk.</param>
/// <param name="ModifiedAt">When it was last written.</param>
/// <param name="WidthDots">Printable width from <c>^PW</c>, or zero.</param>
/// <param name="LengthDots">Label length from <c>^LL</c>, or zero.</param>
internal record StoredLabel( string Name, long Bytes, DateTimeOffset ModifiedAt, int WidthDots, int LengthDots );

/// <summary>
/// Why a label could not be stored, or that it was.
/// </summary>
internal enum LabelSaveOutcome
{
    Saved,
    InvalidName,
    TooLarge,
    NotZpl,
    NoCodeToken,
    AlreadyExists
}

/// <summary>
/// Stores the ZPL templates that blank labels are printed from.
///
/// <para>
/// They live in <c>config/labels</c> because <c>config</c> is already a bind
/// mount, already survives the container being recreated, and is already
/// covered by whatever backs it up. There is nothing new for an operator to
/// learn or to remember to back up.
/// </para>
///
/// <para>
/// The three demo templates are seeded on first run only, decided by whether
/// the labels directory exists at all. Checking for each file individually
/// would put back a demo somebody deliberately deleted, every time the proxy
/// restarted. They are published in the repository, so one deleted by mistake
/// can be downloaded and uploaded again.
/// </para>
/// </summary>
internal sealed class LabelStore
{
    /// <summary>
    /// Long enough for a descriptive name, short enough to stay readable in a
    /// list and well inside any filesystem's limit once the extension is added.
    /// </summary>
    public const int MaxNameLength = 64;

    /// <summary>
    /// Templates are a few hundred bytes. A megabyte is far past anything
    /// genuine and is also the largest body the preview service accepts, so a
    /// template bigger than this could not be previewed even if it were stored.
    /// </summary>
    public const int MaxContentBytes = 1024 * 1024;

    private const string Extension = ".zpl";

    private readonly string _directory;
    private readonly ILogger<LabelStore> _logger;

    /// <summary>
    /// Uploads and deletions arrive on web requests and can overlap. The work
    /// is measured in milliseconds and the contention is between two people
    /// clicking at once, so one lock over the whole store is the right size of
    /// answer.
    /// </summary>
    private readonly object _gate = new();

    public LabelStore( IWebHostEnvironment environment, ILogger<LabelStore> logger )
        : this( Path.Combine( environment.ContentRootPath, "config", "labels" ), logger )
    {
    }

    /// <summary>
    /// Initializes a store over a specific directory. Used by tests.
    /// </summary>
    public LabelStore( string directory, ILogger<LabelStore> logger )
    {
        _directory = Path.GetFullPath( directory );
        _logger = logger;
    }

    /// <summary>The directory templates are stored in.</summary>
    public string Directory => _directory;

    /// <summary>
    /// Whether a name may be used for a label.
    ///
    /// <para>
    /// The allowed set is deliberately narrow. A label name becomes a file
    /// name, so the cost of being generous is directory traversal, and the
    /// benefit is being able to put a slash in a label name - which nobody
    /// wants.
    /// </para>
    /// </summary>
    public static bool IsValidName( string? name )
    {
        if ( string.IsNullOrEmpty( name ) || name.Length > MaxNameLength )
        {
            return false;
        }

        // Rejected outright rather than relying on the character set below,
        // which permits a single dot and would therefore permit two.
        if ( name.Contains( "..", StringComparison.Ordinal ) )
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

        // A name that is nothing but dots and spaces resolves to the directory
        // itself, and one with an edge space produces two labels that look
        // identical in a list and are not.
        return name.Trim( ' ', '.' ).Length > 0 && name == name.Trim();
    }

    /// <summary>
    /// Every stored template, ordered by name.
    /// </summary>
    public IReadOnlyList<StoredLabel> List()
    {
        lock ( _gate )
        {
            if ( !System.IO.Directory.Exists( _directory ) )
            {
                return Array.Empty<StoredLabel>();
            }

            var labels = new List<StoredLabel>();

            foreach ( var path in System.IO.Directory.EnumerateFiles( _directory, "*" + Extension ) )
            {
                var name = Path.GetFileNameWithoutExtension( path );

                // A file somebody dropped in by hand under a name the upload
                // path would have refused. Listing it would offer to print
                // something the rest of this class declines to address.
                if ( !IsValidName( name ) )
                {
                    _logger.LogWarning( "Ignoring {path}: {name} is not a usable label name.", path, name );

                    continue;
                }

                try
                {
                    var info = new FileInfo( path );
                    var (width, length) = ZplTemplate.ReadSize( File.ReadAllBytes( path ) );

                    labels.Add( new StoredLabel( name, info.Length, info.LastWriteTimeUtc, width, length ) );
                }
                catch ( Exception ex )
                {
                    // One unreadable file must not hide every other label.
                    _logger.LogWarning( ex, "Could not read the stored label {name}.", name );
                }
            }

            labels.Sort( ( left, right ) => string.Compare( left.Name, right.Name, StringComparison.OrdinalIgnoreCase ) );

            return labels;
        }
    }

    /// <summary>
    /// Reads a template's bytes, or returns <see langword="null"/> if there is
    /// no such label.
    /// </summary>
    public byte[]? Read( string name )
    {
        if ( !IsValidName( name ) )
        {
            return null;
        }

        lock ( _gate )
        {
            var path = ResolvePath( name );

            return File.Exists( path ) ? File.ReadAllBytes( path ) : null;
        }
    }

    /// <summary>
    /// Stores a template under a new name.
    /// </summary>
    /// <param name="name">The name to store it under.</param>
    /// <param name="content">The raw ZPL, exactly as it will be sent.</param>
    public LabelSaveOutcome Save( string name, byte[] content )
    {
        if ( !IsValidName( name ) )
        {
            return LabelSaveOutcome.InvalidName;
        }

        if ( content.Length > MaxContentBytes )
        {
            return LabelSaveOutcome.TooLarge;
        }

        if ( !ZplTemplate.LooksLikeZpl( content ) )
        {
            return LabelSaveOutcome.NotZpl;
        }

        if ( !ZplTemplate.ContainsCodeToken( content ) )
        {
            return LabelSaveOutcome.NoCodeToken;
        }

        lock ( _gate )
        {
            System.IO.Directory.CreateDirectory( _directory );

            var path = ResolvePath( name );

            if ( File.Exists( path ) )
            {
                // Overwriting silently would replace a template somebody is
                // about to print a stack from. Refusing makes it their choice.
                return LabelSaveOutcome.AlreadyExists;
            }

            WriteAtomically( path, content );

            _logger.LogInformation( "Stored the label {name}, {bytes} bytes.", name, content.Length );

            return LabelSaveOutcome.Saved;
        }
    }

    /// <summary>
    /// Deletes a template. Returns whether there was one to delete.
    /// </summary>
    public bool Delete( string name )
    {
        if ( !IsValidName( name ) )
        {
            return false;
        }

        lock ( _gate )
        {
            var path = ResolvePath( name );

            if ( !File.Exists( path ) )
            {
                return false;
            }

            File.Delete( path );

            _logger.LogInformation( "Deleted the label {name}.", name );

            return true;
        }
    }

    /// <summary>
    /// Writes the demo templates, but only on a genuinely first run.
    /// </summary>
    public void SeedIfFirstRun()
    {
        lock ( _gate )
        {
            if ( System.IO.Directory.Exists( _directory ) )
            {
                // The directory existing is the marker. Anything finer grained -
                // seeding each demo that happens to be missing - would put back
                // one that was deliberately deleted, on every restart.
                return;
            }

            try
            {
                System.IO.Directory.CreateDirectory( _directory );

                foreach ( var (name, content) in ReadDemoTemplates() )
                {
                    WriteAtomically( ResolvePath( name ), content );
                }

                _logger.LogInformation( "Created {directory} and seeded the demo labels.", _directory );
            }
            catch ( Exception ex )
            {
                // A read-only or unwritable config mount is a reason to have no
                // demo labels. It is not a reason to stop the proxy printing.
                _logger.LogError( ex, "Could not seed the demo labels into {directory}.", _directory );
            }
        }
    }

    /// <summary>
    /// The demo templates compiled into this assembly, by label name.
    /// </summary>
    private static IEnumerable<(string Name, byte[] Content)> ReadDemoTemplates()
    {
        var assembly = typeof( LabelStore ).Assembly;

        foreach ( var resource in assembly.GetManifestResourceNames()
                                          .Where( n => n.EndsWith( Extension, StringComparison.Ordinal ) )
                                          .OrderBy( n => n, StringComparer.Ordinal ) )
        {
            using var stream = assembly.GetManifestResourceStream( resource );

            if ( stream == null )
            {
                continue;
            }

            using var buffer = new MemoryStream();

            stream.CopyTo( buffer );

            yield return (Path.GetFileNameWithoutExtension( resource ), buffer.ToArray() );
        }
    }

    /// <summary>
    /// Turns a label name into the file it is stored in, and proves the result
    /// is inside the labels directory.
    ///
    /// <para>
    /// The name has already been validated by every caller. This runs anyway:
    /// validation is a rule about what a name may contain, and this is the
    /// thing that makes a mistake in that rule harmless rather than a way to
    /// read or overwrite <c>config/appsettings.json</c>.
    /// </para>
    /// </summary>
    private string ResolvePath( string name )
    {
        var candidate = Path.GetFullPath( Path.Combine( _directory, name + Extension ) );
        var root = _directory.EndsWith( Path.DirectorySeparatorChar )
            ? _directory
            : _directory + Path.DirectorySeparatorChar;

        if ( !candidate.StartsWith( root, StringComparison.Ordinal ) )
        {
            throw new InvalidOperationException( $"The label name '{name}' resolves outside the labels directory." );
        }

        return candidate;
    }

    /// <summary>
    /// Writes to a temporary file and moves it into place, so a proxy losing
    /// power mid-write cannot leave a half-written template that would print
    /// as garbage.
    /// </summary>
    private static void WriteAtomically( string path, byte[] content )
    {
        var temporary = path + ".tmp";

        File.WriteAllBytes( temporary, content );
        File.Move( temporary, path, overwrite: true );
    }
}
