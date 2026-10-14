'use client';

import { useAuth } from '../hooks/useAuth';

interface AuthGuardProps {
  children: React.ReactNode;
  loadingComponent?: React.ReactNode;
}

/*
 // ┌─ :: SECURITY :: Nothing renders before the BFF says "signed in" ───────────────────────────────
 // │ Why : Showing the Shell before the session is confirmed would flash protected UI and start
 // │       calls that fail.
 // │ How : The guard asks the BFF (/bff/user) and renders only for a signed-in person; otherwise it
 // │       sends the browser to the BFF's login, which runs the OIDC flow server-side.
 // │ Ref : Features 2.1.1 | OWASP A01 (the UI is a convenience, the BFF and APIs decide)
 // └────────────────────────────────────────────────────────────────────────────────────────────────
 * Component that protects routes requiring authentication
 * Automatically redirects to login if user is not authenticated
 */
export function AuthGuard({ children, loadingComponent }: AuthGuardProps) {
  const { isAuthenticated, isLoading } = useAuth(true);

  if (isLoading) {
    return (
      <>
        {loadingComponent || (
          // Classes, not inline styles: the Content-Security-Policy allows no inline CSS (globals.css).
          <div className="auth-loading">
            <div className="auth-loading-spinner" aria-hidden="true"></div>
            <p className="auth-loading-text">Loading...</p>
          </div>
        )}
      </>
    );
  }

  // If not authenticated, useAuth hook will redirect
  // Only render children if authenticated
  if (!isAuthenticated) {
    return null;
  }

  return <>{children}</>;
}