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
using System.Net.Sockets;

namespace Rock.CloudPrint.Service;

/// <summary>What was asked for.</summary>
internal sealed record BlankRunRequest
{
    public string Address { get; init; } = string.Empty;

    /// <summary>The labels making up one copy, in the order they are sent.</summary>
    public IReadOnlyList<string> Labels { get; init; } = Array.Empty<string>();

    /// <summary>
    /// How many copies. Always copies, never labels: ask for 500 with three
    /// labels ticked and 1,500 labels come out.
    /// </summary>
    public int Quantity { get; init; }

    /// <summary>random or sequential.</summary>
    public string Mode { get; init; } = "random";

    public int CodeLength { get; init; } = SecurityCode.DefaultLength;

    /// <summary>Where a sequential run starts. Null continues from the record.</summary>
    public string? Start { get; init; }

    public string Prefix { get; init; } = string.Empty;

    /// <summary>
    /// One copy with a recognisable code, to prove the printer and the stock.
    /// It takes the same route a real run takes, so it proves the route as
    /// well, and it does not move the sequential counter.
    /// </summary>
    public bool IsTestCopy { get; init; }
}

/// <summary>Why a run could not be started, or that it was.</summary>
internal enum BlankRunStartOutcome
{
    Started,
    AlreadyRunning,
    NoLabels,
    UnknownLabel,
    BadAddress,
    BadQuantity,
    BadCodeLength,
    NotEnoughCodes,
    BadPrefix,
    NoSequentialStart,
    CouldNotReserve
}

