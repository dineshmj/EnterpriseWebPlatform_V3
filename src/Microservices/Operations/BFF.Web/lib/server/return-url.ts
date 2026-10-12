/**
 * The pages a sign-in may return to - an allow-list, never an arbitrary URL (open redirect).
 * This app has one page, so every return goes there, whatever was asked for.
 */
export const HEALTH_PAGE = '/v1/operations/platform/view-all';

export function safeReturnUrl(_requested: string | null | undefined): string {
  return HEALTH_PAGE;
}