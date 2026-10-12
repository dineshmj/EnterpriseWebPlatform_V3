// Replaces Next.js's built-in 404 page, which carries inline CSS that the Content-Security-Policy
// does not allow (no 'unsafe-inline' for styles): the same message, styled by class.
export default function NotFound() {
  return (
    <main className="mx-auto max-w-xl px-6 py-24 text-center">
      <h1 className="font-display text-2xl font-semibold text-ink">Page not found</h1>
      <p className="mt-2 text-sm text-ink-muted">This page does not exist. Choose an item from the menu to continue.</p>
    </main>
  );
}