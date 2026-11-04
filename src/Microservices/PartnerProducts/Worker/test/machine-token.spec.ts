import { createHash, createPublicKey, generateKeyPairSync, verify, type KeyObject } from 'node:crypto';
import { mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import type { WorkerOptions } from '../src/config';
import { MachineToken } from '../src/machine-token';

// ┌─ :: ARCHITECTURE + SECURITY :: The worker's machine identity, the IDP played by a stand-in fetch ─
// │ Why : Every call the workflow makes rests on these decisions: no secret, a one-time assertion
// │       for the IDP's issuer only, a token bound to the DPoP key (an unbound one refused), a
// │       fresh proof for every call, a DPoP nonce answered once and never in a loop.
// │ How : The REAL MachineToken with real encrypted key files (written to a temporary folder); the
// │       IDP is a recorded stand-in for fetch - not WireMock, because the worker accepts only an
// │       https token endpoint and the shared WireMock listens on plain http. Each test stages the
// │       IDP's answers, then checks the headers, the error, and every request the worker sent,
// │       signatures included.
// │ Ref : src/machine-token.ts | Features 2.1.2 | RFC 7523, 7638, 9449 | Threat-Model R-9
// └────────────────────────────────────────────────────────────────────────────────────────────────
describe('MachineToken - the IDP played by a stand-in fetch', () => {
  const CLIENT_ID = 'PartnerProducts.Worker.ClientID';
  const AUTHORITY = 'https://idp.ewp.example';
  const ISSUER = 'https://idp.ewp.example';
  const TOKEN_ENDPOINT = 'https://idp.ewp.example/connect/token';
  const DISCOVERY = `${AUTHORITY}/.well-known/openid-configuration`;
  const PAYMENTS_URL = 'https://payments.ewp.example/v1/payments/internal/purchases/DGP-1?expand=status';

  interface Sent { url: string; init: RequestInit; form: URLSearchParams }
  let sent: Sent[];
  let discoveryAnswers: Response[];
  let tokenAnswers: Response[];
  let folder: string;
  let clientKey: { privateKey: KeyObject; publicKey: KeyObject };
  let dpopKey: { privateKey: KeyObject; publicKey: KeyObject };
  let options: WorkerOptions;

  const json = (status: number, body: unknown, headers: Record<string, string> = {}) =>
    new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json', ...headers } });
  const discoveryNames = (tokenEndpoint: string) => json(200, { issuer: ISSUER, token_endpoint: tokenEndpoint });
  const issued = (accessToken: string, expiresIn = 300) => json(200, { access_token: accessToken, token_type: 'DPoP', expires_in: expiresIn });

  /** Writes a key as an encrypted PKCS#8 PEM with its password in a file of its own, as New-EwpClientKeys.mjs does. */
  const keyFiles = (name: string, key: KeyObject) => {
    const password = `${name}-password`;
    writeFileSync(join(folder, `${name}.pem`), key.export({ type: 'pkcs8', format: 'pem', cipher: 'aes-256-cbc', passphrase: password }));
    writeFileSync(join(folder, `${name}.password`), `${password}\n`);
    return { path: join(folder, `${name}.pem`), passwordPath: join(folder, `${name}.password`) };
  };

  const withKeys = (client: KeyObject, dpop: KeyObject): WorkerOptions => {
    const c = keyFiles('client', client);
    const d = keyFiles('dpop', dpop);
    return { authority: AUTHORITY, clientId: CLIENT_ID, clientKeyPath: c.path, clientKeyPasswordPath: c.passwordPath, dpopKeyPath: d.path, dpopKeyPasswordPath: d.passwordPath } as WorkerOptions;
  };

  /** The JWS's header and payload, after checking its ES256 signature with this public key. */
  const verified = (jws: string, publicKey: KeyObject) => {
    const [header, payload, signature] = jws.split('.');
    const valid = verify('sha256', Buffer.from(`${header}.${payload}`), { key: publicKey, dsaEncoding: 'ieee-p1363' }, Buffer.from(signature, 'base64url'));
    expect(valid).toBe(true);
    const decode = (part: string) => JSON.parse(Buffer.from(part, 'base64url').toString()) as Record<string, unknown>;
    return { header: decode(header), payload: decode(payload) };
  };

  const thumbprintOf = (key: KeyObject) => {
    const { crv, kty, x, y } = createPublicKey(key).export({ format: 'jwk' });
    return createHash('sha256').update(JSON.stringify({ crv, kty, x, y })).digest('base64url');
  };

  const tokenRequests = () => sent.filter((s) => s.url === TOKEN_ENDPOINT);

  /** The error a call fails with (a call that succeeds fails the test). */
  const failureOf = (call: Promise<unknown>): Promise<Error> =>
    call.then(() => { throw new Error('The call was expected to fail.'); }, (error: Error) => error);

  beforeEach(() => {
    folder = mkdtempSync(join(tmpdir(), 'ewp-worker-keys-'));
    clientKey = generateKeyPairSync('ec', { namedCurve: 'P-256' });
    dpopKey = generateKeyPairSync('ec', { namedCurve: 'P-256' });
    options = withKeys(clientKey.privateKey, dpopKey.privateKey);
    sent = [];
    discoveryAnswers = [];
    tokenAnswers = [];
    jest.spyOn(globalThis, 'fetch').mockImplementation(async (input, init) => {
      const url = String(input);
      sent.push({ url, init: init ?? {}, form: new URLSearchParams(init?.body ? String(init.body) : '') });
      const answers = url === DISCOVERY ? discoveryAnswers : url === TOKEN_ENDPOINT ? tokenAnswers : [];
      return answers.shift() ?? new Response(null, { status: 404 });
    });
  });

  afterEach(() => {
    jest.restoreAllMocks();
    rmSync(folder, { recursive: true, force: true });
  });

  it('Given both keys, when a call is prepared, then the token is asked for with a one-time assertion and a proof - never a secret', async () => {
    // Given
    discoveryAnswers.push(discoveryNames(TOKEN_ENDPOINT));
    tokenAnswers.push(issued('bound-token'));

    // When
    await new MachineToken(options).authorization('payments.read', 'GET', PAYMENTS_URL);

    // Then: client credentials for exactly this scope ...
    const [request] = tokenRequests();
    expect(request.init.method).toBe('POST');
    expect(request.form.get('grant_type')).toBe('client_credentials');
    expect(request.form.get('client_id')).toBe(CLIENT_ID);
    expect(request.form.get('scope')).toBe('payments.read');
    expect(request.form.get('client_secret')).toBeNull();

    // ... proved by an assertion signed with the client key, for the IDP's issuer only, for 60 s ...
    expect(request.form.get('client_assertion_type')).toBe('urn:ietf:params:oauth:client-assertion-type:jwt-bearer');
    const assertion = verified(request.form.get('client_assertion')!, clientKey.publicKey);
    expect(assertion.header).toMatchObject({ alg: 'ES256', typ: 'client-authentication+jwt', kid: thumbprintOf(clientKey.privateKey) });
    expect(assertion.payload).toMatchObject({ iss: CLIENT_ID, sub: CLIENT_ID, aud: ISSUER });
    expect((assertion.payload.exp as number) - (assertion.payload.iat as number)).toBe(60);
    expect(assertion.payload.jti).toEqual(expect.any(String));

    // ... with a proof by the DPoP key for the token endpoint (no token yet, so no ath) ...
    const proof = verified((request.init.headers as Record<string, string>).DPoP, dpopKey.publicKey);
    expect(proof.header).toMatchObject({ typ: 'dpop+jwt', alg: 'ES256' });
    expect(proof.header.jwk).not.toHaveProperty('d');   // the public key only
    expect(proof.payload).toMatchObject({ htm: 'POST', htu: TOKEN_ENDPOINT });
    expect(proof.payload).not.toHaveProperty('ath');

    // ... and no redirect is ever followed with it.
    expect(sent.every((s) => s.init.redirect === 'error')).toBe(true);
  });

  it('Given a bound token, when a call is prepared, then it goes with the DPoP scheme and a proof for exactly that method and address', async () => {
    // Given
    discoveryAnswers.push(discoveryNames(TOKEN_ENDPOINT));
    tokenAnswers.push(issued('bound-token'));

    // When
    const headers = await new MachineToken(options).authorization('payments.read', 'get', PAYMENTS_URL);

    // Then: the proof names the method and the address without its query, and the token's hash.
    expect(headers.Authorization).toBe('DPoP bound-token');
    const proof = verified(headers.DPoP, dpopKey.publicKey);
    expect(proof.payload).toMatchObject({
      htm: 'GET',
      htu: 'https://payments.ewp.example/v1/payments/internal/purchases/DGP-1',
      ath: createHash('sha256').update('bound-token').digest('base64url'),
    });
  });

  it('Given a token already issued, when the same scope is used again and then another, then the IDP is asked once per scope and every call has a new proof', async () => {
    // Given
    discoveryAnswers.push(discoveryNames(TOKEN_ENDPOINT));
    tokenAnswers.push(issued('for-payments'), issued('for-partner-products'));
    const tokens = new MachineToken(options);

    // When
    const first = await tokens.authorization('payments.read', 'GET', PAYMENTS_URL);
    const second = await tokens.authorization('payments.read', 'GET', PAYMENTS_URL);
    const other = await tokens.authorization('partner-products.write', 'POST', 'https://partner-products.ewp.example/v1/partner-products/internal/advice');

    // Then: a token for one API is never sent to the other ...
    expect(tokenRequests().map((r) => r.form.get('scope'))).toEqual(['payments.read', 'partner-products.write']);
    expect(sent.filter((s) => s.url === DISCOVERY)).toHaveLength(1);
    expect([first.Authorization, second.Authorization, other.Authorization]).toEqual(['DPoP for-payments', 'DPoP for-payments', 'DPoP for-partner-products']);

    // ... and a proof is never used twice (a new jti each time).
    const jtis = [first, second, other].map((h) => verified(h.DPoP, dpopKey.publicKey).payload.jti);
    expect(new Set(jtis).size).toBe(3);
  });

  it('Given a token that was dropped after a 401, when the scope is used again, then a new token is asked for', async () => {
    // Given
    discoveryAnswers.push(discoveryNames(TOKEN_ENDPOINT));
    tokenAnswers.push(issued('first'), issued('second'));
    const tokens = new MachineToken(options);
    await tokens.authorization('payments.read', 'GET', PAYMENTS_URL);

    // When
    tokens.forget('payments.read');
    const headers = await tokens.authorization('payments.read', 'GET', PAYMENTS_URL);

    // Then
    expect(headers.Authorization).toBe('DPoP second');
    expect(tokenRequests()).toHaveLength(2);
  });

  it('Given a token that expires within a minute, when the scope is used again, then it is not reused', async () => {
    // Given
    discoveryAnswers.push(discoveryNames(TOKEN_ENDPOINT));
    tokenAnswers.push(issued('short-lived', 30), issued('fresh'));
    const tokens = new MachineToken(options);
    await tokens.authorization('payments.read', 'GET', PAYMENTS_URL);

    // When
    const headers = await tokens.authorization('payments.read', 'GET', PAYMENTS_URL);

    // Then
    expect(headers.Authorization).toBe('DPoP fresh');
  });

  it('Given the IDP asks for a DPoP nonce, when a token is asked for, then the request is sent once more with the nonce, a new assertion and a new proof', async () => {
    // Given
    discoveryAnswers.push(discoveryNames(TOKEN_ENDPOINT));
    tokenAnswers.push(json(400, { error: 'use_dpop_nonce' }, { 'DPoP-Nonce': 'server-nonce' }), issued('bound-token'));

    // When
    const headers = await new MachineToken(options).authorization('payments.read', 'GET', PAYMENTS_URL);

    // Then
    expect(headers.Authorization).toBe('DPoP bound-token');
    const [first, retry] = tokenRequests();
    const proofOf = (r: Sent) => verified((r.init.headers as Record<string, string>).DPoP, dpopKey.publicKey).payload;
    expect(proofOf(first)).not.toHaveProperty('nonce');
    expect(proofOf(retry).nonce).toBe('server-nonce');
    expect(proofOf(retry).jti).not.toBe(proofOf(first).jti);
    const jtiOf = (r: Sent) => verified(r.form.get('client_assertion')!, clientKey.publicKey).payload.jti;
    expect(jtiOf(retry)).not.toBe(jtiOf(first));   // the IDP takes each assertion once
  });

  it('Given the IDP asks for a nonce again after the retry, when a token is asked for, then it gives up after two requests - never a loop', async () => {
    // Given
    discoveryAnswers.push(discoveryNames(TOKEN_ENDPOINT));
    const nonceAgain = () => json(400, { error: 'use_dpop_nonce' }, { 'DPoP-Nonce': 'another-nonce' });
    tokenAnswers.push(nonceAgain(), nonceAgain(), nonceAgain());

    // When
    const error = await failureOf(new MachineToken(options).authorization('payments.read', 'GET', PAYMENTS_URL));

    // Then
    expect(error.message).toBe("The IDP refused the worker's token for payments.read: HTTP 400 use_dpop_nonce");
    expect(tokenRequests()).toHaveLength(2);
  });

  it('Given the IDP issues a Bearer token, when a call is prepared, then the token is refused and never used', async () => {
    // Given: the client is not set up for DPoP at the IDP.
    discoveryAnswers.push(discoveryNames(TOKEN_ENDPOINT));
    tokenAnswers.push(json(200, { access_token: 'unbound-token', token_type: 'Bearer', expires_in: 300 }));

    // When
    const error = await failureOf(new MachineToken(options).authorization('payments.read', 'GET', PAYMENTS_URL));

    // Then
    expect(error.message).toBe("The IDP issued a 'Bearer' token for payments.read; this worker uses DPoP-bound tokens only.");
  });

  it('Given the IDP refuses the client, when a call is prepared, then the activity fails with the IDP\'s reason (Temporal retries it)', async () => {
    // Given
    discoveryAnswers.push(discoveryNames(TOKEN_ENDPOINT));
    tokenAnswers.push(json(400, { error: 'invalid_client' }));

    // When
    const error = await failureOf(new MachineToken(options).authorization('payments.read', 'GET', PAYMENTS_URL));

    // Then
    expect(error.message).toBe("The IDP refused the worker's token for payments.read: HTTP 400 invalid_client");
  });

  it('Given discovery names a plain-http token endpoint, when a call is prepared, then nothing is sent to it', async () => {
    // Given
    discoveryAnswers.push(discoveryNames('http://idp.ewp.example/connect/token'));

    // When
    const error = await failureOf(new MachineToken(options).authorization('payments.read', 'GET', PAYMENTS_URL));

    // Then
    expect(error.message).toBe('IDP discovery named no issuer or https token endpoint (HTTP 200).');
    expect(sent.map((s) => s.url)).toEqual([DISCOVERY]);
  });

  it('Given discovery failed once, when the next call is prepared, then the IDP is asked again - a failure is not kept', async () => {
    // Given
    discoveryAnswers.push(new Response(null, { status: 503 }), discoveryNames(TOKEN_ENDPOINT));
    tokenAnswers.push(issued('bound-token'));
    const tokens = new MachineToken(options);
    await failureOf(tokens.authorization('payments.read', 'GET', PAYMENTS_URL));

    // When
    const headers = await tokens.authorization('payments.read', 'GET', PAYMENTS_URL);

    // Then
    expect(headers.Authorization).toBe('DPoP bound-token');
    expect(sent.filter((s) => s.url === DISCOVERY)).toHaveLength(2);
  });

  it('Given the same key as client key and DPoP key, when the worker starts, then it refuses to', () => {
    // Given
    const same = withKeys(clientKey.privateKey, clientKey.privateKey);

    // When / Then
    expect(() => new MachineToken(same)).toThrow('The DPoP key must not be the client key.');
  });

  it('Given a client key on another curve, when the worker starts, then it refuses to', () => {
    // Given
    const p384 = withKeys(generateKeyPairSync('ec', { namedCurve: 'P-384' }).privateKey, dpopKey.privateKey);

    // When / Then
    expect(() => new MachineToken(p384)).toThrow("The worker's client key must be an EC P-256 key (ES256).");
  });

  it('Given a wrong password for the DPoP key, when the worker starts, then it refuses to', () => {
    // Given
    writeFileSync(options.dpopKeyPasswordPath, 'not-the-password');

    // When / Then
    expect(() => new MachineToken(options)).toThrow();
  });
});