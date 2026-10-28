import { ServiceUnavailableException } from '@nestjs/common';
import type { JourneyOptions } from '../src/configuration/journey-options';
import { ToxiproxyFaultController } from '../src/faults/toxiproxy-fault-controller';
import { received, resetWireMock, stub, wireMockUrl } from '../../../../../tst/node/wiremock/wiremock';

// ┌─ :: RESILIENCE + SECURITY :: The fault controller against a Toxiproxy played by WireMock ──────
// │ Why : A route must end up doing exactly what the operator chose - and only this API's own
// │       toxics may be touched: whatever else is set on a route (another tool's, a person's)
// │       stays. The proxy's admin API has no authentication, so nothing is followed elsewhere.
// │ How : The REAL ToxiproxyFaultController; WireMock answers Toxiproxy's admin API (GET
// │       /proxies, toxics, enable / disable, /reset); each test stages the proxy's state, then
// │       checks the calls WireMock received.
// │ Ref : src/faults/toxiproxy-fault-controller.ts | Features 1.7.4 | github.com/Shopify/toxiproxy (HTTP API)
// └────────────────────────────────────────────────────────────────────────────────────────────────
describe('ToxiproxyFaultController - Toxiproxy\'s admin API played by WireMock', () => {
  let controller: ToxiproxyFaultController;

  const proxyIs = (name: string, enabled: boolean, toxics: string[]) => stub({
    request: { method: 'GET', urlPath: `/proxies/${name}` },
    response: { status: 200, jsonBody: { name, enabled, toxics: toxics.map(t => ({ name: t })) } },
  });

  const acceptsEverything = async () => {
    for (const method of ['POST', 'DELETE']) {
      await stub({ request: { method, urlPath: '/proxies/accounts-api' }, response: { status: 200, jsonBody: {} } });
      await stub({ request: { method, urlPath: '/proxies/accounts-api/toxics' }, response: { status: 200, jsonBody: {} } });
      for (const toxic of ['ewp-slow', 'ewp-hanging', 'someone-elses']) {
        await stub({ request: { method, urlPath: `/proxies/accounts-api/toxics/${toxic}` }, response: { status: 204 } });
      }
    }
  };

  /** Every change WireMock received, in order, as "METHOD path body". */
  const changes = async (): Promise<string[]> => {
    // WireMock's journal lists the newest first.
    const found = await (await fetch(`${wireMockUrl()}/__admin/requests`)).json() as { requests: { request: { method: string; url: string; body: string } }[] };
    return found.requests
      .map(r => r.request)
      .filter(r => r.method !== 'GET')
      .reverse()
      .map(r => `${r.method} ${r.url}${r.body ? ` ${r.body}` : ''}`);
  };

  beforeEach(async () => {
    await resetWireMock();
    controller = new ToxiproxyFaultController({ faultProxyAdminUrl: wireMockUrl() } as JourneyOptions);
  });

  it('Given routes open, slow, hanging and disabled, when the states are read, then each is named by what it does', async () => {
    // Given
    await stub({
      request: { method: 'GET', urlPath: '/proxies' },
      response: {
        status: 200, jsonBody: {
          'accounts-api': { name: 'accounts-api', enabled: true, toxics: [] },
          'payments-api': { name: 'payments-api', enabled: true, toxics: [{ name: 'ewp-slow' }] },
          'audit-api': { name: 'audit-api', enabled: true, toxics: [{ name: 'ewp-hanging' }, { name: 'ewp-slow' }] },
          'compliance-api': { name: 'compliance-api', enabled: false, toxics: [{ name: 'ewp-slow' }] },
          'kyc-api': { name: 'kyc-api', enabled: true, toxics: [{ name: 'someone-elses' }] },
        },
      },
    });

    // When
    const states = await controller.states();

    // Then: Cut wins over any toxic, Hanging over Slow; another tool's toxic is not ours.
    expect(Object.fromEntries(states)).toEqual({
      'accounts-api': 'Open', 'payments-api': 'Slow', 'audit-api': 'Hanging', 'compliance-api': 'Cut', 'kyc-api': 'Open',
    });
  });

  it('Given an open route, when it is made Slow, then only our latency toxic is added (5 s on the answers)', async () => {
    // Given
    await proxyIs('accounts-api', true, []);
    await acceptsEverything();

    // When
    await controller.set('accounts-api', 'Slow');

    // Then
    const [added] = await received('POST', '/proxies/accounts-api/toxics');
    expect(JSON.parse(added.body)).toEqual({ name: 'ewp-slow', type: 'latency', stream: 'downstream', toxicity: 1, attributes: { latency: 5000, jitter: 0 } });
    expect(await changes()).toHaveLength(1);
  });

  it('Given a slow route, when it is Cut, then our latency toxic is removed and the route disabled', async () => {
    // Given
    await proxyIs('accounts-api', true, ['ewp-slow']);
    await acceptsEverything();

    // When
    await controller.set('accounts-api', 'Cut');

    // Then
    expect(await changes()).toEqual([
      'DELETE /proxies/accounts-api/toxics/ewp-slow',
      'POST /proxies/accounts-api {"enabled":false}',
    ]);
  });

  it('Given a cut, hanging route with someone else\'s toxic, when it is opened, then ours go, the route is enabled - and theirs stays', async () => {
    // Given
    await proxyIs('accounts-api', false, ['ewp-hanging', 'someone-elses']);
    await acceptsEverything();

    // When
    await controller.set('accounts-api', 'Open');

    // Then
    expect(await changes()).toEqual([
      'DELETE /proxies/accounts-api/toxics/ewp-hanging',
      'POST /proxies/accounts-api {"enabled":true}',
    ]);
  });

  it('Given a route already in the chosen state, when it is set again, then nothing changes', async () => {
    // Given
    await proxyIs('accounts-api', true, ['ewp-hanging']);
    await acceptsEverything();

    // When
    await controller.set('accounts-api', 'Hanging');

    // Then
    expect(await changes()).toEqual([]);
  });

  it('Given every route reopened at start-up, when reopenAll runs, then Toxiproxy\'s own reset is called', async () => {
    // Given
    await stub({ request: { method: 'POST', urlPath: '/reset' }, response: { status: 204 } });

    // When
    await controller.reopenAll();

    // Then
    expect(await received('POST', '/reset')).toHaveLength(1);
  });

  it('Given the proxy refuses (404 unknown route), when a route is set, then the operator is told - with the HTTP status only', async () => {
    // Given
    await stub({ request: { method: 'GET', urlPath: '/proxies/nobody' }, response: { status: 404, body: 'proxy not found' } });

    // When
    const set = controller.set('nobody', 'Cut');

    // Then
    await expect(set).rejects.toBeInstanceOf(ServiceUnavailableException);
    await expect(set).rejects.toThrow('The fault proxy refused the change (HTTP 404).');
  });

  it('Given the proxy is not running, when the states are read, then the operator is told how to start it', async () => {
    // Given
    controller = new ToxiproxyFaultController({ faultProxyAdminUrl: 'http://127.0.0.1:9' } as JourneyOptions);

    // When / Then
    await expect(controller.states()).rejects.toThrow('The fault proxy does not answer. Is it running (.\\ps\\toxiproxy\\Start-Toxiproxy.ps1)?');
  });

  it('Given the admin API redirects, when a route is changed, then the redirect is not followed', async () => {
    // Given
    await stub({ request: { method: 'GET', urlPath: '/proxies/accounts-api' }, response: { status: 302, headers: { Location: `${wireMockUrl()}/elsewhere` } } });
    await stub({ request: { method: 'GET', urlPath: '/elsewhere' }, response: { status: 200, jsonBody: { name: 'accounts-api', enabled: true, toxics: [] } } });

    // When / Then
    await expect(controller.set('accounts-api', 'Cut')).rejects.toBeInstanceOf(ServiceUnavailableException);
    expect(await received('GET', '/elsewhere')).toHaveLength(0);
  });
});