using Microsoft.Extensions.Options;

using Rock.CloudPrint.Service;

using Xunit;

namespace Rock.CloudPrint.Tests;

/// <summary>
/// Web UI logins: that a session ends when it should - left idle, too old,
/// logged out, or the PIN changed - that one in use is not cut off early,
/// and that lapsed ones do not accumulate.
/// </summary>
public class AuthServiceTests
{
    /// <summary>
    /// A clock the tests move by hand, so a session can be aged eight hours
    /// without waiting eight hours.
    /// </summary>
    private sealed class FakeClock : TimeProvider
    {
        private DateTimeOffset _now = new( 2026, 9, 26, 9, 0, 0, TimeSpan.Zero );

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance( TimeSpan by ) => _now += by;
    }

    /// <summary>
    /// Options that can be changed mid-test, the way the real ones change when
    /// somebody edits the settings file.
    /// </summary>
    private sealed class Settings : IOptionsMonitor<CloudPrintOptions>
    {
        public CloudPrintOptions CurrentValue { get; } = new() { Password = "1234" };

        public CloudPrintOptions Get( string? name ) => CurrentValue;

        public IDisposable? OnChange( Action<CloudPrintOptions, string?> listener ) => null;
    }

    private readonly FakeClock _clock = new();
    private readonly Settings _options = new();
    private readonly AuthService _auth;

    public AuthServiceTests()
    {
        _auth = new AuthService( _clock );
    }

    [Fact]
    public void ANewTokenIsAccepted()
    {
        var token = _auth.IssueToken( _options );

        Assert.True( _auth.ValidateToken( token, _options ) );
    }

    [Fact]
    public void ATokenThisInstanceNeverIssuedIsRejected()
    {
        _auth.IssueToken( _options );

        Assert.False( _auth.ValidateToken( new string( 'a', 64 ), _options ) );
        Assert.False( _auth.ValidateToken( string.Empty, _options ) );
    }

    [Fact]
    public void ATokenLeftIdleLapses()
    {
        _options.CurrentValue.SessionIdleMinutes = 480;
        var token = _auth.IssueToken( _options );

        _clock.Advance( TimeSpan.FromMinutes( 480 ) );

        Assert.False( _auth.ValidateToken( token, _options ) );
    }

    [Fact]
    public void UsingATokenKeepsItAlivePastTheIdleLimit()
    {
        _options.CurrentValue.SessionIdleMinutes = 480;
        var token = _auth.IssueToken( _options );

        // Nine hours in all, but never more than seven without being used.
        // The limit is on idleness, not on how long somebody has been busy.
        _clock.Advance( TimeSpan.FromHours( 7 ) );
        Assert.True( _auth.ValidateToken( token, _options ) );

        _clock.Advance( TimeSpan.FromHours( 2 ) );
        Assert.True( _auth.ValidateToken( token, _options ) );
    }

    [Fact]
    public void ASessionInConstantUseStillEndsAtTheAbsoluteLimit()
    {
        _options.CurrentValue.SessionIdleMinutes = 480;
        _options.CurrentValue.SessionMaxHours = 24;
        var token = _auth.IssueToken( _options );

        // A dashboard left open polls every couple of seconds, so it is never
        // idle. The absolute limit is the only thing that ends it.
        for ( var hour = 1; hour < 24; hour++ )
        {
            _clock.Advance( TimeSpan.FromHours( 1 ) );
            Assert.True( _auth.ValidateToken( token, _options ) );
        }

        _clock.Advance( TimeSpan.FromHours( 1 ) );
        Assert.False( _auth.ValidateToken( token, _options ) );
    }

    [Fact]
    public void ZeroSwitchesBothLimitsOff()
    {
        _options.CurrentValue.SessionIdleMinutes = 0;
        _options.CurrentValue.SessionMaxHours = 0;
        var token = _auth.IssueToken( _options );

        _clock.Advance( TimeSpan.FromDays( 365 ) );

        Assert.True( _auth.ValidateToken( token, _options ) );
    }

    [Fact]
    public void ShorteningTheLimitAppliesToSessionsAlreadyOpen()
    {
        _options.CurrentValue.SessionIdleMinutes = 480;
        var token = _auth.IssueToken( _options );

        _clock.Advance( TimeSpan.FromMinutes( 30 ) );
        _options.CurrentValue.SessionIdleMinutes = 15;

        Assert.False( _auth.ValidateToken( token, _options ) );
    }

    [Fact]
    public void ALapsedTokenIsForgottenOnceChecked()
    {
        _options.CurrentValue.SessionIdleMinutes = 60;
        var token = _auth.IssueToken( _options );

        _clock.Advance( TimeSpan.FromMinutes( 60 ) );
        Assert.False( _auth.ValidateToken( token, _options ) );

        Assert.Equal( 0, _auth.SessionCount );

        // And lengthening the limit afterwards does not bring it back.
        _options.CurrentValue.SessionIdleMinutes = 600;
        Assert.False( _auth.ValidateToken( token, _options ) );
    }

    [Fact]
    public void IssuingATokenClearsOutLapsedOnes()
    {
        _options.CurrentValue.SessionIdleMinutes = 60;

        // Tokens nobody ever presents again - a closed tab, a browser that
        // was never used twice - are never checked, so checking alone would
        // not get rid of them.
        for ( var i = 0; i < 5; i++ ) _auth.IssueToken( _options );

        _clock.Advance( TimeSpan.FromMinutes( 30 ) );
        var stillLive = _auth.IssueToken( _options );

        _clock.Advance( TimeSpan.FromMinutes( 31 ) );
        var fresh = _auth.IssueToken( _options );

        Assert.Equal( 2, _auth.SessionCount );
        Assert.True( _auth.ValidateToken( stillLive, _options ) );
        Assert.True( _auth.ValidateToken( fresh, _options ) );
    }

    [Fact]
    public void LoggingOutEndsOnlyThatSession()
    {
        var mine  = _auth.IssueToken( _options );
        var yours = _auth.IssueToken( _options );

        _auth.RevokeToken( mine );

        Assert.False( _auth.ValidateToken( mine, _options ) );
        Assert.True( _auth.ValidateToken( yours, _options ) );
    }

    [Fact]
    public void ChangingThePinEndsEverySession()
    {
        var mine  = _auth.IssueToken( _options );
        var yours = _auth.IssueToken( _options );

        _auth.RevokeAll();

        Assert.False( _auth.ValidateToken( mine, _options ) );
        Assert.False( _auth.ValidateToken( yours, _options ) );
        Assert.Equal( 0, _auth.SessionCount );
    }

    [Fact]
    public void ThePinMustMatchExactly()
    {
        Assert.True( _auth.ValidatePassword( "1234", _options ) );

        Assert.False( _auth.ValidatePassword( "1235", _options ) );
        Assert.False( _auth.ValidatePassword( "123", _options ) );
        Assert.False( _auth.ValidatePassword( "12345", _options ) );
        Assert.False( _auth.ValidatePassword( string.Empty, _options ) );
    }

    [Fact]
    public void NothingMatchesWhenNoPinIsSet()
    {
        _options.CurrentValue.Password = string.Empty;

        Assert.False( _auth.ValidatePassword( string.Empty, _options ) );
        Assert.False( _auth.ValidatePassword( "anything", _options ) );
    }
}
