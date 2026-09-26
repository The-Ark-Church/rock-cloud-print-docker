// The web UI's script. It lives in its own file rather than in index.html so
// the content security policy can refuse inline script altogether, which is
// what stops injected markup from running. For the same reason nothing in the
// page uses onclick="" - see "Event wiring" at the bottom.

// ── Auth state ────────────────────────────────────────────────────
let authToken     = sessionStorage.getItem( 'rock-cp-token' ) || '';
let authRequired  = false;
let appInitialized = false;

// Wraps fetch() with the bearer token. On 401 clears the token and
// shows the login overlay; the thrown error lets callers bail out.
async function authFetch( url, opts = {} ) {
    const headers = { ...(opts.headers || {}) };
    if ( authToken ) headers['Authorization'] = 'Bearer ' + authToken;
    const resp = await fetch( url, { ...opts, headers } );
    if ( resp.status === 401 ) {
        authToken = '';
        sessionStorage.removeItem( 'rock-cp-token' );
        showLoginOverlay();
        throw new Error( 'Unauthorized' );
    }
    return resp;
}

function showLoginOverlay() {
    document.getElementById( 'login-overlay' ).classList.remove( 'hidden' );
    document.getElementById( 'login-error' ).classList.add( 'hidden' );
    document.getElementById( 'login-pin' ).value = '';
    setTimeout( () => document.getElementById( 'login-pin' ).focus(), 50 );
}

function hideLoginOverlay() {
    document.getElementById( 'login-overlay' ).classList.add( 'hidden' );
}

async function doLogin() {
    const pin   = document.getElementById( 'login-pin' ).value;
    const errEl = document.getElementById( 'login-error' );
    errEl.classList.add( 'hidden' );

    try {
        const resp = await fetch( '/api/auth/login', {
            method:  'POST',
            headers: { 'Content-Type': 'application/json' },
            body:    JSON.stringify( { password: pin } )
        } );

        if ( resp.status === 401 ) {
            errEl.textContent = 'Incorrect PIN. Please try again.';
            errEl.classList.remove( 'hidden' );
            document.getElementById( 'login-pin' ).value = '';
            document.getElementById( 'login-pin' ).focus();
            return;
        }

        const data = await resp.json();
        authToken = data.token || '';
        if ( authToken ) sessionStorage.setItem( 'rock-cp-token', authToken );
        hideLoginOverlay();
        document.getElementById( 'logout-btn' ).classList.remove( 'hidden' );

        if ( !appInitialized ) {
            initApp();
        }
        // If already initialized, the running intervals resume automatically.
    } catch ( err ) {
        errEl.textContent = 'Error: ' + escHtml( err.message );
        errEl.classList.remove( 'hidden' );
    }
}

async function doLogout() {
    try {
        const headers = authToken ? { 'Authorization': 'Bearer ' + authToken } : {};
        await fetch( '/api/auth/logout', { method: 'POST', headers } );
    } catch ( _ ) {}
    authToken = '';
    sessionStorage.removeItem( 'rock-cp-token' );
    showLoginOverlay();
}

// ── Theme ─────────────────────────────────────────────────────────
// Follows the browser until somebody presses the toggle, and remembers
// the choice from then on. Kept in this browser rather than on the
// proxy on purpose: which theme to look at belongs to the person
// looking, and two administrators should not fight over it.
//
// The class itself is put on the root by a script in <head>, before
// anything is painted. This only keeps the button in step with it.

const THEME_KEY = 'rock-cp-theme';

function storedTheme() {
    try { return localStorage.getItem( THEME_KEY ); }
    catch ( e ) { return null; }
}

function applyTheme( dark ) {
    document.documentElement.classList.toggle( 'dark', dark );

    document.getElementById( 'theme-icon-sun' ).classList.toggle( 'hidden', !dark );
    document.getElementById( 'theme-icon-moon' ).classList.toggle( 'hidden', dark );
    document.getElementById( 'theme-btn' ).title = dark ? 'Switch to light' : 'Switch to dark';
}

function toggleTheme() {
    const dark = !document.documentElement.classList.contains( 'dark' );

    try { localStorage.setItem( THEME_KEY, dark ? 'dark' : 'light' ); }
    catch ( e ) { /* Not remembered, but still applied for this visit. */ }

    applyTheme( dark );
}

function initTheme() {
    applyTheme( document.documentElement.classList.contains( 'dark' ) );

    // Keep following the browser while nobody has chosen, so a machine
    // set to switch at sunset does. Somebody who has chosen keeps what
    // they chose.
    window.matchMedia( '(prefers-color-scheme: dark)' )
        .addEventListener( 'change', event => {
            if ( !storedTheme() ) applyTheme( event.matches );
        } );
}

// ── Boot ──────────────────────────────────────────────────────────
async function boot() {
    // Before anything else, and before the login overlay, so the PIN
    // screen is the same theme as the page behind it.
    initTheme();

    try {
        const resp = await fetch( '/api/auth/config' );
        const cfg  = await resp.json();
        authRequired = cfg.required;
    } catch ( _ ) {}

    document.getElementById( 'logout-btn' ).classList.toggle( 'hidden', !authRequired );

    if ( authRequired && !authToken ) {
        showLoginOverlay();
        return; // initApp() will be called after successful login
    }

    initApp();
}

function initApp() {
    appInitialized = true;
    showTab( 'dashboard' );
    refreshStatus();
    setInterval( refreshStatus, 2000 );
    setInterval( () => {
        if ( !document.getElementById( 'pane-logs' ).classList.contains( 'hidden' ) ) {
            refreshLogs();
        }
    }, 3000 );
}

// ── Tab management ────────────────────────────────────────────────
function showTab( tab ) {
    ['dashboard', 'logs', 'printers', 'labels', 'settings'].forEach( t => {
        const btn   = document.getElementById( 'tab-' + t );
        const pane  = document.getElementById( 'pane-' + t );
        const active = t === tab;
        btn.classList.toggle( 'border-blue-600',   active );
        btn.classList.toggle( 'text-blue-600',     active );
        btn.classList.toggle( 'border-transparent', !active );
        btn.classList.toggle( 'text-gray-500',     !active );
        pane.classList.toggle( 'hidden', !active );
    } );
    if ( tab === 'settings' ) { loadSettings(); loadNotificationSettings(); loadSecuritySection(); }
    if ( tab === 'logs'     ) { refreshLogs(); }
    if ( tab === 'printers' ) { prefillPrinterAddress(); }
    if ( tab === 'labels'   ) { loadLabels(); loadSavedPrinters(); refreshRun(); }
}

// ── Status polling ────────────────────────────────────────────────
function fmtDate( iso ) {
    if ( !iso ) return '—';
    return new Date( iso ).toLocaleString( undefined, {
        month: 'short', day: 'numeric',
        hour: '2-digit', minute: '2-digit', second: '2-digit'
    } );
}

async function refreshStatus() {
    try {
        const resp = await authFetch( '/api/status' );
        if ( !resp.ok ) return;
        const d = await resp.json();

        applyVersion( d.version );
        updatePrintHealth( d );
        updateNotificationBanner( d.notifications );

        const dot    = document.getElementById( 'status-dot' );
        const text   = document.getElementById( 'status-text' );
        const banner = document.getElementById( 'not-configured-banner' );
        const stats  = document.getElementById( 'stats-grid' );

        if ( d.isConnected ) {
            dot.className    = 'w-3 h-3 rounded-full bg-green-500 flex-shrink-0';
            text.textContent = 'Connected to Rock server';
            text.className   = 'text-base font-medium text-green-700';
            stats.classList.remove( 'hidden' );
            banner.classList.add( 'hidden' );
            document.getElementById( 'started-time'    ).textContent = fmtDate( d.startedDateTime );
            document.getElementById( 'connected-since' ).textContent = fmtDate( d.connectedDateTime );
            document.getElementById( 'labels-count'    ).textContent = (d.totalLabelsPrinted || 0).toLocaleString();
        } else if ( !d.isConfigured ) {
            dot.className    = 'w-3 h-3 rounded-full bg-gray-300 flex-shrink-0';
            text.textContent = 'Not configured';
            text.className   = 'text-base font-medium text-gray-500';
            stats.classList.add( 'hidden' );
            banner.classList.remove( 'hidden' );
        } else {
            dot.className    = 'w-3 h-3 rounded-full bg-yellow-400 flex-shrink-0 pulse';
            text.textContent = 'Connecting to Rock server…';
            text.className   = 'text-base font-medium text-yellow-700';
            stats.classList.add( 'hidden' );
            banner.classList.add( 'hidden' );
        }
    } catch ( _ ) {}
}

// Shows the build version beside the title. It never changes while the
// page is open, so this only does work the first time.
let versionShown = false;
function applyVersion( version ) {
    if ( versionShown || !version ) return;
    const badge = document.getElementById( 'version-badge' );
    badge.textContent = version;
    badge.classList.remove( 'hidden' );
    versionShown = true;
}

