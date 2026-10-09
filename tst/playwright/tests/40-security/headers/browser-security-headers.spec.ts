import { expect, test, type APIResponse } from '@playwright/test';
import { urls } from '../../../support/services';

/**
 * Every page a browser loads carries the platform's browser-security headers. Checked over plain
 * HTTP requests (no browser): fast, and it sees exactly what the server sends.
 *
 *   Content-Security-Policy   scripts and styles without 'unsafe-inline' / 'unsafe-eval' (XSS, CSS
 *                             injection), no plugins, base and form targets limited, and framing
 *                             limited to who may frame that application (clickjacking)
 *   X-Content-Type-Options    nosniff (no MIME sniffing)
 *   Referrer-Policy           full URLs never leak to other sites
 *
 * HSTS is not checked: the services send it outside Development only.
 */
interface FrontEnd {
  name: string;
  /** A page of the application (an exported page, or the IDP's sign-in page). */
  page: string;
  /** Who may frame it: 'none', or these origins exactly. */
  frameAncestors: 'none' | string[];
}

const mfeFramers = [urls.shell, urls.idp];

const frontEnds: FrontEnd[] = [
  { name: 'Shell', page: `${urls.shell}/`, frameAncestors: 'none' },
  { name: 'IDP sign-in page', page: `${urls.idp}/Account/Login`, frameAncestors: 'none' },
  { name: 'Customer Onboarding MFE', page: `${urls.customerOnboardingMfe}/v1/customers/view-all/`, frameAncestors: mfeFramers },
  { name: 'Customer KYC MFE', page: `${urls.kycMfe}/v1/kyc/cases/view-all/`, frameAncestors: mfeFramers },
  { name: 'Compliance MFE', page: `${urls.complianceMfe}/v1/compliance/view-all/`, frameAncestors: mfeFramers },
  { name: 'Accounts MFE', page: `${urls.accountsMfe}/v1/accounts/view-all/`, frameAncestors: mfeFramers },
  { name: 'Payments MFE', page: `${urls.paymentsMfe}/v1/payments/view-all/`, frameAncestors: mfeFramers },
  { name: 'Audit web', page: `${urls.auditMfe}/v1/audit/trail/view-all`, frameAncestors: mfeFramers },
];

/** The policy as directive → sources. */
function parseCsp(header: string): Map<string, string[]> {
  const directives = new Map<string, string[]>();
  for (const part of header.split(';').map(p => p.trim()).filter(Boolean)) {
    const [name, ...sources] = part.split(/\s+/);
    directives.set(name.toLowerCase(), sources);
  }
  return directives;
}

/** A directive's sources, falling back to default-src as browsers do. */
function sourcesOf(csp: Map<string, string[]>, directive: string): string[] {
  return csp.get(directive) ?? csp.get('default-src') ?? [];
}

for (const frontEnd of frontEnds) {
  test.describe(`${frontEnd.name} - browser security headers`, () => {
    let response: APIResponse;
    let csp: Map<string, string[]>;

    test.beforeAll(async ({ playwright }) => {
      const request = await playwright.request.newContext({ ignoreHTTPSErrors: true });
      // No redirects: the headers of the page itself (the Audit web app may redirect to sign-in).
      response = await request.get(frontEnd.page, { maxRedirects: 0, timeout: 15_000 });
      csp = parseCsp(response.headers()['content-security-policy'] ?? '');
    });

    test('Should_SendAnEnforcedContentSecurityPolicy', async () => {
      // Arrange
      const headers = response.headers();

      // Act
      const enforced = headers['content-security-policy'];
      const reportOnly = headers['content-security-policy-report-only'];

      // Assert
      expect(enforced, `no Content-Security-Policy on ${frontEnd.page}${reportOnly ? ' (only Report-Only: switch Security:CspReportOnly / *_CSP_REPORT_ONLY off)' : ''}`).toBeTruthy();
    });

    test('ShouldNotAllow_InlineOrEvalScripts', async () => {
      // Arrange
      const scripts = sourcesOf(csp, 'script-src');

      // Act
      const unsafe = scripts.filter(s => s === "'unsafe-inline'" || s === "'unsafe-eval'" || s === '*');

      // Assert
      expect(scripts.length, 'script-src (or default-src) is missing').toBeGreaterThan(0);
      expect(unsafe, `script-src allows ${unsafe.join(' ')}`).toEqual([]);
    });

    test('ShouldNotAllow_InlineStyles', async () => {
      // Arrange
      const styles = sourcesOf(csp, 'style-src');

      // Act
      const unsafe = styles.filter(s => s === "'unsafe-inline'" || s === '*');

      // Assert
      expect(styles.length, 'style-src (or default-src) is missing').toBeGreaterThan(0);
      expect(unsafe, frontEnd.name === 'Audit web'
        ? "style-src allows 'unsafe-inline': is the Audit web app running in development mode (runnow.bat dev)? Start it with Start-NodeServices.ps1."
        : `style-src allows ${unsafe.join(' ')}`).toEqual([]);
    });

    test('ShouldAllow_NoPlugins_AndOnlyOwnBaseAndFormTargets', async () => {
      // Arrange + Act
      const objects = sourcesOf(csp, 'object-src');
      const base = csp.get('base-uri') ?? [];
      const forms = csp.get('form-action') ?? [];

      // Assert
      expect(objects, 'object-src').toEqual(["'none'"]);
      expect(base, 'base-uri').toEqual(["'self'"]);
      expect(forms, 'form-action').toContain("'self'");
    });

    test('ShouldLimit_WhoMayFrameIt', async () => {
      // Arrange
      const ancestors = csp.get('frame-ancestors') ?? [];

      // Act
      const allowed = ancestors.map(a => a.replace(/\/$/, ''));

      // Assert
      if (frontEnd.frameAncestors === 'none') {
        expect(allowed, 'frame-ancestors').toEqual(["'none'"]);
      } else {
        expect(allowed.sort(), 'frame-ancestors').toEqual([...frontEnd.frameAncestors].sort());
      }
    });

    test('ShouldForbid_MimeSniffing_AndLeakingTheReferrer', async () => {
      // Arrange
      const headers = response.headers();

      // Act
      const nosniff = headers['x-content-type-options'];
      const referrer = headers['referrer-policy'];

      // Assert
      expect(nosniff, 'X-Content-Type-Options').toBe('nosniff');
      expect(['no-referrer', 'same-origin', 'strict-origin', 'strict-origin-when-cross-origin'], `Referrer-Policy "${referrer}"`)
        .toContain(referrer);
    });
  });
}