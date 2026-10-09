import { defineConfig } from '@playwright/test';
import './support/env';

/**
 * EWP V3 end-to-end and security tests, through the real front ends, in Microsoft Edge.
 *
 * The platform must be running (Visual Studio's multiple start-up profile, the Node.js services,
 * PostgreSQL and Kafka - see tst\README.md). The projects run in this order:
 *   smoke       every service answers its readiness probe - the others need it first
 *   seed        demo records (Camilla Parker, Jason Millers, payments) after a database re-create
 *   functional  one screen or feature at a time
 *   journeys    several people end to end (agent → KYC → Compliance → Accounts; maker → checker)
 *   security    headers, sessions, CSRF, authorization, browser boundary, input, rate limits, MFA
 *
 * One worker: the tests share one platform and its data (sagas, work queues), so they run in turn.
 */
export default defineConfig({
  testDir: './tests',
  outputDir: './test-results',
  timeout: 90_000,
  expect: { timeout: 15_000 },
  fullyParallel: false,
  workers: 1,
  retries: 0,
  forbidOnly: !!process.env.CI,
  reporter: [['list'], ['html', { open: 'never', outputFolder: 'playwright-report' }]],

  use: {
    channel: 'msedge',
    headless: true,
    viewport: { width: 1600, height: 900 },
    // Edge trusts the ASP.NET Core development certificate (Windows store); Node.js - used by
    // the request-only tests - does not, so certificate errors are ignored for those calls.
    ignoreHTTPSErrors: true,
    actionTimeout: 15_000,
    navigationTimeout: 30_000,
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
    video: 'retain-on-failure',
  },

  projects: [
    { name: 'smoke', testDir: './tests/00-smoke' },
    { name: 'seed', testDir: './tests/10-seed', dependencies: ['smoke'] },
    { name: 'functional', testDir: './tests/20-functional', dependencies: ['smoke'] },
    { name: 'journeys', testDir: './tests/30-journeys', dependencies: ['smoke'] },
    { name: 'security', testDir: './tests/40-security', dependencies: ['smoke'] },
  ],
});