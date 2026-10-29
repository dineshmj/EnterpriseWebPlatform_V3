import { NotFoundException, ServiceUnavailableException } from '@nestjs/common';
import type { JourneyOptions } from '../src/configuration/journey-options';
import type { PlatformComponent, SimulatorKey } from '../src/platform/components';
import { HealthProbeService, SimulatorService } from '../src/platform/platform.services';
import { LoggedRequest, received, resetWireMock, stub, wireMockUrl } from '../../../../../tst/node/wiremock/wiremock';

// ┌─ :: OBSERVABILITY + SECURITY :: Platform Health and the simulators' switch, against WireMock ──
// │ Why : Platform Health must say what each component says about itself (or that it does not
// │       answer) without ever passing on a probe's details; the simulators' switch must reach
// │       only the configured simulator, with its admin key, and must undo itself.
// │ How : The REAL HealthProbeService and SimulatorService; WireMock plays every readiness probe
// │       and the four simulators' admin endpoints; each test stages the answers, then checks
// │       the result and what WireMock received.
// │ Ref : src/platform/platform.services.ts | Features 1.7.4 | Threat-Model (Operations) | CWE-209
// └────────────────────────────────────────────────────────────────────────────────────────────────
describe('HealthProbeService - every readiness probe played by WireMock', () => {
  const component = (name: string, path: string): PlatformComponent => ({ name, group: 'Domain APIs', readiness: `${wireMockUrl()}${path}` });

  const probe = (components: PlatformComponent[]) =>
    new HealthProbeService({ platform: { components, simulatorBaseUrls: {} } } as unknown as JourneyOptions).probeAll();

  beforeEach(resetWireMock);

  it('Given components reporting Healthy, Degraded and Unhealthy, when they are probed, then each status is shown as reported - and nothing else leaves', async () => {
    // Given: the probes' details describe the internals (database, Kafka, outbox backlog).
    const details = { database: 'Host=db-01;Username=accounts', outboxBacklog: 1234 };
    await stub({ request: { method: 'GET', urlPath: '/accounts/health/ready' }, response: { status: 200, jsonBody: { status: 'Healthy', details } } });
    await stub({ request: { method: 'GET', urlPath: '/payments/health/ready' }, response: { status: 200, jsonBody: { status: 'Degraded', details } } });
    await stub({ request: { method: 'GET', urlPath: '/audit/health/ready' }, response: { status: 503, jsonBody: { status: 'Unhealthy', details } } });

    // When
    const { components } = await probe([
      component('Accounts API', '/accounts/health/ready'),
      component('Payments API', '/payments/health/ready'),
      component('Audit API', '/audit/health/ready'),
    ]);

    // Then
    expect(components.map(c => [c.name, c.status])).toEqual([['Accounts API', 'Healthy'], ['Payments API', 'Degraded'], ['Audit API', 'Unhealthy']]);
    for (const c of components) {
      expect(Object.keys(c).sort()).toEqual(['group', 'name', 'responseMs', 'status']);
      expect(c.responseMs).toEqual(expect.any(Number));
    }
  });

  it('Given a web app\'s home page (no health endpoint), when it is probed, then a page or a redirect means up - and the redirect is not followed', async () => {
    // Given
    await stub({ request: { method: 'GET', urlPath: '/web' }, response: { status: 200, body: '<html>EWP</html>' } });
    await stub({ request: { method: 'GET', urlPath: '/audit-web' }, response: { status: 307, headers: { Location: `${wireMockUrl()}/sign-in` } } });
    await stub({ request: { method: 'GET', urlPath: '/sign-in' }, response: { status: 500 } });

    // When
    const { components } = await probe([component('Web', '/web'), component('Audit web', '/audit-web')]);

    // Then
    expect(components.map(c => c.status)).toEqual(['Healthy', 'Healthy']);
    expect(await received('GET', '/sign-in')).toHaveLength(0);
  });

  it('Given an answer without a status (503, empty body), when it is probed, then the HTTP status decides: Unhealthy', async () => {
    // Given
    await stub({ request: { method: 'GET', urlPath: '/worker/health' }, response: { status: 503 } });

    // When
    const { components } = await probe([component('Payments worker', '/worker/health')]);

    // Then
    expect(components[0].status).toBe('Unhealthy');
  });

  it('Given one probe hangs past 3 s and one is unreachable, when they are probed, then both are Unreachable and the others still answer', async () => {
    // Given
    await stub({ request: { method: 'GET', urlPath: '/slow/health' }, response: { status: 200, jsonBody: { status: 'Healthy' }, fixedDelayMilliseconds: 4000 } });
    await stub({ request: { method: 'GET', urlPath: '/fine/health' }, response: { status: 200, jsonBody: { status: 'Healthy' } } });

    // When
    const { components } = await probe([
      component('Slow API', '/slow/health'),
      { name: 'Gone API', group: 'Domain APIs', readiness: 'http://127.0.0.1:9/health' },
      component('Fine API', '/fine/health'),
    ]);

    // Then
    expect(components.map(c => [c.status, c.responseMs === null])).toEqual([['Unreachable', true], ['Unreachable', true], ['Healthy', false]]);
  });
});

