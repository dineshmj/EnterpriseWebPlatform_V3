'use client';

import { useEffect, useState } from 'react';
import { checkAuthentication, redirectToLogin, UNAVAILABLE_RETRY_SECONDS, type AuthState } from '../lib/auth';

/*
 * Custom hook for authentication
 * Automatically checks auth status and redirects to login if not authenticated
 */
export function useAuth(autoRedirect: boolean = true) {
  const [authState, setAuthState] = useState<AuthState>({
    isAuthenticated: false,
    user: null,
    isLoading: true,
  });

  useEffect(() => {
    let retry: ReturnType<typeof setTimeout> | undefined;
    let cancelled = false;
    const checkAuth = async () => {
      const state = await checkAuthentication();
      if (cancelled) return;
      setAuthState(state);

      // Temporarily unavailable: ask again later - never send the person to sign in for it.
      if (state.isUnavailable) {
        retry = setTimeout(checkAuth, UNAVAILABLE_RETRY_SECONDS * 1000);
        return;
      }

      // Auto-redirect to login if not authenticated
      if (autoRedirect && !state.isAuthenticated && !state.isLoading) {
        redirectToLogin();
      }
    };

    void checkAuth();
    return () => {
      cancelled = true;
      if (retry) clearTimeout(retry);
    };
  }, [autoRedirect]);

  return authState;
}