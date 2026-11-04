// ┌─ :: SECURITY :: Client keys for private_key_jwt (staff web apps, customers, workers) ─────────
// │ Why : FAPI 2.0 clients authenticate with an asymmetric key, not a shared secret: the private
// │       key stays with the client, the IDP registers only the public one.
// │ How : One EC P-256 key pair per client (ES256). The private key is written as PKCS#8 PEM,
// │       encrypted (AES-256) with a random password kept next to it - .NET and Node both read PEM,
// │       so one shape serves every client. The public key is written as a JWK with its RFC 7638
// │       thumbprint as key ID.
// │       Existing keys are kept unless --force is given (a new key needs an IDP restart).
// │       DPoP keys (RFC 9449) are separate pairs, one per app or worker that holds tokens: same
// │       PEM shape, no public file - the IDP never registers them, each token names its key
// │       (cnf.jkt).
// │ Ref : Features 2.1.1, 2.1.6 | FAPI 2.0 Security Profile §5.3.1 | RFC 7523, 7517, 7638, 9449 |
// │       Threat-Model R-10 (keys in files: development only)
// └────────────────────────────────────────────────────────────────────────────────────────────────
// DEVELOPMENT ONLY. Called by New-EwpLocalPki.ps1:  node New-EwpClientKeys.mjs <folder> [--force]
import { createHash, generateKeyPairSync, randomBytes } from 'node:crypto';
import { existsSync, mkdirSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';

const clients = [
  // The staff web apps (the Shell and every workspace's BFF).
  'shell-web-client', 'customer-onboarding-web-client', 'customer-kyc-web-client', 'compliance-web-client',
  'accounts-web-client', 'payments-web-client', 'audit-web-client', 'operations-web-client',
  'partner-products-web-client',
  // The customers' channel (My EWP).
  'self-service-web-client', 'self-service-journey-api-client',
  // The Kafka subscribers' machine identities (client credentials).
  'kyc-case-opening-subscriber-client',
  'onboarding-outcome-subscriber-client',
  'compliance-case-opening-subscriber-client',
  'document-invalidation-subscriber-client',
  'account-application-opening-subscriber-client',
  'notifications-subscriber-client',
  'accounts-command-subscriber-client',
  'payments-saga-reply-subscriber-client',
];

// ┌─ SEC ──────────────────────────────────────────────────────────────────────────────────────────
// │ DPoP keys: the staff web apps, the Journey APIs that exchange tokens and the Kafka
// │ subscribers (their machine tokens). A key of its own, not the client key: proving "I hold
// │ this token" and "I am this client" stay separate, and one can be replaced without the other.
// └────────────────────────────────────────────────────────────────────────────────────────────────
const dpopKeys = [
  'shell-web-dpop', 'customer-onboarding-web-dpop', 'customer-kyc-web-dpop', 'compliance-web-dpop',
  'accounts-web-dpop', 'payments-web-dpop', 'audit-web-dpop', 'operations-web-dpop',
  'partner-products-web-dpop',
  'audit-journey-api-dpop', 'operations-journey-api-dpop', 'partner-products-journey-api-dpop',
  'kyc-case-opening-subscriber-dpop',
  'onboarding-outcome-subscriber-dpop',
  'compliance-case-opening-subscriber-dpop',
  'document-invalidation-subscriber-dpop',
  'account-application-opening-subscriber-dpop',
  'notifications-subscriber-dpop',
  'accounts-command-subscriber-dpop',
  'payments-saga-reply-subscriber-dpop',
];

const [folder, ...flags] = process.argv.slice(2);
if (!folder) {
  console.error('Usage: node New-EwpClientKeys.mjs <output folder> [--force]');
  process.exit(2);
}
const force = flags.includes('--force');
mkdirSync(folder, { recursive: true });

for (const name of clients) {
  const privatePath = join(folder, `${name}.key.pem`);
  const passwordPath = join(folder, `${name}.key.pem.password`);
  const publicPath = join(folder, `${name}.public.jwk`);

  if (!force && existsSync(privatePath) && existsSync(passwordPath) && existsSync(publicPath)) {
    console.log(`  kept    ${name} (use -Force to issue a new key)`);
    continue;
  }

  const password = randomBytes(32).toString('base64');
  const { publicKey, privateKey } = generateKeyPairSync('ec', { namedCurve: 'P-256' });

  const jwk = publicKey.export({ format: 'jwk' });
  // RFC 7638: the thumbprint over the required members, in lexical order, without spaces.
  const canonical = JSON.stringify({ crv: jwk.crv, kty: jwk.kty, x: jwk.x, y: jwk.y });
  const kid = createHash('sha256').update(canonical).digest('base64url');

  writeFileSync(privatePath, privateKey.export({ type: 'pkcs8', format: 'pem', cipher: 'aes-256-cbc', passphrase: password }));
  writeFileSync(passwordPath, password);
  writeFileSync(publicPath, JSON.stringify({ kty: 'EC', crv: 'P-256', x: jwk.x, y: jwk.y, kid, use: 'sig', alg: 'ES256' }, null, 2));
  console.log(`  created ${name} (kid ${kid})`);
}

for (const name of dpopKeys) {
  const privatePath = join(folder, `${name}.key.pem`);
  const passwordPath = join(folder, `${name}.key.pem.password`);

  if (!force && existsSync(privatePath) && existsSync(passwordPath)) {
    console.log(`  kept    ${name} (use -Force to issue a new key)`);
    continue;
  }

  const password = randomBytes(32).toString('base64');
  const { privateKey } = generateKeyPairSync('ec', { namedCurve: 'P-256' });
  writeFileSync(privatePath, privateKey.export({ type: 'pkcs8', format: 'pem', cipher: 'aes-256-cbc', passphrase: password }));
  writeFileSync(passwordPath, password);
  console.log(`  created ${name}`);
}