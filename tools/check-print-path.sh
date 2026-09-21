#!/usr/bin/env bash
#
# Proves blank label printing has not grown a connection to the print path,
# and has not grown a time limit.
#
# Both are easy to add by accident and neither shows up as a failing test.
#
# Wiring a blank run into PrintMetrics or FailureNotifier "for consistency"
# would mean a desk printer running out of labels on a Tuesday sends the
# check-in team a printer-failed notification. Adding a time limit "so a
# typo'd address doesn't hang" would mean a printer pausing for a new roll is
# reported as a failed run - and an unmeasured number of exactly that kind
# caused a rollback on this codebase once already.
#
# Comments are stripped before searching, on purpose. The comments in these
# files name the print path precisely so a reader knows what is deliberately
# not being touched, and that prose should not be what trips the check. What
# is being tested is whether any code refers to it.
#
set -uo pipefail

repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo"

# The files this feature added.
new_files=(
    Rock.CloudPrint.Service/AtomicFile.cs
    Rock.CloudPrint.Service/BlankLabelRunner.cs
    Rock.CloudPrint.Service/BlankLabelState.cs
    Rock.CloudPrint.Service/LabelCapture.cs
    Rock.CloudPrint.Service/LabelPreview.cs
    Rock.CloudPrint.Service/LabelStore.cs
    Rock.CloudPrint.Service/PrinterBook.cs
    Rock.CloudPrint.Service/PrinterSocket.cs
    Rock.CloudPrint.Service/SecurityCode.cs
    Rock.CloudPrint.Service/ZplTemplate.cs
)

# The print path, and the bookkeeping that belongs to it alone.
forbidden=(
    ProxyClientWebSocket
    ProxyWebSocket
    ProxyWorker
    ProxyStatus
    PrintMetrics
    FailureNotifier
    RequestAborted
    CancelAfter
    Timeout
)

# Files that carry real check-in traffic and must be identical to the release.
untouchable=(
    Rock.CloudPrint.Service/ProxyClientWebSocket.cs
    Rock.CloudPrint.Shared.Common/ProxyWebSocket.cs
    Rock.CloudPrint.Service/ProxyWorker.cs
    Rock.CloudPrint.Service/ProxyStatus.cs
    Rock.CloudPrint.Service/PrintMetrics.cs
    Rock.CloudPrint.Service/FailureNotifier.cs
)

# The baseline is the current release, not a fixed point in history. Any change
# to the files below is a deliberate release decision, so bumping this is part
# of making one - and leaving it alone is what catches the accidental change
# this script exists for.
#
# v1.5.1 moved it: ProxyWorker gained its own ILogger<ProxyClientWebSocket> so
# print activity is attributed separately from worker activity in the log view.
baseline="${BASELINE:-v1.5.1}"
failed=0

echo "Searching the blank label files for anything that would tie them to the print path."
echo

for file in "${new_files[@]}"; do
    if [ ! -f "$file" ]; then
        echo "  MISSING  $file"
        failed=$((failed + 1))
        continue
    fi

    # Drop whole-line comments, then the trailing part of any line that has
    # one, so only code is searched.
    code="$(sed -e 's://.*::' "$file")"
    hits=""

    for term in "${forbidden[@]}"; do
        if grep -q "$term" <<< "$code"; then
            hits="$hits $term"
        fi
    done

    if [ -n "$hits" ]; then
        echo "  FAIL     $(basename "$file") refers to:$hits"
        failed=$((failed + 1))
    else
        echo "  clean    $(basename "$file")"
    fi
done

echo
echo "Checking the print path is byte-identical to $baseline."
echo

for file in "${untouchable[@]}"; do
    if ! git rev-parse "$baseline" >/dev/null 2>&1; then
        echo "  SKIP     no such revision: $baseline"
        break
    fi

    if [ -z "$(git diff "$baseline" -- "$file")" ]; then
        echo "  clean    $(basename "$file")"
    else
        echo "  FAIL     $(basename "$file") differs from $baseline"
        failed=$((failed + 1))
    fi
done

echo
if [ "$failed" -ne 0 ]; then
    echo "PRINT PATH CHECK: FAILED ($failed)"
    exit 1
fi

echo "PRINT PATH CHECK: PASSED"