/// <summary>A run as the web UI sees it.</summary>
internal sealed record BlankRunStatus
{
    public string Id { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public string Printer { get; init; } = string.Empty;
    public string Mode { get; init; } = string.Empty;
    public IReadOnlyList<string> Labels { get; init; } = Array.Empty<string>();
    public int Quantity { get; init; }
    public int CopiesHandedToPrinter { get; init; }
    public int LabelsPerCopy { get; init; }
    public string? FirstCode { get; init; }
    public string? LastCode { get; init; }
    public string? Error { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? FinishedAt { get; init; }
}

/// <summary>
/// Prints stacks of blank labels.
///
/// <para>
/// The shape of a run is taken from the Windows tool this replaces, which has
/// printed real stacks for years: open one socket to the printer, send the
/// resolved bytes for each label of each copy in order, close. It never
/// touches <c>^PQ</c> and never touches a cut command, and neither does this -
/// raising the quantity command instead of repeating the label would print N
/// labels sharing one security code, which is the single outcome this feature
/// must never produce.
/// </para>
///
/// <para>
/// Nothing here has a time limit and nothing here may grow one. A printer that
/// has run out of labels pauses and then carries on when it is reloaded, even
/// if nobody is standing there when it stops - so a write that is taking a
/// while is a printer waiting for paper, not a failure. A number picked for
/// how long that is allowed to take would turn a normal Sunday afternoon into
/// an error. The way a run ends early is a person pressing Cancel.
/// </para>
///
/// <para>
/// It also has nothing to do with the print path. It does not read
/// <c>PrintMetrics</c>, does not set <c>ProxyStatus</c>, and does not tell
/// <c>FailureNotifier</c> anything - because a desk printer running out of
/// labels on a Tuesday is not a check-in failure, and paging the check-in team
/// about it would teach them to ignore the notification that matters.
/// </para>
/// </summary>
internal sealed class BlankLabelRunner
{
    /// <summary>
    /// What a test copy's code is made of, repeated to whatever length the run
    /// would use.
    ///
    /// <para>
    /// W is the widest character in the code alphabet, and it is what Rock's
    /// own legacy check-in labels use as their security code placeholder. A
    /// label designed for check-in has already been laid out around it.
    /// </para>
    ///
    /// <para>
    /// It is a fixed marker, but that is not what keeps a test copy out of a
    /// real batch - a random run could produce the same code. What keeps it out
    /// is that a test copy does not move the sequential counter.
    /// </para>
    /// </summary>
    public const char TestCharacter = 'W';

    /// <summary>
    /// An upper bound on how many labels can make up one copy. Not a rule
    /// about sets - it is there because every template in a copy is held in
    /// memory for the length of the run, and a request is caller-supplied.
    /// Three labels to a copy is typical.
    /// </summary>
    public const int MaxLabelsPerCopy = 100;

    private readonly LabelStore _labels;
    private readonly BlankLabelStateStore _state;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<BlankLabelRunner> _logger;

    private readonly object _gate = new();

    private ActiveRun? _run;

    public BlankLabelRunner( LabelStore labels,
                             BlankLabelStateStore state,
                             IHostApplicationLifetime lifetime,
                             ILogger<BlankLabelRunner> logger )
    {
        _labels = labels;
        _state = state;
        _lifetime = lifetime;
        _logger = logger;
    }

    /// <summary>
    /// The run in progress, or the last one to finish. Null before the first.
    /// </summary>
    public BlankRunStatus? Current
    {
        get
        {
            lock ( _gate )
            {
                return _run?.Snapshot();
            }
        }
    }

    /// <summary>
    /// Validates a request, reserves its codes, and starts sending.
    ///
    /// <para>
    /// Returns as soon as the run has started rather than when it has
    /// finished. A stack of a thousand takes a while, and a run tied to the
    /// web request that started it would be cancelled by a closed tab or a
    /// wandering laptop - losing both the labels and any record of which codes
    /// went out.
    /// </para>
    /// </summary>
    public (BlankRunStartOutcome Outcome, BlankRunStatus? Run, string? Detail) Start( BlankRunRequest request )
    {
        if ( request.Labels.Count == 0 )
        {
            return (BlankRunStartOutcome.NoLabels, null, null);
        }

        if ( request.Labels.Count > MaxLabelsPerCopy )
        {
            return (BlankRunStartOutcome.NoLabels, null, $"A copy can be made of up to {MaxLabelsPerCopy} labels." );
        }

        if ( !SecurityCode.IsUsablePrefix( request.Prefix ) )
        {
            return (BlankRunStartOutcome.BadPrefix, null, null);
        }

        PrinterAddress printer;

        try
        {
            printer = PrinterAddress.Parse( request.Address );
        }
        catch ( Exception ex )
        {
            return (BlankRunStartOutcome.BadAddress, null, ex.Message);
        }

        var quantity = request.IsTestCopy ? 1 : request.Quantity;

        if ( quantity <= 0 )
        {
            return (BlankRunStartOutcome.BadQuantity, null, null);
        }

        // Read every template once, now, so that deleting or replacing one
        // half way through a stack cannot change what the rest of that stack
        // says.
        var templates = new List<(string Name, byte[] Content)>( request.Labels.Count );

        foreach ( var name in request.Labels )
        {
            var content = _labels.Read( name );

            if ( content == null )
            {
                return (BlankRunStartOutcome.UnknownLabel, null, name);
            }

            templates.Add( (name, content) );
        }

        var sequential = string.Equals( request.Mode, "sequential", StringComparison.OrdinalIgnoreCase );
        IReadOnlyList<string> codes;
        string? reservedThrough = null;
        string? nextStart = null;

        if ( request.IsTestCopy )
        {
            // At the length the real run would use, so the test proves the
            // width for the codes that are actually going to be printed.
            var testLength = sequential
                ? ( ( !string.IsNullOrWhiteSpace( request.Start ) ? request.Start.Trim() : _state.Current.SequentialNext )
                    ?? new string( '0', SecurityCode.DefaultLength ) ).Length
                : request.CodeLength;

            codes = new[]
            {
                request.Prefix + new string( TestCharacter, Math.Clamp( testLength, 1, SecurityCode.MaxLength ) )
            };
        }
        else if ( sequential )
        {
            var start = string.IsNullOrWhiteSpace( request.Start )
                ? _state.Current.SequentialNext
                : request.Start.Trim();

            if ( !SecurityCode.IsUsableStart( start ) )
            {
                return (BlankRunStartOutcome.NoSequentialStart, null, null);
            }

            codes = SecurityCode.Sequential( start!, quantity, request.Prefix );

            // Recorded without the prefix, so it stays comparable with a start
            // typed later and with whatever was reserved before.
            var bare = SecurityCode.Sequential( start!, quantity );

            reservedThrough = Highest( _state.Current.SequentialReservedThrough, bare[^1] );
            nextStart = SecurityCode.Sequential( start!, quantity + 1 )[^1];
        }
        else
        {
            if ( request.CodeLength < 1 || request.CodeLength > SecurityCode.MaxLength )
            {
                return (BlankRunStartOutcome.BadCodeLength, null, null);
            }

            if ( !SecurityCode.HasRoomFor( request.CodeLength, quantity ) )
            {
                return (BlankRunStartOutcome.NotEnoughCodes, null,
                    $"There are only {SecurityCode.CodeSpace( request.CodeLength )} codes at {request.CodeLength} characters." );
            }

            codes = SecurityCode.Random( request.CodeLength, quantity, request.Prefix );
        }

        lock ( _gate )
        {
            if ( _run is { IsFinished: false } )
            {
                return (BlankRunStartOutcome.AlreadyRunning, _run.Snapshot(), null);
            }

            if ( sequential && !request.IsTestCopy )
            {
                // Reserved and written to disk before a single byte goes to the
                // printer. A write completing is not proof a label printed, and
                // a write failing is not proof it did not - the copy before it
                // may already be in the printer's buffer. Reserving up front can
                // only ever leave a gap in the numbering, and a gap is harmless
                // where a repeat is not.
                try
                {
                    _state.Update( state => state with
                    {
                        SequentialNext = nextStart,
                        SequentialReservedThrough = reservedThrough
                    } );
                }
                catch ( Exception ex )
                {
                    _logger.LogError( ex, "Could not record the security codes for this run, so it was not started." );

                    return (BlankRunStartOutcome.CouldNotReserve, null, ex.Message);
                }
            }

            var run = new ActiveRun( printer, request, templates, codes, quantity, _lifetime.ApplicationStopping );

            _run = run;

            _logger.LogInformation(
                "Printing {quantity} {mode} copies of {labels} to {printer}, codes {first} to {last}.",
                quantity,
                request.IsTestCopy ? "test" : request.Mode,
                string.Join( ", ", request.Labels ),
                request.Address,
                codes[0],
                codes[^1] );

            // Deliberately not awaited, and deliberately not tied to the
            // request. The token comes from application shutdown, so a restart
            // ends the run and records what it managed, and nothing else can
            // stop it but Cancel.
            _ = Task.Run( () => SendAsync( run ) );

            return (BlankRunStartOutcome.Started, run.Snapshot(), null);
        }
    }

    /// <summary>
    /// Stops the run with this id. Returns whether there was one to stop.
    /// </summary>
    public bool Cancel( string id )
    {
        lock ( _gate )
        {
            if ( _run is not { IsFinished: false } run || run.Id != id )
            {
                return false;
            }

            _logger.LogInformation( "Cancelling the blank label run {id}.", id );

            run.Cancel();

            return true;
        }
    }

    /// <summary>
    /// Opens one socket and writes every label of every copy through it.
    /// </summary>
    private async Task SendAsync( ActiveRun run )
    {
        try
        {
            using var socket = await PrinterSocket.OpenAsync( run.Printer, run.Token );

            // Disposing the socket is what interrupts a write that has stopped
            // making progress. Cancelling the token alone does not reliably
            // interrupt a send that is already in flight, so Cancel would
            // appear to do nothing until the printer came back.
            using var abort = run.Token.Register( () =>
            {
                try
                {
                    socket.Dispose();
                }
                catch ( ObjectDisposedException )
                {
                }
            } );

            using var stream = new NetworkStream( socket, ownsSocket: false );

            for ( var copy = 0; copy < run.Quantity; copy++ )
            {
                run.Token.ThrowIfCancellationRequested();

                foreach ( var template in run.Templates )
                {
                    // Resolved here rather than up front so a stack of a
                    // thousand costs one label's worth of memory rather than a
                    // thousand. The template bytes behind it were read once.
                    await stream.WriteAsync( ZplTemplate.Resolve( template.Content, run.Codes[copy] ), run.Token );
                }

                run.RecordCopyHandedOver();
            }

            Finish( run, "completed", null );
        }
        catch ( OperationCanceledException )
        {
            Finish( run, "cancelled", null );
        }
        catch ( ObjectDisposedException ) when ( run.Token.IsCancellationRequested )
        {
            // The socket was disposed by the cancellation above, mid-write.
            Finish( run, "cancelled", null );
        }
        catch ( Exception ex )
        {
            Finish( run, "failed", ex.Message );
        }
    }

    private void Finish( ActiveRun run, string status, string? error )
    {
        run.Finish( status, error );

        var snapshot = run.Snapshot();

        if ( status == "completed" )
        {
            _logger.LogInformation( "Handed {copies} copies to {printer}.", snapshot.CopiesHandedToPrinter, snapshot.Printer );
        }
        else
        {
            _logger.LogWarning( "Blank label run {status} after {copies} of {quantity} copies were handed to {printer}. {error}",
                status, snapshot.CopiesHandedToPrinter, snapshot.Quantity, snapshot.Printer, error ?? string.Empty );
        }

        if ( run.Request.IsTestCopy )
        {
            // A test copy reserves nothing, so recording it would push a real
            // run out of the history for no gain.
            return;
        }

        try
        {
            _state.Update( state => state with
            {
                History = new[] { ToRecord( snapshot ) }.Concat( state.History ).ToArray()
            } );
        }
        catch ( Exception ex )
        {
            // The labels are already printed. Failing to write the history
            // afterwards is worth a line in the log and nothing more.
            _logger.LogWarning( ex, "Could not record what the blank label run did." );
        }
    }

    private static BlankRunRecord ToRecord( BlankRunStatus status )
    {
        return new BlankRunRecord
        {
            Id = status.Id,
            StartedAt = status.StartedAt,
            FinishedAt = status.FinishedAt,
            Status = status.Status,
            Printer = status.Printer,
            Mode = status.Mode,
            Labels = status.Labels,
            Quantity = status.Quantity,
            CopiesHandedToPrinter = status.CopiesHandedToPrinter,
            FirstCode = status.FirstCode,
            LastCode = status.LastCode,
            Error = status.Error
        };
    }

    /// <summary>
    /// The larger of two zero-padded numbers, compared as numbers so that 999
    /// does not come out above 1000.
    /// </summary>
    private static string Highest( string? left, string right )
    {
        if ( !SecurityCode.IsUsableStart( left ) )
        {
            return right;
        }

        return long.Parse( left! ) >= long.Parse( right ) ? left! : right;
    }

    /// <summary>
    /// A run that has started. Mutable, and only touched under the runner's
    /// lock or by the sending task through the methods below.
    /// </summary>
    private sealed class ActiveRun
    {
        private readonly CancellationTokenSource _cancellation;
        private readonly object _progressGate = new();

        private int _copiesHandedToPrinter;
        private string _status = "running";
        private string? _error;
        private DateTimeOffset? _finishedAt;

        public ActiveRun( PrinterAddress printer,
                          BlankRunRequest request,
                          IReadOnlyList<(string Name, byte[] Content)> templates,
                          IReadOnlyList<string> codes,
                          int quantity,
                          CancellationToken applicationStopping )
        {
            Printer = printer;
            Request = request;
            Templates = templates;
            Codes = codes;
            Quantity = quantity;
            StartedAt = DateTimeOffset.Now;

            // Linked to application shutdown and nothing else. Not to the web
            // request that started the run: a closed tab would then cancel a
            // stack half way through and lose the record of which codes went
            // out. A restart ends the run and writes down what it managed.
            _cancellation = CancellationTokenSource.CreateLinkedTokenSource( applicationStopping );
        }

        public string Id { get; } = Guid.NewGuid().ToString( "n" );

        public PrinterAddress Printer { get; }

        public BlankRunRequest Request { get; }

        public IReadOnlyList<(string Name, byte[] Content)> Templates { get; }

        public IReadOnlyList<string> Codes { get; }

        public int Quantity { get; }

        public DateTimeOffset StartedAt { get; }

        public CancellationToken Token => _cancellation.Token;

        public bool IsFinished
        {
            get
            {
                lock ( _progressGate )
                {
                    return _finishedAt.HasValue;
                }
            }
        }

        public void Cancel()
        {
            try
            {
                _cancellation.Cancel();
            }
            catch ( ObjectDisposedException )
            {
            }
        }

        public void RecordCopyHandedOver()
        {
            lock ( _progressGate )
            {
                _copiesHandedToPrinter++;
            }
        }

        public void Finish( string status, string? error )
        {
            lock ( _progressGate )
            {
                if ( _finishedAt.HasValue )
                {
                    return;
                }

                _status = status;
                _error = error;
                _finishedAt = DateTimeOffset.Now;
            }
        }

        public BlankRunStatus Snapshot()
        {
            lock ( _progressGate )
            {
                return new BlankRunStatus
                {
                    Id = Id,
                    Status = _status,
                    Printer = Request.Address,
                    Mode = Request.IsTestCopy ? "test" : Request.Mode,
                    Labels = Request.Labels,
                    Quantity = Quantity,
                    CopiesHandedToPrinter = _copiesHandedToPrinter,
                    LabelsPerCopy = Templates.Count,
                    FirstCode = Codes.Count > 0 ? Codes[0] : null,
                    LastCode = Codes.Count > 0 ? Codes[^1] : null,
                    Error = _error,
                    StartedAt = StartedAt,
                    FinishedAt = _finishedAt
                };
            }
        }
    }
}
