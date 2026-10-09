'use client';

import { useAuth } from '../hooks/useAuth';

interface AuthGuardProps {
  children: React.ReactNode;
  loadingComponent?: React.ReactNode;
}

/*
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