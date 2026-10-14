// Replaces Next.js's built-in 404 page, which carries inline CSS that the Content-Security-Policy
// does not allow (no 'unsafe-inline' for styles): the same message, styled by class.
// ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
// │ Own not-found page without inline styles: Next.js's default carries inline CSS, which
// │ the strict CSP would otherwise force us to allow ('unsafe-inline').
// └──────────────────────────────────────────────────────────────────────────────────────
export default function NotFound() {
  return (
    <main className="not-found">
      <h1>Page not found</h1>
      <p>This page does not exist. <a href="/">Back to the workspace</a>.</p>
    </main>
  );
}