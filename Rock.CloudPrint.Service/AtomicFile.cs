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
/// Writes a file in a way that cannot leave a half-written one behind.
///
/// <para>
/// The proxy runs on a Raspberry Pi in a cupboard, and the two files this is
/// used for are both ones a torn write would quietly ruin: a label template
/// that would print as nonsense, and the record of which security codes have
/// already been used. Writing to a temporary file and moving it into place
/// means a power cut leaves either the old file or the new one, never half of
/// each.
/// </para>
/// </summary>
internal static class AtomicFile
{
    /// <summary>
    /// Writes the content, replacing anything already there.
    /// </summary>
    public static void Write( string path, byte[] content )
    {
        var temporary = path + ".tmp";

        File.WriteAllBytes( temporary, content );

        // A move within one directory is a rename, which the filesystem treats
        // as a single operation. This is the whole point of the exercise.
        File.Move( temporary, path, overwrite: true );
    }
}