// Printed / failed / too-slow counts, plus what went wrong last.
function updatePrintHealth( d ) {
    const panel   = document.getElementById( 'print-health' );
    const printed = d.labelsPrinted || 0;
    const failed  = d.labelsFailed  || 0;
    const slow    = d.slowPrints    || 0;

    if ( !d.isConnected && printed === 0 && failed === 0 ) {
        panel.classList.add( 'hidden' );
        return;
    }
    panel.classList.remove( 'hidden' );

    document.getElementById( 'printed-count' ).textContent = printed.toLocaleString();

    const failedEl = document.getElementById( 'failed-count' );
    failedEl.textContent = failed.toLocaleString();
    failedEl.className   = 'mt-0.5 text-xl font-bold tabular-nums '
        + ( failed > 0 ? 'text-red-600' : 'text-gray-400' );

    const slowEl = document.getElementById( 'slow-count' );
    slowEl.textContent = slow.toLocaleString();
    slowEl.className   = 'mt-0.5 text-xl font-bold tabular-nums '
        + ( slow > 0 ? 'text-yellow-600' : 'text-gray-400' );

    const block = document.getElementById( 'last-failure' );
    const f     = d.lastFailure;

    lastFailedAddress = f ? f.address : lastFailedAddress;

    if ( !f ) {
        block.classList.add( 'hidden' );
        return;
    }
    block.classList.remove( 'hidden' );

    // This is the same string the server was given, so it matches what
    // check-in displayed to the operator.
    document.getElementById( 'last-failure-reason' ).textContent = f.reason || 'Unknown error';
    document.getElementById( 'last-failure-detail' ).textContent =
        f.address + ' · ' + fmtDate( f.at ) + ' · ' + (f.elapsedMilliseconds || 0).toLocaleString() + 'ms';

    const note = document.getElementById( 'last-failure-note' );
    if ( f.exceededRockTimeout ) {
        note.textContent = 'Finished after the Rock server stopped waiting, so check-in showed a timeout instead of this message.';
        note.classList.remove( 'hidden' );
    } else {
        note.classList.add( 'hidden' );
    }
}

// ── Printers ──────────────────────────────────────────────────────
// Remembered from the dashboard so a failed printer can be retested
// without typing its address again.
let lastFailedAddress = '';

function prefillPrinterAddress() {
    const box = document.getElementById( 'printer-test-address' );
    if ( !box.value && lastFailedAddress ) box.value = lastFailedAddress;
}

function showPrinterTestResult( kind, html ) {
    const el = document.getElementById( 'printer-test-result' );
    const styles = {
        ok:      'bg-green-50 border border-green-200 text-green-800',
        bad:     'bg-red-50 border border-red-200 text-red-800',
        waiting: 'bg-gray-50 border border-gray-200 text-gray-600',
    };
    el.className   = 'mt-4 rounded-md p-3 text-sm ' + styles[kind];
    el.innerHTML   = html;
    el.classList.remove( 'hidden' );
}

async function testPrinter() {
    const btn     = document.getElementById( 'printer-test-btn' );
    const address = document.getElementById( 'printer-test-address' ).value.trim();

    if ( !address ) {
        showPrinterTestResult( 'bad', 'Enter a printer address first.' );
        return;
    }

    btn.disabled = true;
    showPrinterTestResult( 'waiting', 'Connecting to ' + escHtml( address ) + '…' );

    try {
        const resp = await authFetch( '/api/printer/test', {
            method:  'POST',
            headers: { 'Content-Type': 'application/json' },
            body:    JSON.stringify( { address } )
        } );

        if ( resp.status === 429 ) {
            showPrinterTestResult( 'bad', 'Too many tests in a short time. Wait a moment and try again.' );
            return;
        }

        const d = await resp.json();

        if ( !resp.ok ) {
            showPrinterTestResult( 'bad', escHtml( d.error || 'The test could not be run.' ) );
            return;
        }

        if ( d.ok ) {
            showPrinterTestResult( 'ok',
                '<span class="font-medium">Reached ' + escHtml( d.address ) + '</span> on port ' + d.port +
                ' in ' + d.elapsedMilliseconds.toLocaleString() + 'ms. Nothing was printed.' );
        } else {
            let html = '<span class="font-medium">Could not reach ' + escHtml( d.address ) + '</span>' +
                       ' on port ' + d.port + '.<br>' + escHtml( d.error );
            if ( d.timedOut ) {
                html += '<br><span class="text-xs">This is the test\'s own 5 second limit. ' +
                        'Printing has no such limit, so a real print might still succeed — just too late ' +
                        'for check-in, which gives up after about five seconds.</span>';
            }
            showPrinterTestResult( 'bad', html );
        }
    } catch ( _ ) {
        showPrinterTestResult( 'bad', 'The test could not be run.' );
    } finally {
        btn.disabled = false;
    }
}

// ── Logs ──────────────────────────────────────────────────────────
const LEVEL_COLORS = {
    Critical:    'text-red-400',
    Error:       'text-red-400',
    Warning:     'text-yellow-400',
    Information: 'text-gray-100',
    Debug:       'text-gray-500',
    Trace:       'text-gray-600',
};

// Position within the service's log buffer. We ask only for what is newer
// than this, so the pane keeps up regardless of how much has been logged.
let logLastSeq     = 0;
let logInstanceId  = null;

// Log types offered as filter toggles. The sink drops anything below
// Information, so those are the only levels that can appear. A level
// that somehow does appear without a toggle is always shown.
const LOG_LEVELS = ['Information', 'Warning', 'Error', 'Critical'];

// What the user has switched off. Empty means "show everything". Sources
// are not known ahead of time the way levels are - they are discovered
// from the entries as they arrive.
const hiddenLevels  = new Set();
const hiddenSources = new Set();

// Styles a filter toggle: lit in onColor when active, dimmed when not.
function paintFilterButton( btn, on, onColor ) {
    btn.className = 'px-2 py-0.5 rounded border text-xs transition-colors ' + ( on
        ? 'bg-gray-700 border-gray-600 ' + onColor
        : 'bg-gray-900 border-gray-700 text-gray-600 hover:text-gray-400' );
    btn.setAttribute( 'aria-pressed', on ? 'true' : 'false' );
}

function buildLevelFilters() {
    const host = document.getElementById( 'log-level-filters' );
    if ( host.childElementCount > 0 ) return;   // already built

    LOG_LEVELS.forEach( level => {
        const btn = document.createElement( 'button' );
        btn.type          = 'button';
        btn.dataset.level = level;
        btn.textContent   = level;
        btn.onclick       = () => toggleLevelFilter( level );
        host.appendChild( btn );
    } );

    paintLevelFilters();
}

function paintLevelFilters() {
    document.querySelectorAll( '#log-level-filters button' ).forEach( btn => {
        paintFilterButton( btn, !hiddenLevels.has( btn.dataset.level ),
            LEVEL_COLORS[btn.dataset.level] || 'text-gray-200' );
    } );
}

function toggleLevelFilter( level ) {
    if ( hiddenLevels.has( level ) ) {
        hiddenLevels.delete( level );
    } else {
        hiddenLevels.add( level );
    }

    paintLevelFilters();
    applyLogFilter();
}

// Adds a toggle for any source seen for the first time. Sources are kept
// in alphabetical order, so the buttons are rebuilt rather than appended.
// Ones already known are kept even after they age out of the buffer, so a
// toggle never disappears from under the user mid-session.
function syncSourceFilters( entries ) {
    const host  = document.getElementById( 'log-source-filters' );
    const known = new Set( [...host.children].map( b => b.dataset.source ) );
    const found = new Set( entries.map( e => e.category || '' ).filter( c => c !== '' ) );

    if ( [...found].every( c => known.has( c ) ) ) return;   // nothing new

    const all = [...new Set( [...known, ...found] )].sort();
    host.innerHTML = '';

    all.forEach( source => {
        const btn = document.createElement( 'button' );
        btn.type           = 'button';
        btn.dataset.source = source;
        btn.textContent    = source;
        btn.onclick        = () => toggleSourceFilter( source );
        host.appendChild( btn );
    } );

    document.getElementById( 'log-source-row' ).classList.toggle( 'hidden', all.length === 0 );
    paintSourceFilters();
}

function paintSourceFilters() {
    document.querySelectorAll( '#log-source-filters button' ).forEach( btn => {
        paintFilterButton( btn, !hiddenSources.has( btn.dataset.source ), 'text-blue-400' );
    } );
}

function toggleSourceFilter( source ) {
    if ( hiddenSources.has( source ) ) {
        hiddenSources.delete( source );
    } else {
        hiddenSources.add( source );
    }

    paintSourceFilters();
    applyLogFilter();
}

// Shows or hides already-rendered rows to match the selected filters. A
// row must pass both Type and From to be shown. Only entry rows carry a
// data-level, so the stream notices are never filtered or counted.
function applyLogFilter() {
    const rows = document.querySelectorAll( '#log-list > div[data-level]' );
    let visible = 0;

    rows.forEach( row => {
        const show = !hiddenLevels.has( row.dataset.level )
                  && !hiddenSources.has( row.dataset.source );
        row.classList.toggle( 'hidden', !show );
        if ( show ) visible++;
    } );

    const empty = document.getElementById( 'log-empty' );
    empty.textContent = rows.length === 0
        ? 'No log entries yet.'
        : 'No log entries match the selected filters.';
    empty.classList.toggle( 'hidden', visible > 0 );
}

// Local date and time, zero-padded so the column stays aligned.
function fmtLogTimestamp( iso ) {
    const d = new Date( iso );
    if ( isNaN( d.getTime() ) ) return '';

    const p = n => String( n ).padStart( 2, '0' );
    return d.getFullYear() + '-' + p( d.getMonth() + 1 ) + '-' + p( d.getDate() ) +
           ' ' + p( d.getHours() ) + ':' + p( d.getMinutes() ) + ':' + p( d.getSeconds() );
}

