import { expect, test } from '@playwright/test';
import { services } from '../../support/services';

/**
 * Is the platform up? Every service's readiness probe (/health/ready) answers 200: Healthy, or
 * Degraded ("working, but look" - shown as an annotation, not a failure). Every other project
 * depends on this one, so a stopped service is reported once, by name, before any screen test
 * fails with a confusing timeout.
 */
test.describe('Readiness of every service', () => {
  for (const service of services) {
    test(`${service.name} is ready`, async ({ request }) => {
      // Arrange
      const hint = `${service.name} is not answering at ${service.readiness} - start it (${service.startedBy}).`;

      // Act
      const response = await request.get(service.readiness, { timeout: 10_000, maxRedirects: 0 }).catch(() => null);

      // Assert
      expect(response, hint).not.toBeNull();
      if (!service.readiness.endsWith('/health/ready')) {
        // No probe of its own (Audit web): any answer below 500 proves it is up.
        expect(response!.status(), hint).toBeLessThan(500);
        return;
      }

      const body = await response!.json().catch(() => ({})) as { status?: string; checks?: { name: string; status: string }[] };
      const failing = (body.checks ?? []).filter(c => c.status !== 'Healthy').map(c => `${c.name}: ${c.status}`).join(', ');
      expect(response!.status(), `${service.name} is not ready (${failing || response!.status()}).`).toBe(200);
      if (body.status === 'Degraded') {
        test.info().annotations.push({ type: 'degraded', description: `${service.name}: ${failing}` });
      }
    });
  }
});