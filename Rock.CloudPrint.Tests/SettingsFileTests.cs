using System.Text;
using System.Text.Json.Nodes;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Rock.CloudPrint.Service;

using Xunit;

namespace Rock.CloudPrint.Tests;

/// <summary>
/// The settings the web UI saves.
///
/// <para>
/// Every form saves only its own keys into one shared file, so the failures
/// worth testing are the ones where a save quietly loses somebody else's:
/// two saves at once, and a file already on disk that cannot be read.
/// </para>
/// </summary>
public class SettingsFileTests : IDisposable
{
    private readonly string _root = Path.Combine( Path.GetTempPath(), "settingsfile-" + Guid.NewGuid().ToString( "n" ) );

    private string Path_ => System.IO.Path.Combine( _root, "config", "appsettings.json" );

    private SettingsFile NewFile( IConfigurationRoot? configuration = null ) =>
        new( Path_, configuration, NullLogger<SettingsFile>.Instance );

    private JsonObject ReadBack() => JsonNode.Parse( File.ReadAllText( Path_ ) )!.AsObject();

    public void Dispose()
    {
        if ( Directory.Exists( _root ) )
        {
            Directory.Delete( _root, recursive: true );
        }

        GC.SuppressFinalize( this );
    }

    [Fact]
    public async Task AFirstSaveCreatesTheFileAndItsDirectory()
    {
        Assert.True( await NewFile().UpdateAsync( json => json["Url"] = "https://rock.example.org" ) );

        Assert.Equal( "https://rock.example.org", ReadBack()["Url"]!.GetValue<string>() );
    }

    [Fact]
    public async Task ASaveLeavesKeysItDidNotTouchAlone()
    {
        var file = NewFile();

        await file.UpdateAsync( json => json["Password"] = "1234" );
        await file.UpdateAsync( json => json["Url"] = "https://rock.example.org" );

        var saved = ReadBack();

        Assert.Equal( "1234", saved["Password"]!.GetValue<string>() );
        Assert.Equal( "https://rock.example.org", saved["Url"]!.GetValue<string>() );
    }

    [Fact]
    public async Task SavesMadeAtTheSameTimeAreAllKept()
    {
        var file = NewFile();

        // Each writes a different key, as the three settings forms do. Without
        // the saves being made one at a time, some of these would be lost to a
        // later write that started from an older copy of the file.
        await Task.WhenAll( Enumerable.Range( 0, 50 ).Select( i =>
            Task.Run( () => file.UpdateAsync( json => json[$"Key{i}"] = i ) ) ) );

        var saved = ReadBack();

        for ( var i = 0; i < 50; i++ )
        {
            Assert.Equal( i, saved[$"Key{i}"]!.GetValue<int>() );
        }
    }

    [Fact]
    public async Task AFileThatCannotBeReadIsRefusedAndLeftAsItWas()
    {
        Directory.CreateDirectory( System.IO.Path.GetDirectoryName( Path_ )! );

        const string handWritten = "{ \"Url\": \"https://rock.example.org\", ";
        File.WriteAllText( Path_, handWritten );

        Assert.False( await NewFile().UpdateAsync( json => json["Name"] = "Office" ) );

        Assert.Equal( handWritten, File.ReadAllText( Path_ ) );
    }

    [Fact]
    public async Task AFileThatIsNotAnObjectIsRefused()
    {
        Directory.CreateDirectory( System.IO.Path.GetDirectoryName( Path_ )! );
        File.WriteAllText( Path_, "[ 1, 2, 3 ]" );

        Assert.False( await NewFile().UpdateAsync( json => json["Name"] = "Office" ) );
    }

    [Fact]
    public async Task AnEmptyFileIsTreatedAsNoSettings()
    {
        Directory.CreateDirectory( System.IO.Path.GetDirectoryName( Path_ )! );
        File.WriteAllBytes( Path_, Array.Empty<byte>() );

        Assert.True( await NewFile().UpdateAsync( json => json["Name"] = "Office" ) );

        Assert.Equal( "Office", ReadBack()["Name"]!.GetValue<string>() );
    }

    [Fact]
    public async Task NoTemporaryFileIsLeftBehind()
    {
        await NewFile().UpdateAsync( json => json["Name"] = "Office" );

        Assert.Equal(
            new[] { "appsettings.json" },
            Directory.GetFiles( System.IO.Path.GetDirectoryName( Path_ )! ).Select( f => System.IO.Path.GetFileName( f ) ) );
    }

    [Fact]
    public async Task TheChangeIsVisibleInConfigurationStraightAway()
    {
        Directory.CreateDirectory( System.IO.Path.GetDirectoryName( Path_ )! );
        File.WriteAllText( Path_, "{}", Encoding.UTF8 );

        var configuration = new ConfigurationBuilder()
            .AddJsonFile( Path_, optional: true, reloadOnChange: false )
            .Build();

        await NewFile( configuration ).UpdateAsync( json => json["Name"] = "Office" );

        Assert.Equal( "Office", configuration["Name"] );
    }
}