// A divider for something that happened to the stream itself rather than
// a logged event - a restart, or entries that aged out before we asked.
function appendLogNotice( list, text ) {
    const row = document.createElement( 'div' );
    row.className = 'text-xs leading-5 text-gray-500 italic py-1';
    row.textContent = '— ' + text + ' —';
    list.appendChild( row );
}

async function refreshLogs() {
    buildLevelFilters();

    try {
        const resp = await authFetch( '/api/logs' + ( logLastSeq ? '?after=' + logLastSeq : '' ) );
        if ( !resp.ok ) return;
        const data = await resp.json();

        const list      = document.getElementById( 'log-list' );
        const container = document.getElementById( 'log-container' );

        // The service restarted, so its sequence numbers began again and
        // the position we are holding refers to a run that is gone. Start
        // over; the next poll fetches from the beginning of the new run.
        if ( logInstanceId && data.instanceId !== logInstanceId ) {
            list.innerHTML = '';
            appendLogNotice( list, 'service restarted' );
            logInstanceId = data.instanceId;
            logLastSeq    = 0;
            return;
        }
        logInstanceId = data.instanceId;

        const entries = data.entries || [];

        if ( data.skipped > 0 ) {
            appendLogNotice( list, data.skipped.toLocaleString() +
                ( data.skipped === 1 ? ' entry not shown' : ' entries not shown' ) );
        }

        syncSourceFilters( entries );

        if ( entries.length > 0 ) {
            const atBottom = container.scrollTop + container.clientHeight >= container.scrollHeight - 60;

            entries.forEach( e => {
                const color = LEVEL_COLORS[e.level] || 'text-gray-300';
                const row   = document.createElement( 'div' );
                row.dataset.level  = e.level || '';
                row.dataset.source = e.category || '';
                row.className      = 'flex text-xs leading-5 ' + color;
                row.innerHTML =
                    '<span class="text-gray-500 w-40 flex-shrink-0 tabular-nums">' + fmtLogTimestamp( e.timestamp ) + '</span>' +
                    '<span class="w-24 flex-shrink-0 truncate pr-2">'              + escHtml( e.level )             + '</span>' +
                    '<span class="text-blue-500 w-44 flex-shrink-0 truncate pr-2">' + escHtml( e.category )         + '</span>' +
                    '<span class="flex-1 break-all">'                              + escHtml( e.message )           + '</span>';
                list.appendChild( row );
            } );

            applyLogFilter();

            if ( document.getElementById( 'log-autoscroll' ).checked && atBottom ) {
                container.scrollTop = container.scrollHeight;
            }
        } else {
            applyLogFilter();
        }

        logLastSeq = data.lastSeq;
    } catch ( _ ) {}
}

// Clears what is on screen only. The position is kept, so the pane picks
// up from now rather than replaying everything still in the buffer.
function clearLogs() {
    document.getElementById( 'log-list' ).innerHTML = '';
    applyLogFilter();
}

function escHtml( str ) {
    return (str || '').replace( /&/g, '&amp;' ).replace( /</g, '&lt;' ).replace( />/g, '&gt;' );
}

// ── Notifications ────────────────────────────────

// The banner names the actual fault rather than saying "it failed", because
// telling 401 from 404 from a bare 200 is the whole reason this reports the
// status code. It clears on the next success - there is no dismiss button,
// because a banner you can dismiss while the fault persists is worse than
// no banner at all.
function updateNotificationBanner( n ) {
    const banner = document.getElementById( 'notification-banner' );
    const text   = document.getElementById( 'notification-banner-text' );

    if ( !n || !n.enabled || !n.lastAttempt || n.lastAttempt.delivered ) {
        banner.classList.add( 'hidden' );
        return;
    }

    text.textContent = ' ' + n.lastAttempt.outcome;
    banner.classList.remove( 'hidden' );
}

async function loadNotificationSettings() {
    try {
        const resp = await authFetch( '/api/settings/notifications' );
        const d    = await resp.json();
        document.getElementById( 'notif-enabled'  ).checked = !!d.enabled;
        document.getElementById( 'notif-url'      ).value   = d.url || '';
        document.getElementById( 'notif-cooldown' ).value   = d.cooldownMinutes ?? 5;
        document.getElementById( 'notif-secret'   ).value   = '';
        // The secret is never sent back, only whether one is stored.
        document.getElementById( 'notif-secret-hint' ).textContent = d.secretIsSet
            ? 'A secret is stored. Leave blank to keep it, or type a new one to replace it.'
            : 'Sent as the X-CloudPrint-Token header.';
    } catch ( _ ) {}
}

function showNotifMessage( html ) {
    const msg = document.getElementById( 'notif-message' );
    msg.innerHTML = html;
    msg.classList.remove( 'hidden' );
}

async function saveNotificationSettings() {
    const enabled  = document.getElementById( 'notif-enabled'  ).checked;
    const url      = document.getElementById( 'notif-url'      ).value.trim();
    const secretIn = document.getElementById( 'notif-secret'   ).value;
    const cooldown = parseInt( document.getElementById( 'notif-cooldown' ).value, 10 );

    // null leaves the stored secret alone; the UI is never given it to send back.
    const secret = secretIn === '' ? null : secretIn;

    try {
        const resp = await authFetch( '/api/settings/notifications', {
            method:  'POST',
            headers: { 'Content-Type': 'application/json' },
            body:    JSON.stringify( { enabled, url, secret, cooldownMinutes: isNaN( cooldown ) ? 5 : cooldown } )
        } );
        const d = await resp.json().catch( () => ( {} ) );

        if ( resp.ok ) {
            showNotifMessage( '<div class="text-green-700 bg-green-50 border border-green-200 rounded px-3 py-2 text-sm">Saved. The Rock connection is not affected.</div>' );
            loadNotificationSettings();
        } else {
            showNotifMessage( '<div class="text-red-700 bg-red-50 border border-red-200 rounded px-3 py-2 text-sm">' + escHtml( d.error || ( 'Save failed (HTTP ' + resp.status + ').' ) ) + '</div>' );
        }
    } catch ( err ) {
        if ( err.message !== 'Unauthorized' ) {
            showNotifMessage( '<div class="text-red-700 bg-red-50 border border-red-200 rounded px-3 py-2 text-sm">Error: ' + escHtml( err.message ) + '</div>' );
        }
    }
}

async function sendTestNotification() {
    showNotifMessage( '<div class="text-gray-600 bg-gray-50 border border-gray-200 rounded px-3 py-2 text-sm">Sending\u2026</div>' );

    try {
        const resp = await authFetch( '/api/notifications/test', {
            method:  'POST',
            headers: { 'Content-Type': 'application/json' },
            body:    '{}'
        } );

        if ( resp.status === 429 ) {
            showNotifMessage( '<div class="text-amber-800 bg-amber-50 border border-amber-200 rounded px-3 py-2 text-sm">Too many tests in a short time. Wait a minute and try again.</div>' );
            return;
        }

        const d = await resp.json();

        // The exact status and body, not a summary - this is the whole
        // point of the button: prove the chain now, not during an outage.
        const detail = d.detail ? '<div class="mt-1 text-xs opacity-80">' + escHtml( d.detail ) + '</div>' : '';
        const status = d.statusCode ? ' (HTTP ' + d.statusCode + ')' : '';

        if ( d.delivered ) {
            showNotifMessage( '<div class="text-green-700 bg-green-50 border border-green-200 rounded px-3 py-2 text-sm">' + escHtml( d.outcome ) + status + detail + '</div>' );
        } else {
            showNotifMessage( '<div class="text-red-700 bg-red-50 border border-red-200 rounded px-3 py-2 text-sm">' + escHtml( d.outcome ) + status + detail + '</div>' );
        }
    } catch ( err ) {
        if ( err.message !== 'Unauthorized' ) {
            showNotifMessage( '<div class="text-red-700 bg-red-50 border border-red-200 rounded px-3 py-2 text-sm">Error: ' + escHtml( err.message ) + '</div>' );
        }
    }
}

// ── Settings — connection ─────────────────────────────────────────
async function loadSettings() {
    try {
        const resp = await authFetch( '/api/settings' );
        const d    = await resp.json();
        document.getElementById( 'setting-url'  ).value = d.url  || '';
        document.getElementById( 'setting-id'   ).value = d.id   || '';
        document.getElementById( 'setting-name' ).value = d.name || '';
    } catch ( _ ) {}
}

async function saveSettings() {
    const url  = document.getElementById( 'setting-url'  ).value.trim();
    const id   = document.getElementById( 'setting-id'   ).value.trim();
    const name = document.getElementById( 'setting-name' ).value.trim();
    const msg  = document.getElementById( 'save-message' );
    try {
        const resp = await authFetch( '/api/settings', {
            method:  'POST',
            headers: { 'Content-Type': 'application/json' },
            body:    JSON.stringify( { url, name, id } )
        } );
        if ( resp.ok ) {
            msg.innerHTML = '<div class="text-green-700 bg-green-50 border border-green-200 rounded px-3 py-2 text-sm">Settings saved. Reconnecting…</div>';
            msg.classList.remove( 'hidden' );
            setTimeout( () => { msg.classList.add( 'hidden' ); showTab( 'dashboard' ); }, 2000 );
        } else {
            msg.innerHTML = '<div class="text-red-700 bg-red-50 border border-red-200 rounded px-3 py-2 text-sm">Save failed (HTTP ' + resp.status + ').</div>';
            msg.classList.remove( 'hidden' );
        }
    } catch ( err ) {
        if ( err.message !== 'Unauthorized' ) {
            msg.innerHTML = '<div class="text-red-700 bg-red-50 border border-red-200 rounded px-3 py-2 text-sm">Error: ' + escHtml( err.message ) + '</div>';
            msg.classList.remove( 'hidden' );
        }
    }
}