describe('SimulatorService - the four simulators played by WireMock', () => {
  const ADMIN_KEY = 'cluster-admin-key';
  const paths: Record<SimulatorKey, string> = {
    'screening-provider': '/screening-provider',
    'core-banking': '/core-banking',
    'payment-network': '/payment-network',
    'fraud-scoring': '/fraud-scoring',
  };
  let service: SimulatorService;

  const create = (overrides: Partial<JourneyOptions> = {}) => new SimulatorService({
    simulatorControlsEnabled: true,
    simulatorAutoRevertMinutes: 15,
    simulatorAdminKey: '',
    platform: {
      components: [],
      simulatorBaseUrls: Object.fromEntries(Object.entries(paths).map(([key, path]) => [key, `${wireMockUrl()}${path}`])),
    },
    ...overrides,
  } as JourneyOptions);

  const behaviourOf = (key: SimulatorKey, status: number, jsonBody?: unknown) =>
    stub({ request: { method: 'GET', urlPath: `${paths[key]}/admin/behaviour` }, response: { status, jsonBody } });

  const accepts = (key: SimulatorKey) =>
    stub({ request: { method: 'PUT', urlPath: `${paths[key]}/admin/behaviour` }, response: { status: 204 } });

  const putsTo = async (key: SimulatorKey): Promise<LoggedRequest[]> => received('PUT', `${paths[key]}/admin/behaviour`);

  /** Waits until WireMock has received `count` changes for this simulator (the automatic revert runs on a timer). */
  const untilPuts = async (key: SimulatorKey, count: number): Promise<LoggedRequest[]> => {
    for (const deadline = Date.now() + 5000; Date.now() < deadline; await new Promise(r => setTimeout(r, 50))) {
      const puts = await putsTo(key);
      if (puts.length >= count) return puts;
    }
    return putsTo(key);
  };

  beforeAll(() => {
    jest.spyOn(console, 'log').mockImplementation(() => undefined);     // the "who changed what" lines are expected here
    jest.spyOn(console, 'error').mockImplementation(() => undefined);
  });

  beforeEach(async () => {
    await resetWireMock();
    service = create();
  });

  afterEach(() => service.onModuleDestroy());

  it('Given three simulators answering and one not, when they are listed, then the silent one is "Unknown" - not an error', async () => {
    // Given
    await behaviourOf('screening-provider', 200, { behaviour: 'Healthy' });
    await behaviourOf('core-banking', 200, { behaviour: 'Slow' });
    await behaviourOf('payment-network', 500);
    await behaviourOf('fraud-scoring', 200, { behaviour: 'Failing' });

    // When
    const list = await service.list();

    // Then
    expect(list.map(s => [s.key, s.behaviour])).toEqual([['screening-provider', 'Healthy'], ['core-banking', 'Slow'], ['payment-network', 'Unknown'], ['fraud-scoring', 'Failing']]);
  });

  it('Given a cluster (admin key set), when the simulators are read and changed, then the key goes with every call; on a laptop (no key), it is absent', async () => {
    // Given
    service = create({ simulatorAdminKey: ADMIN_KEY });
    await behaviourOf('core-banking', 200, { behaviour: 'Healthy' });
    await accepts('core-banking');

    // When
    await service.list();
    await service.change('core-banking', 'Healthy', 'ops.admin');
    await create().change('core-banking', 'Healthy', 'ops.admin');

    // Then
    const [read] = await received('GET', '/core-banking/admin/behaviour');
    const [withKey, withoutKey] = await putsTo('core-banking');
    expect(read.headers['X-Simulator-Admin-Key']).toBe(ADMIN_KEY);
    expect(withKey.headers['X-Simulator-Admin-Key']).toBe(ADMIN_KEY);
    expect(withoutKey.headers['X-Simulator-Admin-Key']).toBeUndefined();
  });

  it('Given a simulator switched to Down, when its time is up, then it goes back to Healthy by itself', async () => {
    // Given: a revert after 0.002 minutes (120 ms) instead of 15.
    service = create({ simulatorAutoRevertMinutes: 0.002 });
    await accepts('payment-network');

    // When
    await service.change('payment-network', 'Down', 'ops.admin');

    // Then
    const puts = await untilPuts('payment-network', 2);
    expect(puts.map(p => JSON.parse(p.body))).toEqual([{ behaviour: 'Down' }, { behaviour: 'Healthy' }]);
  });

  it('Given a simulator switched to Slow, when it is listed, then it shows when it goes back and who changed it', async () => {
    // Given
    await accepts('screening-provider');
    await behaviourOf('screening-provider', 200, { behaviour: 'Slow' });

    // When
    await service.change('screening-provider', 'Slow', 'ops.admin');
    const [screening] = await service.list();

    // Then
    expect(screening.changedBy).toBe('ops.admin');
    expect(Date.parse(screening.revertsAt!)).toBeGreaterThan(Date.now() + 14 * 60_000);
  });

  it('Given the simulator refuses the change (500), when it is changed, then the operator is told and no automatic revert is set', async () => {
    // Given
    await stub({ request: { method: 'PUT', urlPath: '/core-banking/admin/behaviour' }, response: { status: 500 } });
    await behaviourOf('core-banking', 200, { behaviour: 'Healthy' });

    // When
    const change = service.change('core-banking', 'Down', 'ops.admin');

    // Then
    await expect(change).rejects.toBeInstanceOf(ServiceUnavailableException);
    await expect(change).rejects.toThrow('The simulator refused the change (HTTP 500).');
    expect((await service.list())[1].revertsAt).toBeNull();
  });

  it('Given a simulator that redirects, when it is changed, then the redirect is not followed and the admin key goes nowhere else', async () => {
    // Given
    service = create({ simulatorAdminKey: ADMIN_KEY });
    await stub({ request: { method: 'PUT', urlPath: '/core-banking/admin/behaviour' }, response: { status: 307, headers: { Location: `${wireMockUrl()}/elsewhere` } } });
    await stub({ request: { method: 'PUT', urlPath: '/elsewhere' }, response: { status: 204 } });

    // When
    const change = service.change('core-banking', 'Down', 'ops.admin');

    // Then
    await expect(change).rejects.toBeInstanceOf(ServiceUnavailableException);
    expect(await received('PUT', '/elsewhere')).toHaveLength(0);
  });

  it('Given the switch is off (outside Development and Test), when the simulators are listed or changed, then the feature does not exist (404) and nothing is called', async () => {
    // Given
    service = create({ simulatorControlsEnabled: false });

    // When / Then
    await expect(service.list()).rejects.toBeInstanceOf(NotFoundException);
    await expect(service.change('core-banking', 'Down', 'ops.admin')).rejects.toBeInstanceOf(NotFoundException);
    expect(await putsTo('core-banking')).toHaveLength(0);
    expect(await received('GET', '/core-banking/admin/behaviour')).toHaveLength(0);
  });
});