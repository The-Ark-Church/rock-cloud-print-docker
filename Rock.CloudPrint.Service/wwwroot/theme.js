// Loaded from <head> without defer or async, so it runs before the body is
// parsed. That is the point of it: a separate file only because the content
// security policy forbids inline script, not so it can load later.
//
// Decided before the first paint, so a dark page does not flash white
// on the way in. Reads the same key the toggle in the header writes:
// a stored choice wins, and the browser's own setting is followed
// until somebody makes one.
( function () {
    try {
        var chosen = localStorage.getItem( 'rock-cp-theme' );
        var dark = chosen
            ? chosen === 'dark'
            : window.matchMedia( '(prefers-color-scheme: dark)' ).matches;

        if ( dark ) document.documentElement.classList.add( 'dark' );
    } catch ( e ) {
        // Private browsing, or storage turned off. Light is a fine
        // thing to fall back to, and the toggle still works.
    }
} )();