// ── Settings — security / PIN ─────────────────────────────────────
async function loadSecuritySection() {
    try {
        const resp = await fetch( '/api/auth/config' );
        const cfg  = await resp.json();

        document.getElementById( 'pin-envvar-notice' ).classList.toggle( 'hidden', !cfg.fromEnvVar );
        document.getElementById( 'pin-not-set'       ).classList.toggle( 'hidden', cfg.required || cfg.fromEnvVar );
        document.getElementById( 'pin-is-set'        ).classList.toggle( 'hidden', !cfg.required || cfg.fromEnvVar );

        // Clear stale input values and messages.
        ['new-pin','confirm-pin','current-pin','change-pin-new','change-pin-confirm'].forEach( id => {
            const el = document.getElementById( id );
            if ( el ) el.value = '';
        } );
        ['pin-msg-set','pin-msg-change'].forEach( id =>
            document.getElementById( id ).classList.add( 'hidden' ) );
    } catch ( _ ) {}
}

function showPinMsg( elId, type, text ) {
    const el  = document.getElementById( elId );
    const cls = type === 'error'
        ? 'text-red-700 bg-red-50 border border-red-200'
        : 'text-green-700 bg-green-50 border border-green-200';
    el.innerHTML = '<div class="' + cls + ' rounded px-3 py-2 text-sm">' + escHtml( text ) + '</div>';
    el.classList.remove( 'hidden' );
}

async function savePin() {
    const newPin = document.getElementById( 'new-pin'     ).value;
    const confirm = document.getElementById( 'confirm-pin' ).value;
    if ( !newPin )            { showPinMsg( 'pin-msg-set', 'error', 'Please enter a PIN.' );   return; }
    if ( newPin !== confirm ) { showPinMsg( 'pin-msg-set', 'error', 'PINs do not match.' );    return; }

    try {
        const resp = await authFetch( '/api/settings/security', {
            method:  'POST',
            headers: { 'Content-Type': 'application/json' },
            body:    JSON.stringify( { currentPassword: null, newPassword: newPin } )
        } );
        if ( resp.ok ) {
            // PIN just set — force login with the new PIN.
            authToken    = '';
            authRequired = true;
            sessionStorage.removeItem( 'rock-cp-token' );
            document.getElementById( 'logout-btn' ).classList.remove( 'hidden' );
            showLoginOverlay();
        } else {
            const d = await resp.json().catch( () => ({}) );
            showPinMsg( 'pin-msg-set', 'error', d.error || 'Failed to set PIN.' );
        }
    } catch ( err ) {
        if ( err.message !== 'Unauthorized' )
            showPinMsg( 'pin-msg-set', 'error', 'Error: ' + escHtml( err.message ) );
    }
}

async function changePin() {
    const current = document.getElementById( 'current-pin'      ).value;
    const newPin  = document.getElementById( 'change-pin-new'   ).value;
    const confirm = document.getElementById( 'change-pin-confirm' ).value;
    if ( newPin && newPin !== confirm ) {
        showPinMsg( 'pin-msg-change', 'error', 'New PINs do not match.' );
        return;
    }

    try {
        const resp = await authFetch( '/api/settings/security', {
            method:  'POST',
            headers: { 'Content-Type': 'application/json' },
            body:    JSON.stringify( { currentPassword: current, newPassword: newPin } )
        } );
        if ( resp.ok ) {
            if ( !newPin ) {
                // PIN removed — no longer required.
                authToken    = '';
                authRequired = false;
                sessionStorage.removeItem( 'rock-cp-token' );
                document.getElementById( 'logout-btn' ).classList.add( 'hidden' );
                showPinMsg( 'pin-msg-change', 'success', 'PIN removed. Protection is now disabled.' );
                loadSecuritySection();
            } else {
                // PIN changed — all sessions invalidated, must re-login.
                authToken = '';
                sessionStorage.removeItem( 'rock-cp-token' );
                showLoginOverlay();
            }
        } else {
            const d = await resp.json().catch( () => ({}) );
            showPinMsg( 'pin-msg-change', 'error', d.error || 'Failed to update PIN.' );
        }
    } catch ( err ) {
        if ( err.message !== 'Unauthorized' )
            showPinMsg( 'pin-msg-change', 'error', 'Error: ' + escHtml( err.message ) );
    }
}

// ── Restart ───────────────────────────────────────────────────────
// ── Blank labels ──────────────────────────────────────────────────
//
// The whole tab works with Rock unreachable, so nothing here reads the
// connection status or waits for it.

const BLANK_PREFS = 'rock-cp-blank-labels';

let blankLabels     = [];            // what is stored, from the API
let blankOrder      = [];            // names, in the order they print
let blankPicked     = new Set();     // which of them make up a copy
let blankSequential = {};            // where the numbering has reached
let blankRun        = null;
let blankPollTimer  = null;
let blankRestored   = false;

// The same group gets printed every few months, so re-ticking it and
// re-typing the printer every time is pointless friction.
function loadBlankPrefs() {
    try { return JSON.parse( localStorage.getItem( BLANK_PREFS ) ) || {}; }
    catch ( e ) { return {}; }
}

function saveBlankPrefs() {
    try {
        localStorage.setItem( BLANK_PREFS, JSON.stringify( {
            order:   blankOrder,
            picked:  [...blankPicked],
            address: document.getElementById( 'blank-address' ).value.trim(),
            cutter:  document.getElementById( 'blank-cutter' ).checked
        } ) );
    } catch ( e ) { /* private browsing, or a full quota. Not worth a message. */ }
}

// ── Saved printers ────────────────────────────────────────────────
// The list lives on the proxy, not in this browser. An address kept in
// local storage is remembered for one person on one machine, which
// left the next administrator who had to print blanks looking up an IP.
//
// There is one box. It takes a saved printer's name or a raw address,
// and the name is resolved to an address at the moment of printing. So
// whoever sets a printer up types its address once, and everybody
// after them types the name and never sees one.

let savedPrinters    = [];
let printerListIndex = -1;      // the row the arrow keys are on
let printerEditing   = null;    // the printer the dialog was opened on

async function loadSavedPrinters() {
    let resp;
    try { resp = await authFetch( '/api/printers' ); }
    catch ( e ) { return; }

    if ( !resp.ok ) return;

    savedPrinters = await resp.json();

    if ( isPrinterListOpen() ) renderPrinterList();
}

// A name that matches a saved printer wins, because that is what was
// typed. Anything else is an address and is used as one.
function resolvePrinter( text ) {
    const value = ( text || '' ).trim().toLowerCase();

    return savedPrinters.find( p => p.name.toLowerCase() === value ) || null;
}

// What a run is actually sent. Named here rather than read straight out
// of the box, because the box may be holding a name.
function printerAddressForRun() {
    const value   = document.getElementById( 'blank-address' ).value.trim();
    const printer = resolvePrinter( value );

    return printer ? printer.address : value;
}

function isPrinterListOpen() {
    return !document.getElementById( 'printer-list' ).classList.contains( 'hidden' );
}

function openPrinterList() {
    printerListIndex = -1;
    renderPrinterList();
}

function closePrinterList() {
    document.getElementById( 'printer-list' ).classList.add( 'hidden' );
    printerListIndex = -1;
}

function matchingPrinters() {
    const value = document.getElementById( 'blank-address' ).value.trim().toLowerCase();

    // An empty box, or one already holding a printer's name, shows the
    // whole list - so a different printer can be chosen without having
    // to clear the box first.
    if ( !value || resolvePrinter( value ) ) return savedPrinters;

    return savedPrinters.filter( p =>
        p.name.toLowerCase().includes( value ) || p.address.toLowerCase().includes( value ) );
}

function renderPrinterList() {
    const list = document.getElementById( 'printer-list' );
    const rows = matchingPrinters();

    list.replaceChildren();

    // Nothing to offer is not a dropdown. Somebody who has never saved
    // a printer sees a plain box and nothing else.
    if ( !rows.length ) { list.classList.add( 'hidden' ); return; }

    rows.forEach( ( printer, i ) => {
        const row = document.createElement( 'div' );

        row.className = 'flex items-center gap-2 px-2 py-1.5 text-sm cursor-pointer ' +
            ( i === printerListIndex ? 'bg-blue-50' : 'hover:bg-gray-50' );

        // mousedown rather than click: the box loses focus first, and
        // the blur that closes the list would land before a click did.
        row.addEventListener( 'mousedown', event => {
            event.preventDefault();
            choosePrinter( printer.name );
        } );

        const name = document.createElement( 'span' );
        name.className = 'flex-1 min-w-0 truncate text-gray-900';
        name.textContent = printer.name;

        const where = document.createElement( 'span' );
        where.className = 'text-xs text-gray-400 whitespace-nowrap';
        where.textContent = printer.address + ( printer.hasCutter ? ' · cutter' : '' );

        const forget = document.createElement( 'button' );
        forget.className = 'shrink-0 px-1 text-sm leading-none text-gray-300 hover:text-red-600';
        forget.title = 'Forget this printer';
        forget.innerHTML = '&times;';
        forget.addEventListener( 'mousedown', event => {
            event.preventDefault();
            event.stopPropagation();
            forgetPrinter( printer.name );
        } );

        row.append( name, where, forget );
        list.appendChild( row );
    } );

    list.classList.remove( 'hidden' );
}

