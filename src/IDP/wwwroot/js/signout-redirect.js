// After sign-out, wait for the front-channel logout iframe (which notifies every
// client) to finish loading, then return the user to the application.
// An external file because the IDP's CSP does not allow inline scripts.
window.addEventListener('load', function () {
    var link = document.querySelector('a.post-logout-redirect');
    if (!link) return;

    var iframe = document.querySelector('iframe.signout');
    var go = function () { window.location.href = link.href; };

    if (iframe) {
        iframe.addEventListener('load', go);
        // Do not strand the user if a client's logout endpoint is slow.
        window.setTimeout(go, 3000);
    } else {
        go();
    }
});
