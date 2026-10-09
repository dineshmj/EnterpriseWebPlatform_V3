// Replaces Next.js's built-in 404 page, which carries inline CSS that the Content-Security-Policy
// does not allow (no 'unsafe-inline' for styles): the same message, styled by class.
export default function NotFound() {
  return (
    <main className="not-found">
      <h1>Page not found</h1>
      <p>This page does not exist. <a href="/">Back to the workspace</a>.</p>
    </main>
  );
}