function choosePrinter( name ) {
    const printer = savedPrinters.find( p => p.name === name );

    if ( !printer ) return;

    document.getElementById( 'blank-address' ).value = printer.name;

    // The cutter is a fact about the machine, so choosing one sets it.
    // Still a checkbox, so it can be overridden for a single run.
    document.getElementById( 'blank-cutter' ).checked = !!printer.hasCutter;

    closePrinterList();
    saveBlankPrefs();
}

function printerTyped() {
    printerListIndex = -1;

    renderPrinterList();
    saveBlankPrefs();
}

function printerListKey( event ) {
    if ( event.key === 'Escape' ) { closePrinterList(); return; }

    const rows = matchingPrinters();

    if ( event.key === 'ArrowDown' || event.key === 'ArrowUp' ) {
        if ( !rows.length ) return;

        event.preventDefault();

        if ( !isPrinterListOpen() ) { openPrinterList(); return; }

        printerListIndex = ( printerListIndex + ( event.key === 'ArrowDown' ? 1 : rows.length ) ) % rows.length;

        renderPrinterList();

        return;
    }

    if ( event.key === 'Enter' && isPrinterListOpen() && printerListIndex >= 0 ) {
        event.preventDefault();

        choosePrinter( rows[printerListIndex].name );
    }
}

async function forgetPrinter( name ) {
    const printer = savedPrinters.find( p => p.name === name );

    if ( !printer ) return;

    if ( !confirm( 'Stop offering "' + name + '" in the list?\n\n' +
                   'The printer itself is untouched, and nothing already printed is affected.' ) ) return;

    const box     = document.getElementById( 'blank-address' );
    const showing = box.value.trim().toLowerCase() === name.toLowerCase();

    if ( !await printerBookRequest( '/api/printers/' + encodeURIComponent( name ), { method: 'DELETE' },
            'Removed <strong>' + escHtml( name ) + '</strong>.' ) ) return;

    // The box was holding a name that now means nothing to anyone. Put
    // the address in its place rather than emptying it.
    if ( showing ) {
        box.value = printer.address;
        saveBlankPrefs();
    }

    renderPrinterList();
}

async function printerBookRequest( url, options, okMessage ) {
    let resp;

    try { resp = await authFetch( url, options ); }
    catch ( e ) { showBlankMessage( 'bad', 'The list of printers could not be changed.' ); return false; }

    const data = await resp.json().catch( () => ( {} ) );

    if ( !resp.ok ) {
        showBlankMessage( 'bad', escHtml( data.error || 'The list of printers could not be changed.' ) );

        return false;
    }

    await loadSavedPrinters();

    showBlankMessage( 'ok', okMessage );

    return true;
}

// The + button. Opens on whatever the box is holding: an address to be
// named, or a saved printer to be corrected.
function openPrinterDialog() {
    const value = document.getElementById( 'blank-address' ).value.trim();

    printerEditing = resolvePrinter( value );

    document.getElementById( 'printer-modal-title' ).textContent =
        printerEditing ? 'Edit this printer' : 'Save this printer';
    document.getElementById( 'printer-modal-name' ).value = printerEditing ? printerEditing.name : '';
    document.getElementById( 'printer-modal-address' ).value = printerEditing ? printerEditing.address : value;
    document.getElementById( 'printer-modal-cutter' ).checked = printerEditing
        ? !!printerEditing.hasCutter
        : document.getElementById( 'blank-cutter' ).checked;

    hideBoxMessage( 'printer-modal-message' );
    closePrinterList();

    document.getElementById( 'printer-modal' ).classList.remove( 'hidden' );
    document.getElementById( printerEditing ? 'printer-modal-address' : 'printer-modal-name' ).focus();
}

function closePrinterDialog() {
    document.getElementById( 'printer-modal' ).classList.add( 'hidden' );

    hideBoxMessage( 'printer-modal-message' );

    printerEditing = null;
}

function printerDialogKey( event ) {
    if ( event.key === 'Enter' )  { event.preventDefault(); savePrinterDialog(); }
    if ( event.key === 'Escape' ) { closePrinterDialog(); }
}

async function savePrinterDialog() {
    const name      = document.getElementById( 'printer-modal-name' ).value.trim();
    const address   = document.getElementById( 'printer-modal-address' ).value.trim();
    const hasCutter = document.getElementById( 'printer-modal-cutter' ).checked;

    if ( !name )    { showBoxMessage( 'printer-modal-message', 'bad', 'Give the printer a name.', '' ); return; }
    if ( !address ) { showBoxMessage( 'printer-modal-message', 'bad', 'Enter the printer address.', '' ); return; }

    const renamedFrom = printerEditing && printerEditing.name !== name ? printerEditing.name : null;

    let resp;

    try {
        resp = await authFetch( '/api/printers', {
            method:  'POST',
            headers: { 'Content-Type': 'application/json' },
            body:    JSON.stringify( { name: name, address: address, hasCutter: hasCutter } )
        } );
    } catch ( e ) {
        showBoxMessage( 'printer-modal-message', 'bad', 'That could not be saved.', '' );

        return;
    }

    const data = await resp.json().catch( () => ( {} ) );

    if ( !resp.ok ) {
        showBoxMessage( 'printer-modal-message', 'bad', escHtml( data.error || 'That could not be saved.' ), '' );

        return;
    }

    // A rename is a save under the new name and then a tidy-up, so the
    // old name does not linger in the list as a second printer.
    if ( renamedFrom ) {
        try { await authFetch( '/api/printers/' + encodeURIComponent( renamedFrom ), { method: 'DELETE' } ); }
        catch ( e ) { /* The save worked. A leftover name is not worth failing over. */ }
    }

    await loadSavedPrinters();

    // Showing the name is the entire point of having saved one.
    document.getElementById( 'blank-address' ).value = name;
    document.getElementById( 'blank-cutter' ).checked = hasCutter;

    saveBlankPrefs();
    closePrinterDialog();

    showBlankMessage( 'ok', 'Saved <strong>' + escHtml( name ) + '</strong>.' );
}

function showLabelMessage( kind, html ) {
    showBoxMessage( 'label-message', kind, html, 'mt-3' );
}

function showBlankMessage( kind, html ) {
    showBoxMessage( 'blank-message', kind, html, '' );
}

// How long a success notice stays up before clearing itself.
const MESSAGE_LINGER_MS = 10000;

const messageTimers = {};

function showBoxMessage( id, kind, html, extra ) {
    const el = document.getElementById( id );
    const styles = {
        ok:      'bg-green-50 border border-green-200 text-green-800',
        bad:     'bg-red-50 border border-red-200 text-red-800',
        waiting: 'bg-gray-50 border border-gray-200 text-gray-600',
    };
    el.className = ( extra + ' rounded-md p-3 text-sm ' + styles[kind] ).trim();

    // Every notice can be dismissed. Being stuck with one you have read
    // and dealt with is a small thing that is irritating every time.
    el.innerHTML =
        '<div class="flex items-start justify-between gap-3">' +
            '<div class="min-w-0">' + html + '</div>' +
            '<button data-action="hideBoxMessage" data-arg="' + id + '" title="Dismiss" ' +
                'class="shrink-0 leading-none opacity-50 hover:opacity-100">&times;</button>' +
        '</div>';
    el.classList.remove( 'hidden' );

    if ( messageTimers[id] ) clearTimeout( messageTimers[id] );
    delete messageTimers[id];

    // A success is an acknowledgement rather than something to act on,
    // so it goes away on its own. A failure stays until it is dismissed
    // or replaced, because it is the only record of what went wrong.
    if ( kind === 'ok' ) {
        messageTimers[id] = setTimeout( () => hideBoxMessage( id ), MESSAGE_LINGER_MS );
    }
}

function hideBoxMessage( id ) {
    document.getElementById( id ).classList.add( 'hidden' );

    if ( messageTimers[id] ) clearTimeout( messageTimers[id] );
    delete messageTimers[id];
}

function hideBlankMessage() {
    hideBoxMessage( 'blank-message' );
}

async function loadLabels() {
    let resp;
    try { resp = await authFetch( '/api/labels' ); }
    catch ( e ) { return; }

    if ( !resp.ok ) { showLabelMessage( 'bad', 'Could not read the stored labels.' ); return; }

    blankLabels = await resp.json();

    if ( !blankRestored ) {
        const prefs = loadBlankPrefs();
        blankOrder  = Array.isArray( prefs.order ) ? prefs.order.slice() : [];
        blankPicked = new Set( Array.isArray( prefs.picked ) ? prefs.picked : [] );
        const address = document.getElementById( 'blank-address' );
        if ( prefs.address && !address.value ) address.value = prefs.address;
        document.getElementById( 'blank-cutter' ).checked = !!prefs.cutter;
        blankRestored = true;
    }

    // Keep the remembered order for labels that still exist, put anything
    // new at the end, and forget anything that has been deleted.
    const names = blankLabels.map( l => l.name );
    blankOrder  = blankOrder.filter( n => names.includes( n ) );
    names.forEach( n => { if ( !blankOrder.includes( n ) ) blankOrder.push( n ); } );
    [...blankPicked].forEach( n => { if ( !names.includes( n ) ) blankPicked.delete( n ); } );

    renderLabelList();
    updateBlankTotals();
}

