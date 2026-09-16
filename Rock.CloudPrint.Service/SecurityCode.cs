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
using System.Security.Cryptography;

namespace Rock.CloudPrint.Service;

/// <summary>
/// Makes the security codes printed on blank labels.
///
/// <para>
/// A code has one job: to be the same on the child's tag and the parent's
/// receipt, and different from the code on the next copy, so that a
/// hand-written pickup still matches. The proxy invents them itself and Rock
/// neither supplies nor knows them - which is what lets blanks be printed
/// while Rock is unreachable, the situation they exist for.
/// </para>
///
/// <para>
/// A code colliding with one Rock issued for a real check-in is acceptable:
/// the parent is holding the matching half and the hand-written names differ,
/// so a volunteer sees the mismatch. Codes are deduplicated within a run
/// because it is cheap, not because a duplicate would be dangerous.
/// </para>
/// </summary>
internal static class SecurityCode
{
    /// <summary>
    /// The symbols a random code is drawn from: no zero, capital O, one or
    /// capital I. Somebody reads this off a label and says it out loud at a
    /// pickup desk, so the pairs that get misread are simply not used.
    /// </summary>
    public const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    /// <summary>
    /// A security code is printed large enough to read at a glance from a
    /// couple of feet away, so on an ordinary label five or six characters is
    /// already the practical ceiling. The Ark uses three. Eight is past
    /// anything that would fit and is here to stop a typo, not to ration.
    /// </summary>
    public const int MaxLength = 8;

    /// <summary>Three characters, which is what is used in practice.</summary>
    public const int DefaultLength = 3;

    /// <summary>
    /// How many distinct codes exist at a given length.
    /// </summary>
    public static long CodeSpace( int length )
    {
        if ( length < 1 || length > MaxLength )
        {
            return 0;
        }

        var space = 1L;

        for ( var i = 0; i < length; i++ )
        {
            space *= Alphabet.Length;
        }

        return space;
    }

    /// <summary>
    /// Whether a random run of this size can succeed at this length, with room
    /// to spare.
    ///
    /// <para>
    /// This is the only arithmetic limit on a run, and it is not a policy: it
    /// prevents a run that cannot finish. Three characters gives 32,768 codes,
    /// so any realistic quantity is fine; two gives 1,024, where a thousand
    /// distinct codes is impossible. Requiring twice the room rather than just
    /// enough also keeps the generator's work bounded, since every draw is
    /// then at least even money to be one not already taken.
    /// </para>
    /// </summary>
    public static bool HasRoomFor( int length, int quantity )
    {
        return quantity > 0 && CodeSpace( length ) >= 2L * quantity;
    }

    /// <summary>
    /// Whether a string can be used as the starting value of a sequential run.
    /// </summary>
    public static bool IsUsableStart( string? start )
    {
        return !string.IsNullOrEmpty( start )
            && start.Length <= MaxLength
            && start.All( char.IsAsciiDigit );
    }

    /// <summary>
    /// Whether a string can be put in front of every code.
    ///
    /// <para>
    /// The check that matters is the last one. A caret or a tilde inside field
    /// data is read by the printer as the start of a command, so a prefix
    /// containing either would not print as text - it would change the label.
    /// </para>
    /// </summary>
    public static bool IsUsablePrefix( string? prefix )
    {
        if ( string.IsNullOrEmpty( prefix ) )
        {
            return true;
        }

        return prefix.Length <= MaxLength
            && prefix.All( c => char.IsAsciiLetterOrDigit( c ) || c == '-' || c == '_' );
    }

    /// <summary>
    /// Generates distinct random codes.
    /// </summary>
    /// <param name="length">Characters in each code, before any prefix.</param>
    /// <param name="quantity">How many to generate.</param>
    /// <param name="prefix">Put in front of every code.</param>
    public static IReadOnlyList<string> Random( int length, int quantity, string prefix = "" )
    {
        if ( !HasRoomFor( length, quantity ) )
        {
            throw new ArgumentException(
                $"{quantity} distinct codes cannot be drawn from the {CodeSpace( length )} that exist at {length} characters.",
                nameof( quantity ) );
        }

        var codes = new List<string>( quantity );
        var seen = new HashSet<string>( quantity, StringComparer.Ordinal );

        // Because HasRoomFor guarantees the space is at least twice the
        // quantity, every draw has better than even odds of being new, so this
        // is expected to finish in about two draws per code. The ceiling is not
        // a limit on anything - it is there so a mistake in that reasoning ends
        // as an exception rather than as a proxy spinning forever.
        var attempts = 0;
        var ceiling = quantity * 64L + 1024L;

        while ( codes.Count < quantity )
        {
            if ( ++attempts > ceiling )
            {
                throw new InvalidOperationException(
                    $"Gave up drawing {quantity} distinct codes at {length} characters after {attempts} attempts." );
            }

            var code = new string( RandomNumberGenerator.GetItems<char>( Alphabet, length ) );

            if ( seen.Add( code ) )
            {
                codes.Add( prefix + code );
            }
        }

        return codes;
    }

    /// <summary>
    /// Generates consecutive codes, counting up from a starting value.
    /// </summary>
    /// <param name="start">
    /// The first code, as typed. Its length sets the zero padding, so "0998"
    /// gives 0998, 0999, 1000 - the padding is a minimum width, not a limit,
    /// and a run that counts past it simply gets one character wider.
    /// </param>
    /// <param name="quantity">How many to generate.</param>
    /// <param name="prefix">Put in front of every code.</param>
    public static IReadOnlyList<string> Sequential( string start, int quantity, string prefix = "" )
    {
        if ( !IsUsableStart( start ) )
        {
            throw new ArgumentException( "A sequential run starts from a number.", nameof( start ) );
        }

        if ( quantity <= 0 )
        {
            throw new ArgumentOutOfRangeException( nameof( quantity ) );
        }

        var first = long.Parse( start );
        var width = start.Length;
        var codes = new List<string>( quantity );

        for ( var i = 0; i < quantity; i++ )
        {
            codes.Add( prefix + ( first + i ).ToString().PadLeft( width, '0' ) );
        }

        return codes;
    }
}
