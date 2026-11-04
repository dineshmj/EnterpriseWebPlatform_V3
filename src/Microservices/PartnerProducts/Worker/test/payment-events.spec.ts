import { WorkflowNotFoundError, type Client } from '@temporalio/client';
import type { WorkerOptions } from '../src/config';
import type { MachineToken } from '../src/machine-token';
import { PaymentEventListener } from '../src/payment-events';
import { settledPaymentOf } from '../src/payment-outcome';

// NOTE: the Kafka client is never started here; mocked so its native binding (not built with --ignore-scripts) is not loaded.
jest.mock('@confluentinc/kafka-javascript', () => ({ KafkaJS: {} }));

// ┌─ :: RESILIENCE + SECURITY :: The payment-event listener, Kafka and Temporal played by stand-ins ─
// │ Why : An event only WAKES a purchase's workflow; anything that is not a final outcome for the
// │       Partner Bank's settlement account must be ignored, and a workflow that already ended is
// │       not an error.
// │ How : The REAL listener's handling of one message (the private handle, as the Kafka loop calls
// │       it): the purchase is found through a stand-in fetch (the internal endpoint, with the
// │       worker's DPoP authorization), the workflow signalled through a stand-in Temporal client.
// │       The fields the listener reads are fixed by its Pact contract (test/contracts).
// │ Ref : src/payment-events.ts, src/payment-outcome.ts | Threat-Model R-16
// └────────────────────────────────────────────────────────────────────────────────────────────────
describe('PaymentEventListener - one payment event', () => {
  const SETTLEMENT = { bsb: '033-000', accountNumber: '10203040' };
  const PAYMENT_REF = '0199f2a4-6c1e-7b3a-9d42-5e8f1a2b3c4d';
  const options = {
    settlementBsb: SETTLEMENT.bsb,
    settlementAccountNumber: SETTLEMENT.accountNumber,
    partnerProductsApiBaseUrl: 'https://partner-products.ewp.example',
  } as WorkerOptions;

  const event = (eventType: string, overrides: Record<string, string> = {}) => JSON.stringify({
    EventType: eventType,
    Payload: { PaymentRef: PAYMENT_REF, PaymentNumber: 'PAY-261015-TEST01', ToBsb: SETTLEMENT.bsb, ToAccountNumber: SETTLEMENT.accountNumber, ...overrides },
  });

  let signal: jest.Mock;
  let getHandle: jest.Mock;
  let fetchMock: jest.SpyInstance;
  let listener: PaymentEventListener;
  let handle: (client: Client, raw: string | undefined) => Promise<void>;

  beforeEach(() => {
    signal = jest.fn().mockResolvedValue(undefined);
    getHandle = jest.fn(() => ({ signal }));
    fetchMock = jest.spyOn(globalThis, 'fetch');
    jest.spyOn(console, 'log').mockImplementation(() => undefined);
    const tokens = { authorization: async () => ({ Authorization: 'DPoP t', DPoP: 'p' }), forget: jest.fn() } as unknown as MachineToken;
    listener = new PaymentEventListener(options, tokens);
    handle = (client, raw) => (listener as unknown as { handle: typeof handle }).handle(client, raw);
  });

  afterEach(() => jest.restoreAllMocks());

  const client = () => ({ workflow: { getHandle } }) as unknown as Client;
  const purchaseFound = () => fetchMock.mockResolvedValue(new Response(JSON.stringify({ number: 'DGP-261015-0001' }), { status: 200 }));

  it('Given a completed payment to the settlement account, when it arrives, then the purchase is looked up by the payment key and its workflow told to look again', async () => {
    purchaseFound();

    await handle(client(), event('PaymentCompleted'));

    expect(fetchMock).toHaveBeenCalledTimes(1);
    expect(String(fetchMock.mock.calls[0][0])).toBe(`${options.partnerProductsApiBaseUrl}/internal/v1/gold-purchases/by-payment-ref/${PAYMENT_REF}`);
    expect(fetchMock.mock.calls[0][1].headers).toMatchObject({ Authorization: 'DPoP t', DPoP: 'p' });
    expect(getHandle).toHaveBeenCalledWith('gold-purchase-DGP-261015-0001');
    expect(signal).toHaveBeenCalledTimes(1);
  });

  it('Given the workflow has already ended, when the event arrives, then nothing fails', async () => {
    purchaseFound();
    signal.mockRejectedValue(new WorkflowNotFoundError('not found', 'gold-purchase-DGP-261015-0001', undefined));

    await expect(handle(client(), event('PaymentFailed'))).resolves.toBeUndefined();
  });

  it('Given a payment no purchase started, when the event arrives, then no workflow is signalled', async () => {
    fetchMock.mockResolvedValue(new Response('', { status: 404 }));

    await handle(client(), event('PaymentCompleted'));

    expect(getHandle).not.toHaveBeenCalled();
  });

  it('Given any other event, when it arrives, then nothing is looked up', async () => {
    await handle(client(), event('PaymentApprovalRequired'));
    await handle(client(), event('PaymentCompleted', { ToAccountNumber: '99999999' }));
    await handle(client(), 'not json');
    await handle(client(), undefined);

    expect(fetchMock).not.toHaveBeenCalled();
  });

  it.each([
    ['not JSON', 'not json'],
    ['no message', undefined],
    ['not a final outcome', event('PaymentApprovalRequired')],
    ['another payee BSB', event('PaymentCompleted', { ToBsb: '062-000' })],
    ['another payee account', event('PaymentCompleted', { ToAccountNumber: '99999999' })],
    ['a PaymentRef that is not a GUID', event('PaymentCompleted', { PaymentRef: '../../admin' })],
  ])('settledPaymentOf: %s is not passed on', (_case, raw) => {
    expect(settledPaymentOf(raw, SETTLEMENT)).toBeNull();
  });
});