// Size is baked into the template and cannot be changed from here, so
// this is a note about which stock to load rather than a setting.
function describeSize( label ) {
    if ( !label.widthDots || !label.lengthDots ) {
        return { text: label.bytes + ' B',
                 tip:  'This template does not say what size it is.' };
    }
    const w = ( label.widthDots  / 203 ).toFixed( 1 );
    const h = ( label.lengthDots / 203 ).toFixed( 1 );
    return {
        text: w + ' \u00d7 ' + h + ' in',
        tip:  'The size is fixed in the template (^PW' + label.widthDots +
              ' ^LL' + label.lengthDots + '), at 203 dpi. Load the stock that matches. ' +
              label.bytes + ' bytes.'
    };
}

function renderLabelList() {
    const host = document.getElementById( 'label-list' );

    if ( !blankOrder.length ) {
        host.innerHTML = '<p class="py-3 text-xs text-gray-500">' +
            'Nothing stored. Upload a ZPL file with <code class="bg-gray-100 px-1 rounded">WWW</code> ' +
            'where the security code should go.</p>';
        return;
    }

    const byName = {};
    blankLabels.forEach( l => { byName[l.name] = l; } );

    const icon = 'px-1 text-gray-300 hover:text-gray-600 disabled:opacity-40 ' +
                 'disabled:hover:text-gray-300 text-xs leading-none';

    host.innerHTML = blankOrder.map( ( name, i ) => {
        const size = describeSize( byName[name] || { bytes: 0 } );
        return '<div class="flex items-center gap-2 py-1 border-b border-gray-100 last:border-0">' +
            '<input type="checkbox" ' + ( blankPicked.has( name ) ? 'checked ' : '' ) +
                'data-change="toggleLabel" data-index="' + i + '" ' +
                'class="h-3.5 w-3.5 rounded border-gray-300 text-blue-600 focus:ring-blue-500">' +
            '<span class="flex-1 min-w-0 truncate text-sm text-gray-900">' + escHtml( name ) + '</span>' +
            '<span class="text-xs text-gray-400 tabular-nums whitespace-nowrap" title="' +
                escHtml( size.tip ) + '">' + escHtml( size.text ) + '</span>' +
            '<button data-action="moveLabel" data-index="' + i + '" data-delta="-1" class="' + icon + '" ' +
                ( i === 0 ? 'disabled ' : '' ) + 'title="Print earlier">&#9650;</button>' +
            '<button data-action="moveLabel" data-index="' + i + '" data-delta="1" class="' + icon + '" ' +
                ( i === blankOrder.length - 1 ? 'disabled ' : '' ) + 'title="Print later">&#9660;</button>' +
            '<button data-action="deleteLabel" data-index="' + i + '" title="Delete" ' +
                'class="px-1 text-sm leading-none text-gray-300 hover:text-red-600">&times;</button>' +
        '</div>';
    } ).join( '' );
}

// ── Capturing a label from Rock ───────────────────────────────────

// Matches LabelCapture.DefaultPort. The raw printing port, so a Rock
// device address with no port on it reaches capture unchanged.
const LabelCaptureDefaultPort = 9100;

let capturePollTimer = null;

function toggleCapture() {
    const panel = document.getElementById( 'capture-panel' );
    panel.classList.toggle( 'hidden' );

    if ( !panel.classList.contains( 'hidden' ) ) {
        updateCaptureAddress();
        refreshCapture();
    } else {
        stopCapturePolling();
    }
}

// The address is from the point of view of whichever proxy opens the
// connection, which is not necessarily this one. The ordinary case is a
// production proxy elsewhere sending here as if this were a printer, so
// the address it needs is this machine's - the one the browser reached
// it on is the best available guess at that.
function updateCaptureAddress() {
    const port = document.getElementById( 'capture-port' ).value || '9100';
    document.getElementById( 'capture-address' ).textContent = location.hostname + ':' + port;
}

function showCaptureMessage( kind, html ) {
    showBoxMessage( 'capture-message', kind, html, '' );
}

function hideCaptureMessage() {
    hideBoxMessage( 'capture-message' );
}

async function armCapture() {
    hideCaptureMessage();

    const port = parseInt( document.getElementById( 'capture-port' ).value, 10 ) || LabelCaptureDefaultPort;

    try {
        const resp = await authFetch( '/api/labels/capture/arm', {
            method:  'POST',
            headers: { 'Content-Type': 'application/json' },
            body:    JSON.stringify( { port } )
        } );
        const data = await resp.json();

        if ( !resp.ok ) { showCaptureMessage( 'bad', escHtml( data.error || 'Could not start waiting.' ) ); return; }

        updateCaptureAddress();
        renderCapture( data );
        startCapturePolling();
    } catch ( e ) { /* handled */ }
}

async function disarmCapture() { await captureAction( '/api/labels/capture/disarm' ); }
async function discardCapture() { await captureAction( '/api/labels/capture/discard' ); }

async function captureAction( url ) {
    hideCaptureMessage();
    try {
        const resp = await authFetch( url, { method: 'POST' } );
        renderCapture( await resp.json() );
    } catch ( e ) { /* handled */ }
}

async function refreshCapture() {
    try {
        const resp = await authFetch( '/api/labels/capture' );
        if ( resp.ok ) renderCapture( await resp.json() );
    } catch ( e ) { stopCapturePolling(); }
}

function startCapturePolling() {
    if ( capturePollTimer ) return;
    capturePollTimer = setInterval( refreshCapture, 1000 );
}

function stopCapturePolling() {
    if ( !capturePollTimer ) return;
    clearInterval( capturePollTimer );
    capturePollTimer = null;
}

function renderCapture( data ) {
    const waiting  = data.state === 'waiting';
    const captured = data.state === 'captured';

    document.getElementById( 'capture-arm-btn'  ).classList.toggle( 'hidden', waiting );
    document.getElementById( 'capture-stop-btn' ).classList.toggle( 'hidden', !waiting );
    document.getElementById( 'capture-result'   ).classList.toggle( 'hidden', !captured );

    const state = document.getElementById( 'capture-state' );
    if ( waiting )       state.textContent = 'waiting on port ' + data.port + '\u2026';
    else if ( captured ) state.textContent = data.bytes + ' bytes from ' + ( data.from || 'the proxy' );
    else                 state.textContent = '';

    if ( data.error ) showCaptureMessage( 'bad', escHtml( data.error ) );

    if ( !captured ) { if ( !waiting ) stopCapturePolling(); return; }

    stopCapturePolling();

    // Every field, in order. More than one can be the code - a receipt
    // torn in half carries it on both halves - so these are tick boxes
    // rather than a single choice.
    //
    // On a label Rock rendered without an attendance behind it the code
    // fields are EMPTY, so there is often no text to recognise. The
    // font height is what gives it away: the code is the one thing on a
    // check-in label printed large.
    const tallest = data.fields.reduce( ( m, f ) => Math.max( m, f.fontHeight || 0 ), 0 );

    document.getElementById( 'capture-fields' ).innerHTML = data.fields.length
        ? data.fields.map( f => {
            const big = tallest > 0 && f.fontHeight === tallest;
            const shown = f.text
                ? '<code class="bg-white px-1 rounded border border-gray-200">' + escHtml( f.text ) + '</code>'
                : '<span class="italic text-gray-400">empty</span>';
            const size = f.fontHeight
                ? '<span class="text-gray-400 tabular-nums">' + f.fontHeight + ' dots tall</span>'
                : '';
            return '<label class="flex items-center gap-2 cursor-pointer">' +
                '<input type="checkbox" name="capture-field" value="' + f.index + '"' +
                    ( big ? ' checked' : '' ) +
                    ' class="h-3 w-3 rounded border-gray-300 text-blue-600 focus:ring-blue-500">' +
                '<span class="text-gray-400 tabular-nums w-4">' + ( f.index + 1 ) + '.</span>' +
                shown + size +
            '</label>';
        } ).join( '' )
        : '<p class="text-red-700">No printable fields were found in that label.</p>';

    // The largest fields start ticked, which on a real check-in label is
    // the right answer. It is a starting point, not a decision - only
    // whoever designed the label knows for certain.
    document.getElementById( 'capture-hint' ).textContent = tallest > 0
        ? 'The largest fields are ticked to start with, since the code is normally the biggest thing on the label. Change it if that is wrong.'
        : '';
}

async function saveCapture() {
    hideCaptureMessage();

    const fields = [...document.querySelectorAll( 'input[name="capture-field"]:checked' )]
        .map( box => parseInt( box.value, 10 ) );
    const name   = document.getElementById( 'capture-name' ).value.trim();

    if ( !fields.length ) { showCaptureMessage( 'bad', 'Tick which fields hold the security code.' ); return; }
    if ( !name )          { showCaptureMessage( 'bad', 'Give the label a name.' ); return; }

    try {
        const resp = await authFetch( '/api/labels/capture/save', {
            method:  'POST',
            headers: { 'Content-Type': 'application/json' },
            body:    JSON.stringify( { name, fields } )
        } );
        const data = await resp.json();

        if ( !resp.ok ) { showCaptureMessage( 'bad', escHtml( data.error || 'That label was not saved.' ) ); return; }

        showCaptureMessage( 'ok', 'Saved <strong>' + escHtml( data.name ) + '</strong>.' );
        document.getElementById( 'capture-name' ).value = '';
        await refreshCapture();
        await loadLabels();
    } catch ( e ) { /* handled */ }
}

function toggleLabelHelp() {
    document.getElementById( 'label-help' ).classList.toggle( 'hidden' );
}

function toggleLabel( index ) {
    const name = blankOrder[index];
    if ( blankPicked.has( name ) ) blankPicked.delete( name ); else blankPicked.add( name );
    saveBlankPrefs();
    updateBlankTotals();
}

