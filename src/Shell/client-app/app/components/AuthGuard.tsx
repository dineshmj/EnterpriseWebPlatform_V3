'use client';

import { useAuth } from '../hooks/useAuth';
import { UNAVAILABLE_RETRY_SECONDS } from '../lib/auth';

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
  const { isAuthenticated, isLoading, isUnavailable } = useAuth(true);

  // The BFF answered 503: say so, and keep asking (useAuth) - not a white page, not a sign-in loop.
  if (isUnavailable) {
    return (
      <div className="auth-loading" role="alert">
        <p className="auth-loading-text">
          EWP is temporarily unavailable. Trying again in {UNAVAILABLE_RETRY_SECONDS} seconds...
        </p>
      </div>
    );
  }

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