// Read (and then erase) the URL fragment the app was opened with.
//
// The desktop client's "Server Settings..." button opens this page at
// #admin=<token>&page=settings - a fragment rather than a query string because a
// fragment is never sent to the server as part of the request, so a short-lived
// admin token cannot end up in an access log or a Referer header on the way in.
//
// Erasing it once read is the other half of that: history.replaceState rewrites
// the address bar without navigating, so the token does not sit in the URL for
// the rest of the session, in the browser's history, or in a screenshot.
export function getHash() {
    return globalThis.location.hash ?? "";
}

export function clearHash() {
    const url = globalThis.location.pathname + globalThis.location.search;
    globalThis.history.replaceState(null, "", url);
}

export function getOrigin() {
    return globalThis.location.origin ?? "";
}