function moveLabel( index, delta ) {
    const target = index + delta;
    if ( target < 0 || target >= blankOrder.length ) return;
    const [moved] = blankOrder.splice( index, 1 );
    blankOrder.splice( target, 0, moved );
    saveBlankPrefs();
    renderLabelList();
}

// Copies, never labels. Somebody asking for 500 wants 500 usable sets,
// and the label count is shown next to it only so they know how much
// stock to have to hand.
function updateBlankTotals() {
    const copies = parseInt( document.getElementById( 'blank-quantity' ).value, 10 ) || 0;
    const picked = blankPicked.size;
    const el     = document.getElementById( 'blank-totals' );

    if ( !copies || !picked ) { el.textContent = 'Copies of the ticked labels, not a label count.'; return; }

    el.textContent = copies.toLocaleString() + ( copies === 1 ? ' copy · ' : ' copies · ' ) +
                     ( copies * picked ).toLocaleString() + ' labels';
}

function updateBlankMode() {
    const sequential = document.getElementById( 'blank-mode' ).value === 'sequential';
    document.getElementById( 'blank-start-field'  ).classList.toggle( 'hidden', !sequential );
    document.getElementById( 'blank-length-field' ).classList.toggle( 'hidden', sequential );
    renderSequentialNote();
}

async function uploadLabel( event ) {
    const file = event.target.files && event.target.files[0];
    event.target.value = '';
    if ( !file ) return;

    const name = file.name.replace( /\.(zpl|prn|txt)$/i, '' );
    const buffer = new Uint8Array( await file.arrayBuffer() );

    // Base64 by hand rather than FileReader, so the bytes go across
    // untouched - a template can legitimately contain bytes that are
    // not valid text.
    let binary = '';
    buffer.forEach( b => { binary += String.fromCharCode( b ); } );

    try {
        const resp = await authFetch( '/api/labels', {
            method:  'POST',
            headers: { 'Content-Type': 'application/json' },
            body:    JSON.stringify( { name, contentBase64: btoa( binary ) } )
        } );
        const data = await resp.json();

        if ( !resp.ok ) { showLabelMessage( 'bad', escHtml( data.error || 'That label was not stored.' ) ); return; }

        showLabelMessage( 'ok', 'Stored <strong>' + escHtml( data.name ) + '</strong>.' );
        await loadLabels();
    } catch ( e ) { /* authFetch already handled a 401 */ }
}

async function deleteLabel( index ) {
    const name = blankOrder[index];
    if ( !confirm( 'Delete "' + name + '"?\n\nThe three demo labels can be downloaded again from the project page; anything else you uploaded cannot.' ) ) return;

    try {
        const resp = await authFetch( '/api/labels/' + encodeURIComponent( name ), { method: 'DELETE' } );
        if ( !resp.ok ) {
            const data = await resp.json();
            showLabelMessage( 'bad', escHtml( data.error || 'That label was not deleted.' ) );
            return;
        }
        showLabelMessage( 'ok', 'Deleted <strong>' + escHtml( name ) + '</strong>.' );
        await loadLabels();
    } catch ( e ) { /* handled */ }
}

function firstPickedLabel() {
    for ( const name of blankOrder ) if ( blankPicked.has( name ) ) return name;
    return null;
}

async function previewLabel() {
    const names = blankOrder.filter( n => blankPicked.has( n ) );

    if ( !names.length ) { showBlankMessage( 'bad', 'Tick at least one label to preview.' ); return; }

    hideBlankMessage();
    openPreview( '<p class="text-sm text-gray-500">Drawing\u2026</p>', '' );

    const mode  = document.getElementById( 'blank-mode' ).value;
    const start = document.getElementById( 'blank-start' ).value.trim();
    const body  = {
        names, mode,
        codeLength: parseInt( document.getElementById( 'blank-length' ).value, 10 ) || 3,
        prefix:     document.getElementById( 'blank-prefix' ).value.trim()
    };
    if ( start ) body.start = start;

    let resp, data;
    try {
        resp = await authFetch( '/api/labels/preview', {
            method:  'POST',
            headers: { 'Content-Type': 'application/json' },
            body:    JSON.stringify( body )
        } );
        data = await resp.json();
    } catch ( e ) { closePreview(); return; }

    if ( resp.status === 429 ) {
        openPreview( '<p class="text-sm text-red-700">Too many previews at once. Try again in a second.</p>', '' );
        return;
    }
    if ( !resp.ok ) {
        openPreview( '<p class="text-sm text-red-700">' +
            escHtml( data.error || 'Those labels could not be drawn.' ) + '</p>', '' );
        return;
    }

    // One code across all of them, because that is what a copy is - the
    // child's tag and the parent's receipt carry the same one.
    const subtitle = names.length === 1
        ? 'Code ' + data.code
        : names.length + ' labels, all carrying the code ' + data.code + ' \u2014 that is one copy';

    openPreview( data.labels.map( l =>
        '<figure>' +
            '<img alt="' + escHtml( l.name ) + '" class="max-w-full border border-gray-300 bg-white" ' +
                 'src="data:image/png;base64,' + l.png + '">' +
            '<figcaption class="mt-1 text-xs text-gray-500">' + escHtml( l.name ) + ' \u00b7 ' +
                ( l.widthDots / data.dpi ).toFixed( 1 ) + ' \u00d7 ' +
                ( l.lengthDots / data.dpi ).toFixed( 1 ) + ' in at ' + data.dpi + ' dpi' +
            '</figcaption>' +
        '</figure>' ).join( '' ), subtitle );
}

function openPreview( html, subtitle ) {
    document.getElementById( 'preview-body' ).innerHTML = html;
    document.getElementById( 'preview-subtitle' ).textContent = subtitle;
    document.getElementById( 'preview-modal' ).classList.remove( 'hidden' );
    document.addEventListener( 'keydown', previewKeyHandler );
}

function closePreview() {
    document.getElementById( 'preview-modal' ).classList.add( 'hidden' );
    document.getElementById( 'preview-body' ).innerHTML = '';
    document.removeEventListener( 'keydown', previewKeyHandler );
}

function previewKeyHandler( event ) {
    if ( event.key === 'Escape' ) closePreview();
}

async function startBlankRun( isTest ) {
    // The box may be holding a saved printer's name rather than an
    // address, so it is resolved here. Everything past this point deals
    // in addresses, exactly as it did before names existed.
    const address = printerAddressForRun();
    const labels  = blankOrder.filter( n => blankPicked.has( n ) );
    const mode    = document.getElementById( 'blank-mode' ).value;
    const start   = document.getElementById( 'blank-start' ).value.trim();

    if ( !address ) { showBlankMessage( 'bad', 'Enter the printer address.' ); return; }
    if ( !labels.length ) { showBlankMessage( 'bad', 'Tick at least one label.' ); return; }

    // Advisory only - there is no limit on the server, and a thousand
    // sets has been printed in practice. This is here to catch the
    // stray extra zero, so it sits above anything anyone has asked for.
    const copies = parseInt( document.getElementById( 'blank-quantity' ).value, 10 ) || 0;
    if ( !isTest && copies > 1000 ) {
        if ( !confirm( copies.toLocaleString() + ' copies is ' +
                       ( copies * labels.length ).toLocaleString() +
                       ' labels. Make sure there is enough stock on the roll — the printer will pause ' +
                       'and carry on when it is reloaded, but it is a long wait.\n\nPrint them?' ) ) return;
    }

    // Starting below what has already gone out would put the same code
    // on two different stacks. Worth stopping to think about, but it is
    // still their call - there are good reasons to reprint.
    if ( mode === 'sequential' && !isTest ) {
        const from = start || blankSequential.sequentialNext || '';
        const used = blankSequential.sequentialReservedThrough;
        if ( from && used && Number( from ) <= Number( used ) ) {
            const last = ( blankSequential.history || [] )[0];
            const when = last ? '\n\nThe run on ' + fmtDate( last.startedAt ) + ' used ' +
                                last.firstCode + ' to ' + last.lastCode + '.' : '';
            if ( !confirm( 'Codes up to ' + used + ' have already been printed.' + when +
                           '\n\nStarting at ' + from + ' puts the same codes on a second stack. Print anyway?' ) ) return;
        }
    }

    saveBlankPrefs();
    hideBlankMessage();

    const body = {
        address, labels, mode,
        quantity:   parseInt( document.getElementById( 'blank-quantity' ).value, 10 ) || 0,
        codeLength: parseInt( document.getElementById( 'blank-length' ).value, 10 ) || 3,
        prefix:     document.getElementById( 'blank-prefix' ).value.trim(),
        hasCutter:  document.getElementById( 'blank-cutter' ).checked,
        test:       !!isTest
    };
    if ( start ) body.start = start;

    try {
        const resp = await authFetch( '/api/labels/print', {
            method:  'POST',
            headers: { 'Content-Type': 'application/json' },
            body:    JSON.stringify( body )
        } );
        const data = await resp.json();

        if ( resp.status === 429 ) { showBlankMessage( 'bad', 'Too many runs started at once. Wait a moment.' ); return; }
        if ( resp.status === 409 ) { showBlankMessage( 'bad', 'A run is already going. Wait for it to finish, or cancel it.' ); refreshRun(); return; }
        if ( !resp.ok ) { showBlankMessage( 'bad', escHtml( data.error || 'That run could not be started.' ) ); return; }

        blankRun = data;
        renderRun();
        startRunPolling();
    } catch ( e ) { /* handled */ }
}

