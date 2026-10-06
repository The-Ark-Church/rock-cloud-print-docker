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
using System.Text.Json.Nodes;

namespace Rock.CloudPrint.Service;

/// <summary>
/// The settings the web UI saves, in <c>config/appsettings.json</c>.
///
/// <para>
/// This file is read at startup, and a torn one stops the proxy starting at
/// all: the configuration loader throws on JSON it cannot parse, and the
/// container restarts into the same failure until somebody edits the file by
/// hand. So it is written with <see cref="AtomicFile"/>, the same way labels
/// and the record of used codes are.
/// </para>
///
/// <para>
/// Every change is a read, a modification and a write of the whole file, and
/// the settings, PIN and notification forms each change different keys. Two
/// saves at once would each read the same starting file and the second write
/// would undo the first, so changes are made one at a time.
/// </para>
/// </summary>
internal class SettingsFile
{
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    private readonly string _path;
    private readonly IConfigurationRoot? _configuration;
    private readonly ILogger<SettingsFile> _logger;
    private readonly SemaphoreSlim _gate = new( 1, 1 );

    /// <summary>
    /// Initializes the settings file in the application's config directory.
    /// </summary>
    public SettingsFile( IWebHostEnvironment environment, IConfiguration configuration, ILogger<SettingsFile> logger )
        : this( Path.Combine( environment.ContentRootPath, "config", "appsettings.json" ), configuration as IConfigurationRoot, logger )
    {
    }

    /// <summary>
    /// Initializes a settings file at a specific path. Used by tests, which
    /// pass no configuration to reload.
    /// </summary>
    public SettingsFile( string path, IConfigurationRoot? configuration, ILogger<SettingsFile> logger )
    {
        _path = Path.GetFullPath( path );
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>
    /// Applies <paramref name="change"/> to the stored settings and saves them,
    /// leaving every key it does not touch as it was.
    /// </summary>
    /// <returns>
    /// <c>false</c> when the existing file could not be read. It is left alone
    /// rather than replaced: it may hold settings somebody wrote by hand, and
    /// overwriting it would lose them without a word.
    /// </returns>
    public async Task<bool> UpdateAsync( Action<JsonObject> change, CancellationToken cancellationToken = default )
    {
        await _gate.WaitAsync( cancellationToken );

        try
        {
            Directory.CreateDirectory( Path.GetDirectoryName( _path )! );

            JsonObject json;

            if ( File.Exists( _path ) )
            {
                try
                {
                    await using var stream = File.OpenRead( _path );

                    // An empty file is treated as no settings, which is what the
                    // configuration loader makes of it too.
                    json = stream.Length == 0
                        ? new JsonObject()
                        : ( await JsonNode.ParseAsync( stream, cancellationToken: cancellationToken ) ) as JsonObject
                            ?? throw new JsonException( "The settings are not a JSON object." );
                }
                catch ( JsonException ex )
                {
                    _logger.LogError( ex, "The settings file {Path} could not be read, so it was not changed.", _path );
                    return false;
                }
            }
            else
            {
                json = new JsonObject();
            }

            change( json );

            AtomicFile.Write( _path, JsonSerializer.SerializeToUtf8Bytes( json, WriteOptions ) );

            // The file watcher would pick the change up on its own, but not
            // before the response goes back - and the UI re-reads straight away.
            _configuration?.Reload();

            return true;
        }
        finally
        {
            _gate.Release();
        }
    }
}