async function cancelBlankRun() {
    if ( !blankRun ) return;
    try { await authFetch( '/api/labels/print/' + encodeURIComponent( blankRun.id ) + '/cancel', { method: 'POST' } ); }
    catch ( e ) { return; }
    refreshRun();
}

function startRunPolling() {
    if ( blankPollTimer ) return;
    blankPollTimer = setInterval( refreshRun, 1000 );
}

function stopRunPolling() {
    if ( !blankPollTimer ) return;
    clearInterval( blankPollTimer );
    blankPollTimer = null;
}

async function refreshRun() {
    let resp;
    try { resp = await authFetch( '/api/labels/print' ); }
    catch ( e ) { stopRunPolling(); return; }
    if ( !resp.ok ) return;

    const data = await resp.json();
    blankSequential = data;
    blankRun        = data.run;

    renderRun();
    renderSequentialNote();
    renderHistory();

    if ( blankRun && blankRun.status === 'running' ) startRunPolling(); else stopRunPolling();
}

function renderSequentialNote() {
    const note = document.getElementById( 'blank-sequential-note' );
    const box  = document.getElementById( 'blank-start' );
    const sequential = document.getElementById( 'blank-mode' ).value === 'sequential';

    note.classList.toggle( 'hidden', !sequential );

    if ( blankSequential.sequentialStateUnreadable ) {
        note.textContent = 'The record of used codes could not be read, so this needs a starting number. Check the last labels you printed.';
        note.className   = ( sequential ? '' : 'hidden ' ) + 'text-xs text-red-600';
        box.placeholder  = 'required';
        return;
    }

    note.className = ( sequential ? '' : 'hidden ' ) + 'text-xs text-gray-500';

    if ( blankSequential.sequentialNext ) {
        box.placeholder  = blankSequential.sequentialNext;
        note.textContent = 'Leave blank to carry on from ' + blankSequential.sequentialNext +
                           '. Codes up to ' + blankSequential.sequentialReservedThrough + ' have already gone out.';
    } else {
        note.textContent = 'No sequential run yet, so choose where to start.';
    }
}

function renderRun() {
    const card = document.getElementById( 'blank-run' );

    if ( !blankRun ) { card.classList.add( 'hidden' ); return; }

    const running = blankRun.status === 'running';
    const done    = blankRun.copiesHandedToPrinter;
    const total   = blankRun.quantity;

    document.getElementById( 'blank-run-title' ).textContent = {
        running:   'Printing',
        completed: 'Finished',
        cancelled: 'Cancelled',
        failed:    'Stopped'
    }[blankRun.status] || blankRun.status;

    // "Handed to the printer", never "printed". A completed write only
    // means the operating system took the bytes - the printer may still
    // be working through them, or may have jammed.
    let detail = done.toLocaleString() + ' of ' + total.toLocaleString() +
                 ( total === 1 ? ' copy' : ' copies' ) + ' handed to the printer';
    if ( blankRun.labelsPerCopy > 1 ) detail += ' · ' + ( done * blankRun.labelsPerCopy ).toLocaleString() + ' labels';
    if ( blankRun.error ) detail += ' · ' + blankRun.error;

    document.getElementById( 'blank-run-detail' ).textContent = detail;

    let codes = 'Codes ' + blankRun.firstCode + ' to ' + blankRun.lastCode;
    if ( blankRun.mode === 'sequential' ) {
        codes += ' were reserved and will not be reused';
        if ( blankSequential.sequentialNext ) codes += ' · the next run starts at ' + blankSequential.sequentialNext;
    }
    document.getElementById( 'blank-run-codes' ).textContent = codes;

    document.getElementById( 'blank-run-bar' ).style.width =
        ( total ? Math.round( 100 * done / total ) : 0 ) + '%';
    document.getElementById( 'blank-cancel-btn' ).classList.toggle( 'hidden', !running );

    card.classList.remove( 'hidden' );
}

function renderHistory() {
    const card = document.getElementById( 'blank-history-card' );
    const host = document.getElementById( 'blank-history' );
    const runs = blankSequential.history || [];

    if ( !runs.length ) { card.classList.add( 'hidden' ); return; }

    host.innerHTML = runs.map( r => {
        const colour = r.status === 'completed' ? 'text-green-700'
                     : r.status === 'failed'    ? 'text-red-700' : 'text-gray-500';
        return '<div class="flex items-baseline justify-between gap-3 py-1.5 border-b border-gray-100 last:border-0">' +
            '<span class="text-xs text-gray-500 tabular-nums whitespace-nowrap">' + escHtml( fmtDate( r.startedAt ) ) + '</span>' +
            '<span class="flex-1 text-sm text-gray-700 truncate">' +
                escHtml( r.firstCode + '–' + r.lastCode ) + ' · ' +
                escHtml( r.labels.join( ', ' ) ) + '</span>' +
            '<span class="text-xs tabular-nums whitespace-nowrap ' + colour + '">' +
                r.copiesHandedToPrinter + '/' + r.quantity + ' ' + escHtml( r.status ) + '</span>' +
        '</div>';
    } ).join( '' );

    card.classList.remove( 'hidden' );
}

async function restartService() {
    const note = authRequired
        ? '\n\nYou will need to log in again after the restart.'
        : '\n\nThe page will reload automatically.';
    if ( !confirm( 'Restart the proxy container?' + note ) ) return;

    document.getElementById( 'restart-overlay' ).classList.remove( 'hidden' );

    // Trigger the restart. The server may close the socket before the
    // response completes — that is expected and harmless.
    try {
        const headers = authToken ? { 'Authorization': 'Bearer ' + authToken } : {};
        await fetch( '/api/restart', { method: 'POST', headers } );
    } catch ( _ ) {}

    // Poll the public config endpoint (no auth required) until the
    // server comes back, then do a full page reload to re-init auth.
    for ( let i = 0; i < 40; i++ ) {
        await new Promise( r => setTimeout( r, 1000 ) );
        try {
            const r = await fetch( '/api/auth/config' );
            if ( r.ok ) { window.location.reload(); return; }
        } catch ( _ ) {}
    }
    window.location.reload();
}

// ── Event wiring ──────────────────────────────────────────────────
// The content security policy allows scripts from this origin only, which
// also forbids inline onclick="" attributes. Buttons name what they do in a
// data-action attribute instead, and one listener on the document runs it.
// Delegating from the document means buttons that are rebuilt from HTML
// strings - the label list, the dismiss button on a notice - need no
// wiring of their own.
//
// A table rather than looking the name up on window, so markup can only
// reach the handful of functions listed here.
const clickActions = {
    doLogin:                  () => doLogin(),
    doLogout:                 () => doLogout(),
    toggleTheme:              () => toggleTheme(),
    restartService:           () => restartService(),
    showTab:                  el => showTab( el.dataset.arg ),
    clearLogs:                () => clearLogs(),
    testPrinter:              () => testPrinter(),
    toggleLabelHelp:          () => toggleLabelHelp(),
    toggleCapture:            () => toggleCapture(),
    armCapture:               () => armCapture(),
    disarmCapture:            () => disarmCapture(),
    saveCapture:              () => saveCapture(),
    discardCapture:           () => discardCapture(),
    openPrinterDialog:        () => openPrinterDialog(),
    closePrinterDialog:       () => closePrinterDialog(),
    savePrinterDialog:        () => savePrinterDialog(),
    startBlankRun:            () => startBlankRun( false ),
    startTestRun:             () => startBlankRun( true ),
    previewLabel:             () => previewLabel(),
    closePreview:             () => closePreview(),
    cancelBlankRun:           () => cancelBlankRun(),
    saveSettings:             () => saveSettings(),
    saveNotificationSettings: () => saveNotificationSettings(),
    sendTestNotification:     () => sendTestNotification(),
    savePin:                  () => savePin(),
    changePin:                () => changePin(),
    hideBoxMessage:           el => hideBoxMessage( el.dataset.arg ),
    moveLabel:                el => moveLabel( Number( el.dataset.index ), Number( el.dataset.delta ) ),
    deleteLabel:              el => deleteLabel( Number( el.dataset.index ) ),
};

document.addEventListener( 'click', event => {
    const el = event.target.closest( '[data-action]' );
    if ( !el ) return;

    const action = clickActions[el.dataset.action];
    if ( action ) action( el );
} );

// The label list's checkboxes are rebuilt on every render, so their change
// events are delegated the same way.
document.addEventListener( 'change', event => {
    const el = event.target.closest( '[data-change="toggleLabel"]' );
    if ( el ) toggleLabel( Number( el.dataset.index ) );
} );

// Everything else is a fixed element that exists from the start.
function on( id, type, handler ) {
    document.getElementById( id ).addEventListener( type, handler );
}

on( 'login-pin', 'keydown', event => { if ( event.key === 'Enter' ) doLogin(); } );
on( 'label-upload', 'change', event => uploadLabel( event ) );
on( 'capture-port', 'input', () => updateCaptureAddress() );
on( 'blank-address', 'input', () => printerTyped() );
on( 'blank-address', 'focus', () => openPrinterList() );
on( 'blank-address', 'keydown', event => printerListKey( event ) );
on( 'blank-address', 'blur', () => closePrinterList() );
on( 'blank-cutter', 'change', () => saveBlankPrefs() );
on( 'blank-quantity', 'input', () => updateBlankTotals() );
on( 'blank-mode', 'change', () => updateBlankMode() );
on( 'printer-modal-name', 'keydown', event => printerDialogKey( event ) );
on( 'printer-modal-address', 'keydown', event => printerDialogKey( event ) );

// ── Start ─────────────────────────────────────────────────────────
boot();
