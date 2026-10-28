// ┌─ :: SECURITY :: Every secret of a cloud demo: made once, kept in Key Vault only ───────────────
// │ Why : 35 EWP services, Kafka, PostgreSQL and the Partner Bank need about 80 secrets. None may
// │       be in the repository, GitHub or the Terraform state, and none may outlive the demo.
// │ How : Base values (random passwords and keys, a throw-away PKI) are made here the first time
// │       and written to the demo's Key Vaults; a re-run of Demo up reads them back instead, so
// │       every role, Kafka user and client keeps its secret. Values built from others (a
// │       connection string) are written as their own Key Vault secret. Then, per namespace, the
// │       list of Kubernetes Secrets and which Key Vault secret fills each key is written for
// │       deploy/helm/keyvault-secrets - the only path from Key Vault into the cluster.
// │       Two vaults: EWP's (namespaces ewp, ewp-infra, observability) and the Partner Bank's (partner-bank). Of
// │       the PKI, the Partner Bank gets only its server certificate and the PUBLIC certificates
// │       of EWP's root and request-signing key; the root's private key is never stored at all.
// │       Values are never printed. Key Vault is called over its REST API with the Azure CLI's
// │       token (the workflow's identity, Key Vault Secrets Officer on both vaults).
// │ Ref : doc/Cloud-Demo.md CD14-CD15 | doc/Kubernetes.md (Secrets an environment creates) |
// │       ps/pki/New-EwpLocalPki.ps1 (the laptop's PKI, same shapes) | OWASP ASVS V13.3, V11.4
// └────────────────────────────────────────────────────────────────────────────────────────────────
// Usage:
//   node demo-secrets.mjs --list     every namespace's Secret keys, one "<namespace> <secret> <key>"
//                                    per line (no Azure; check-charts.sh compares it with the charts)
//   node demo-secrets.mjs --check-pki  make a PKI and check it (no Azure; CI)
//   node demo-secrets.mjs            fill the vaults and write the lists; needs (env):
//     EWP_KEY_VAULT, PARTNER_KEY_VAULT, TENANT_ID, EWP_SECRETS_CLIENT_ID, PARTNER_SECRETS_CLIENT_ID,
//     PARTNER_DOMAIN, POSTGRES_HOST, OUT_DIR (the lists: keyvault-secrets-<ns>.json,
//     and partner-bank-ca.pem, the public root the Gateway checks the Partner Bank's pods with)
//     Optional: LAUNCHDARKLY_SDK_KEY (GitHub secret) - then also Secret ewp-launchdarkly, and
//     demo-platform.sh switches the chart to LaunchDarkly (both --list and the chart: off without it)
import { createHash, generateKeyPairSync, randomBytes, randomInt } from 'node:crypto';
import { execFileSync } from 'node:child_process';
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

// ── What exists ──────────────────────────────────────────────────────────────────────────────────

// CONTRACT: db/EwpServiceDbUsers.sql (the roles) and ps/database/Initialize-EwpDatabases.ps1.
const dbRoles = [
  'ewp_idp', 'ewp_shell', 'ewp_customer_onboarding_api', 'ewp_customer_outbox_relay', 'ewp_kyc_api',
  'ewp_documents_api', 'ewp_compliance_api', 'ewp_accounts_api', 'ewp_notifications_api', 'ewp_payments_api',
  'ewp_audit_api', 'ewp_partner_products_api', 'ewp_co_bff', 'ewp_compliance_bff', 'ewp_accounts_bff',
  'ewp_payments_bff', 'ewp_kyc_bff', 'ewp_audit_web', 'ewp_operations_web', 'ewp_partner_products_web',
];

// CONTRACT: deploy/helm/ewp-infra values.yaml (kafka.users) = ps/kafka/Setup-KafkaSecurity.ps1.
const kafkaUsers = [
  'ewp-co-outbox-relay', 'ewp-kyc-api', 'ewp-kyc-case-opening-subscriber', 'ewp-onboarding-outcome-subscriber',
  'ewp-compliance-api', 'ewp-compliance-case-opening-subscriber', 'ewp-dm-invalidation-subscriber',
  'ewp-accounts-api', 'ewp-accounts-application-opening-subscriber', 'ewp-accounts-command-subscriber',
  'ewp-payments-api', 'ewp-partner-products-api', 'ewp-partner-products-worker',
  'ewp-payments-saga-reply-subscriber', 'ewp-notifications-subscriber', 'ewp-audit-api',
];

// The IDP's confidential clients (deploy/helm/ewp values.yaml, idp.secretEnv) and who holds each.
const clients = {
  'BSS.Shell.BFF.ClientID': ['shell', 'Oidc__ClientSecret'],
  'CustomerOnboarding.Microservice.BFF.ClientID': ['customer-onboarding-bff-web', 'Oidc__ClientSecret'],
  'CustomerKYC.Microservice.BFF.ClientID': ['customer-kyc-bff-web', 'KYC_BFF_CLIENT_SECRET'],
  'Compliance.Microservice.BFF.ClientID': ['compliance-bff-web', 'Oidc__ClientSecret'],
  'Accounts.Microservice.BFF.ClientID': ['accounts-bff-web', 'Oidc__ClientSecret'],
  'Payments.Microservice.BFF.ClientID': ['payments-bff-web', 'Oidc__ClientSecret'],
  'Audit.Microservice.Web.ClientID': ['audit-bff-web', 'AUDIT_WEB_CLIENT_SECRET'],
  'Audit.JourneyApi.ClientID': ['audit-journey-api', 'AUDIT_JOURNEY_CLIENT_SECRET'],
  'Operations.Microservice.Web.ClientID': ['operations-bff-web', 'OPERATIONS_WEB_CLIENT_SECRET'],
  'Operations.JourneyApi.ClientID': ['operations-journey-api', 'OPERATIONS_JOURNEY_CLIENT_SECRET'],
  'PartnerProducts.Microservice.Web.ClientID': ['partner-products-bff-web', 'PARTNER_PRODUCTS_WEB_CLIENT_SECRET'],
  'PartnerProducts.JourneyApi.ClientID': ['partner-products-journey-api', 'PARTNER_PRODUCTS_JOURNEY_CLIENT_SECRET'],
  'PartnerProducts.Worker.M2M.ClientID': ['partner-products-worker', 'PARTNER_PRODUCTS_WORKER_CLIENT_SECRET'],
  'CustomerKyc.CaseOpeningSubscriber.To.CustomerKycApi.M2M.ClientID': ['kyc-case-opening-subscriber', 'KycCaseOpeningSubscriber__ClientSecret'],
  'CustomerOnboarding.OutcomeSubscriber.To.CustomerOnboardingApi.M2M.ClientID': ['onboarding-outcome-subscriber', 'OnboardingOutcomeSubscriber__ClientSecret'],
  'Compliance.CaseOpeningSubscriber.To.ComplianceApi.M2M.ClientID': ['compliance-case-opening-subscriber', 'ComplianceCaseOpeningSubscriber__ClientSecret'],
  'DocumentsManagement.InvalidationSubscriber.To.DocumentsManagementApi.M2M.ClientID': ['document-invalidation-subscriber', 'DocumentInvalidationSubscriber__ClientSecret'],
  'Accounts.ApplicationOpeningSubscriber.To.AccountsApi.M2M.ClientID': ['account-application-opening-subscriber', 'AccountApplicationOpeningSubscriber__ClientSecret'],
  'Notifications.Subscriber.To.NotificationsApi.M2M.ClientID': ['notifications-subscriber', 'NotificationsSubscriber__ClientSecret'],
  'Accounts.CommandSubscriber.To.AccountsApi.M2M.ClientID': ['accounts-command-subscriber', 'AccountsCommandSubscriber__ClientSecret'],
  'Payments.SagaReplySubscriber.To.PaymentsApi.M2M.ClientID': ['payments-saga-reply-subscriber', 'PaymentsSagaReplySubscriber__ClientSecret'],
};

// Each component's database (key, role, database) and Kafka user (key, user).
const databases = {
  idp: [['ConnectionStrings__IdentityDbConnection', 'ewp_idp', 'EwpIdentityAccessDb']],
  shell: [['ConnectionStrings__MenuDbConnection', 'ewp_shell', 'EwpBssShellDb'], ['ConnectionStrings__BffStateDbConnection', 'ewp_shell', 'EwpBffStateDb']],
  'customer-onboarding-bff-web': [['ConnectionStrings__BffStateDbConnection', 'ewp_co_bff', 'EwpBffStateDb']],
  'compliance-bff-web': [['ConnectionStrings__BffStateDbConnection', 'ewp_compliance_bff', 'EwpBffStateDb']],
  'accounts-bff-web': [['ConnectionStrings__BffStateDbConnection', 'ewp_accounts_bff', 'EwpBffStateDb']],
  'payments-bff-web': [['ConnectionStrings__BffStateDbConnection', 'ewp_payments_bff', 'EwpBffStateDb']],
  'customer-onboarding-api': [['ConnectionStrings__CustomerDbConnection', 'ewp_customer_onboarding_api', 'EwpCustomerDb']],
  'customer-kyc-api': [['ConnectionStrings__KycDbConnection', 'ewp_kyc_api', 'EwpKycDb']],
  'documents-management-api': [['ConnectionStrings__DocumentsManagementDbConnection', 'ewp_documents_api', 'EwpDocumentsManagementDb']],
  'compliance-api': [['ConnectionStrings__ComplianceDbConnection', 'ewp_compliance_api', 'EwpComplianceDb']],
  'accounts-api': [['ConnectionStrings__AccountsDbConnection', 'ewp_accounts_api', 'EwpAccountsDb']],
  'payments-api': [['ConnectionStrings__PaymentsDbConnection', 'ewp_payments_api', 'EwpPaymentsDb']],
  'notifications-api': [['ConnectionStrings__NotificationsDbConnection', 'ewp_notifications_api', 'EwpNotificationsDb']],
  'audit-api': [['ConnectionStrings__AuditDbConnection', 'ewp_audit_api', 'EwpAuditDb']],
  'partner-products-api': [['ConnectionStrings__PartnerProductsDbConnection', 'ewp_partner_products_api', 'EwpPartnerProductsDb']],
  'customer-outbox-publisher': [['ConnectionStrings__CustomerDbConnection', 'ewp_customer_outbox_relay', 'EwpCustomerDb']],
};
// The Node BFFs take a URL (node-postgres) to their sessions database.
const databaseUrls = {
  'customer-kyc-bff-web': ['KYC_BFF_DATABASE_URL', 'ewp_kyc_bff'],
  'audit-bff-web': ['AUDIT_WEB_DATABASE_URL', 'ewp_audit_web'],
  'operations-bff-web': ['OPERATIONS_WEB_DATABASE_URL', 'ewp_operations_web'],
  'partner-products-bff-web': ['PARTNER_PRODUCTS_WEB_DATABASE_URL', 'ewp_partner_products_web'],
};
// SEC: Grafana (deploy/helm/ewp-observability) signs in through the IDP: its client secret is
// the IDP's ClientSecrets__<id> and the observability namespace's Secret ewp-grafana-oidc.
const grafanaClientId = 'Grafana.ClientID';

const kafkaClients = {
  'customer-kyc-api': 'ewp-kyc-api',
  'compliance-api': 'ewp-compliance-api',
  'accounts-api': 'ewp-accounts-api',
  'payments-api': 'ewp-payments-api',
  'audit-api': 'ewp-audit-api',
  'partner-products-api': 'ewp-partner-products-api',
  'customer-outbox-publisher': 'ewp-co-outbox-relay',
  'kyc-case-opening-subscriber': 'ewp-kyc-case-opening-subscriber',
  'onboarding-outcome-subscriber': 'ewp-onboarding-outcome-subscriber',
  'compliance-case-opening-subscriber': 'ewp-compliance-case-opening-subscriber',
  'document-invalidation-subscriber': 'ewp-dm-invalidation-subscriber',
  'account-application-opening-subscriber': 'ewp-accounts-application-opening-subscriber',
  'notifications-subscriber': 'ewp-notifications-subscriber',
  'accounts-command-subscriber': 'ewp-accounts-command-subscriber',
  'payments-saga-reply-subscriber': 'ewp-payments-saga-reply-subscriber',
};

// ── Key Vault object names (letters, digits and dashes only) ──────────────────────────────────────

const kv = (s) => s.replace(/[^A-Za-z0-9-]/g, '-');
const o = {
  dbRole: (role) => `db-role-${kv(role)}`,
  dbAdmin: 'postgres-admin-password',               // made by Terraform (postgres.tf)
  conn: (role, db) => `db-conn-${kv(role)}-${db}`,
  dbUrl: (role) => `db-url-${kv(role)}`,
  kafka: (user) => `kafka-user-${user}`,
  client: (id) => `client-${kv(id)}`,
  apiKey: (name) => `api-key-${name}`,
  simulatorAdminKey: 'simulator-admin-key',
  sessionKey: (name) => `session-key-${name}`,
  kycSessionSecret: 'session-secret-kyc-bff',
  documentKey: 'documents-encryption-key-1',
  anchorKey: 'audit-anchor-signing-key',
  keystorePassword: 'tls-keystore-password',
  dataProtectionPfx: 'pki-data-protection-pfx',
  dataProtectionPassword: 'pki-data-protection-password',
  idpSigningPfx: 'pki-idp-signing-pfx',
  idpSigningPassword: 'pki-idp-signing-password',
  clientKey: (name) => `idp-client-key-${name}`,
  trustRoot: 'pki-trust-root-cer',
  partnerClientPfx: 'pki-ewp-partner-client-pfx',
  partnerClientPassword: 'pki-ewp-partner-client-password',
  requestSigningPfx: 'pki-ewp-request-signing-pfx',
  requestSigningPassword: 'pki-ewp-request-signing-password',
  requestSigningCer: 'pki-ewp-request-signing-cer',
  // The Partner Bank's vault.
  partnerServerPfx: 'pki-partner-bank-server-pfx',
  partnerServerPassword: 'pki-partner-bank-server-password',
  windowClient: 'window-client-secret',
  launchDarklySdkKey: 'launchdarkly-sdk-key',
};

// SEC: the one secret made OUTSIDE this script (the LaunchDarkly environment's SDK key, a GitHub
// secret). Optional: without it the APIs read the chart's flag file.
const launchDarklySdkKey = process.env.LAUNCHDARKLY_SDK_KEY?.trim();

// ── The Secrets per namespace: { secret: { key: [object, encoding?] } } ───────────────────────────

const b64 = 'base64';
function ewpNamespace() {
  const s = {};
  const add = (secret, key, object, encoding) => { (s[secret] ??= {})[key] = [object, encoding]; };
  for (const [component, entries] of Object.entries(databases))
    for (const [key, role, db] of entries) add(`ewp-${component}`, key, o.conn(role, db));
  for (const [component, [key, role]] of Object.entries(databaseUrls)) add(`ewp-${component}`, key, o.dbUrl(role));
  for (const [component, user] of Object.entries(kafkaClients)) add(`ewp-${component}`, 'Kafka__SaslPassword', o.kafka(user));
  add('ewp-partner-products-worker', 'PARTNER_PRODUCTS_WORKER_KAFKA_PASSWORD', o.kafka('ewp-partner-products-worker'));
  for (const [id, [component, key]] of Object.entries(clients)) {
    add('ewp-idp', `ClientSecrets__${id}`, o.client(id));
    add(`ewp-${component}`, key, o.client(id));
  }
  add('ewp-idp', `ClientSecrets__${grafanaClientId}`, o.client(grafanaClientId));
  add('ewp-idp', 'SigningCredential__CertificatePassword', o.idpSigningPassword);
  add('ewp-customer-kyc-bff-web', 'KYC_BFF_SESSION_SECRET', o.kycSessionSecret);
  add('ewp-customer-kyc-bff-web', 'KYC_BFF_SESSION_KEY', o.sessionKey('kyc-bff'));
  add('ewp-audit-bff-web', 'AUDIT_WEB_SESSION_KEY', o.sessionKey('audit-web'));
  add('ewp-operations-bff-web', 'OPERATIONS_WEB_SESSION_KEY', o.sessionKey('operations-web'));
  add('ewp-partner-products-bff-web', 'PARTNER_PRODUCTS_WEB_SESSION_KEY', o.sessionKey('partner-products-web'));
  add('ewp-documents-management-api', 'DocumentStorage__Encryption__Keys__key-1', o.documentKey);
  add('ewp-audit-api', 'Audit__Anchors__SigningKey', o.anchorKey);
  // A simulator's key equals its caller's.
  for (const [caller, key, simulator, name] of [
    ['compliance-api', 'ScreeningProvider__ApiKey', 'screening-provider-simulator', 'screening-provider'],
    ['accounts-api', 'CoreBanking__ApiKey', 'core-banking-simulator', 'core-banking'],
    ['payments-api', 'PaymentNetwork__ApiKey', 'payment-network-simulator', 'payment-network'],
  ]) {
    add(`ewp-${caller}`, key, o.apiKey(name));
    add(`ewp-${simulator}`, 'Simulator__ApiKey', o.apiKey(name));
    // SEC: the behaviour switch from the Operations Journey API (another pod): one admin key,
    // known only to it and the simulators (Test environment only - the chart's simulators).
    add(`ewp-${simulator}`, 'Simulator__AdminKey', o.simulatorAdminKey);
  }
  add('ewp-operations-journey-api', 'OPERATIONS_JOURNEY_SIMULATOR_ADMIN_KEY', o.simulatorAdminKey);
  if (launchDarklySdkKey) add('ewp-launchdarkly', 'sdk-key', o.launchDarklySdkKey);
  add('ewp-data-protection', 'certificate.pfx', o.dataProtectionPfx, b64);
  add('ewp-data-protection', 'password', o.dataProtectionPassword);
  add('ewp-tls-keystore-password', 'password', o.keystorePassword);
  add('ewp-idp-signing', 'signing.pfx', o.idpSigningPfx, b64);
  add('ewp-idp-client-keys', 'SelfService.Web.ClientID.jwk', o.clientKey('selfservice-web'));
  add('ewp-idp-client-keys', 'SelfService.JourneyApi.ClientID.jwk', o.clientKey('selfservice-journeyapi'));
  add('ewp-partner-pki', 'ewp-partner-client.pfx', o.partnerClientPfx, b64);
  add('ewp-partner-pki', 'ewp-partner-client.pfx.password', o.partnerClientPassword);
  add('ewp-partner-pki', 'ewp-request-signing.pfx', o.requestSigningPfx, b64);
  add('ewp-partner-pki', 'ewp-request-signing.pfx.password', o.requestSigningPassword);
  add('ewp-partner-pki', 'ewp-trust-root.cer', o.trustRoot);
  return s;
}

function infraNamespace() {
  const s = { 'ewp-kafka-users': {}, 'ewp-db-roles': {}, 'ewp-db-admin': { password: [o.dbAdmin] } };
  for (const user of kafkaUsers) s['ewp-kafka-users'][user] = [o.kafka(user)];
  for (const role of dbRoles) s['ewp-db-roles'][role] = [o.dbRole(role)];
  return s;
}

function partnerBankNamespace() {
  return {
    'partner-bank-pki': {
      'partner-bank-server.pfx': [o.partnerServerPfx, b64],
      'partner-bank-server.pfx.password': [o.partnerServerPassword],
      'ewp-trust-root.cer': [o.trustRoot],
      'ewp-request-signing.cer': [o.requestSigningCer],
    },
    'partner-bank-window-client': { secret: [o.windowClient] },
  };
}

function observabilityNamespace() {
  return { 'ewp-grafana-oidc': { 'client-secret': [o.client(grafanaClientId)] } };
}

const namespaces = {
  ewp: ewpNamespace(), 'ewp-infra': infraNamespace(), observability: observabilityNamespace(), 'partner-bank': partnerBankNamespace(),
};

if (process.argv.includes('--list')) {
  for (const [ns, secrets] of Object.entries(namespaces))
    for (const [secret, keys] of Object.entries(secrets))
      for (const key of Object.keys(keys)) console.log(`${ns} ${secret} ${key}`);
  process.exit(0);
}

// ── Making values ────────────────────────────────────────────────────────────────────────────────

const env = (name) => {
  const value = process.env[name];
  if (!value) { console.error(`${name} is not set.`); process.exit(2); }
  return value;
};

// SEC: letters and digits only - safe inside a connection string and a URL without escaping.
const alphabet = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789';
const password = (length = 32) => Array.from({ length }, () => alphabet[randomInt(alphabet.length)]).join('');
const key32 = () => randomBytes(32).toString('base64');

function ecPkcs8() {
  const { privateKey } = generateKeyPairSync('ec', { namedCurve: 'P-256' });
  return privateKey.export({ type: 'pkcs8', format: 'der' }).toString('base64');
}

// A public JWK only (the customers' channel is not deployed: its private keys are thrown away).
function publicJwk() {
  const { publicKey } = generateKeyPairSync('ec', { namedCurve: 'P-256' });
  const jwk = publicKey.export({ format: 'jwk' });
  const kid = createHash('sha256').update(JSON.stringify({ crv: jwk.crv, kty: jwk.kty, x: jwk.x, y: jwk.y })).digest('base64url');
  return JSON.stringify({ kty: 'EC', crv: 'P-256', x: jwk.x, y: jwk.y, kid, use: 'sig', alg: 'ES256' });
}

// ┌─ SEC ──────────────────────────────────────────────────────────────────────────────────────────
// │ The demo's PKI, in the shapes of ps/pki/New-EwpLocalPki.ps1, valid for 7 days (a demo lives 6
// │ hours at most). The root's private key exists only in a temporary folder for these seconds.
// │ Each .pfx gets its own random password (OpenSSL 3: AES-256, PBKDF2), passed through the
// │ environment, never on a command line.
// └────────────────────────────────────────────────────────────────────────────────────────────────
function makePki(partnerDomain) {
  const dir = mkdtempSync(join(tmpdir(), 'ewp-pki-'));
  const openssl = (args, extraEnv = {}) => execFileSync('openssl', args, { cwd: dir, stdio: ['ignore', 'ignore', 'pipe'], env: { ...process.env, ...extraEnv } });
  // CONTRACT: the subjects of the laptop's PKI. The Partner Bank's authorization server knows
  // EWP's client by its certificate's subject, compared as .NET prints it ("CN=..., O=...") -
  // .NET prints the parts in reverse of their encoded order, so O is encoded first.
  const config = (name, [cn, org], extensions) => {
    writeFileSync(join(dir, `${name}.cnf`), [
      '[req]', 'prompt = no', 'distinguished_name = dn', 'x509_extensions = ext',
      '[dn]', `O = ${org}`, `CN = ${cn}`,
      '[ext]', ...extensions,
    ].join('\n'));
    return `${name}.cnf`;
  };
  const issue = (name, subject, keyArgs, extensions, selfSigned = false) => {
    openssl(['req', '-x509', '-new', ...keyArgs, '-nodes', '-keyout', `${name}.key`, '-out', `${name}.crt`, '-days', '7',
      '-config', config(name, subject, extensions), ...(selfSigned ? [] : ['-CA', 'root.crt', '-CAkey', 'root.key'])]);
  };
  const pfx = (name) => {
    const pfxPassword = password();
    openssl(['pkcs12', '-export', '-inkey', `${name}.key`, '-in', `${name}.crt`, '-out', `${name}.pfx`, '-passout', 'env:PFX_PASSWORD'], { PFX_PASSWORD: pfxPassword });
    return { pfx: readFileSync(join(dir, `${name}.pfx`)).toString('base64'), password: pfxPassword };
  };
  const pem = (name) => readFileSync(join(dir, `${name}.crt`), 'utf8');
  const rsa = ['-newkey', 'rsa:2048'];
  const leaf = ['basicConstraints = critical, CA:FALSE', 'subjectKeyIdentifier = hash', 'authorityKeyIdentifier = keyid'];
  try {
    issue('root', ['EWP Demo Trust Root (DEMO ONLY)', 'EWP V3 PoC'], ['-newkey', 'rsa:3072'],
      ['basicConstraints = critical, CA:TRUE', 'keyUsage = critical, keyCertSign, cRLSign', 'subjectKeyIdentifier = hash'], true);
    // The Partner Bank's server certificate: its public names and the API's in-cluster name
    // (https://partner-api:47404, the internal port).
    const partnerNames = ['partner-auth', 'partner-api', 'partner-products'];
    issue('partner-bank-server', [`partner-auth.${partnerDomain}`, 'Partner Bank (PoC)'], rsa, [...leaf,
      'keyUsage = critical, digitalSignature, keyEncipherment', 'extendedKeyUsage = serverAuth',
      `subjectAltName = ${[...partnerNames.map((n) => `DNS:${n}.${partnerDomain}`), ...partnerNames.map((n) => `DNS:${n}`)].join(', ')}`]);
    issue('ewp-partner-client', ['EWP BSS Partner Products Client', 'Four Walls Inc.'], rsa, [...leaf,
      'keyUsage = critical, digitalSignature', 'extendedKeyUsage = clientAuth']);
    issue('ewp-request-signing', ['EWP BSS Request Signing', 'Four Walls Inc.'], ['-newkey', 'ec', '-pkeyopt', 'ec_paramgen_curve:P-256'], [...leaf,
      'keyUsage = critical, digitalSignature']);
    // Self-signed, not trusted by anyone: the IDP's token-signing key and the Data Protection key
    // ring's encryption key, carried in certificates because that is how .NET loads them.
    issue('idp-signing', ['EWP IDP token signing (cloud demo)', 'EWP V3 PoC'], rsa, ['basicConstraints = critical, CA:FALSE',
      'keyUsage = critical, digitalSignature'], true);
    issue('data-protection', ['EWP Data Protection (cloud demo)', 'EWP V3 PoC'], rsa, ['basicConstraints = critical, CA:FALSE',
      'keyUsage = critical, keyEncipherment, dataEncipherment'], true);

    const server = pfx('partner-bank-server');
    const client = pfx('ewp-partner-client');
    const signing = pfx('ewp-request-signing');
    const idp = pfx('idp-signing');
    const dataProtection = pfx('data-protection');
    return {
      ewp: {
        [o.trustRoot]: pem('root'),
        [o.partnerClientPfx]: client.pfx, [o.partnerClientPassword]: client.password,
        [o.requestSigningPfx]: signing.pfx, [o.requestSigningPassword]: signing.password,
        [o.requestSigningCer]: pem('ewp-request-signing'),
        [o.idpSigningPfx]: idp.pfx, [o.idpSigningPassword]: idp.password,
        [o.dataProtectionPfx]: dataProtection.pfx, [o.dataProtectionPassword]: dataProtection.password,
      },
      partner: {
        [o.partnerServerPfx]: server.pfx, [o.partnerServerPassword]: server.password,
        [o.trustRoot]: pem('root'),
        [o.requestSigningCer]: pem('ewp-request-signing'),
      },
    };
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
}

// OBS: --check-pki makes a PKI (no Azure) and proves it is what the services expect: each .pfx
// opens with its password, the Partner Bank's server and EWP's client certificate chain to the root
// with the right purpose, the server certificate names every Partner host. CI runs it.
if (process.argv.includes('--check-pki')) {
  const pki = makePki('partner.example');
  const dir = mkdtempSync(join(tmpdir(), 'ewp-pki-check-'));
  const run = (args, extraEnv = {}) => execFileSync('openssl', args, { cwd: dir, encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'], env: { ...process.env, ...extraEnv } });
  try {
    writeFileSync(join(dir, 'root.pem'), pki.ewp[o.trustRoot]);
    const open = (name, pfx, pw) => {
      writeFileSync(join(dir, `${name}.pfx`), Buffer.from(pfx, 'base64'));
      run(['pkcs12', '-in', `${name}.pfx`, '-passin', 'env:P', '-nokeys', '-clcerts', '-out', `${name}.pem`], { P: pw });
      run(['pkcs12', '-in', `${name}.pfx`, '-passin', 'env:P', '-nocerts', '-noenc', '-out', `${name}.key`], { P: pw });
      return run(['x509', '-in', `${name}.pem`, '-noout', '-text']);
    };
    const server = open('server', pki.partner[o.partnerServerPfx], pki.partner[o.partnerServerPassword]);
    run(['verify', '-CAfile', 'root.pem', '-purpose', 'sslserver', 'server.pem']);
    for (const host of ['partner-auth.partner.example', 'partner-api.partner.example', 'partner-products.partner.example', 'partner-api'])
      if (!server.includes(`DNS:${host}`)) throw new Error(`The Partner Bank's certificate lacks ${host}.`);
    open('client', pki.ewp[o.partnerClientPfx], pki.ewp[o.partnerClientPassword]);
    run(['verify', '-CAfile', 'root.pem', '-purpose', 'sslclient', 'client.pem']);
    // .NET prints this as "CN=EWP BSS Partner Products Client, O=Four Walls Inc." (the Partner
    // Bank's PartnerBank:EwpClient:CertificateSubject).
    const subject = run(['x509', '-in', 'client.pem', '-noout', '-subject', '-nameopt', 'RFC2253']).trim();
    if (subject !== 'subject=CN=EWP BSS Partner Products Client,O=Four Walls Inc.')
      throw new Error(`EWP's client certificate has the subject ${subject}.`);
    if (!open('signing', pki.ewp[o.requestSigningPfx], pki.ewp[o.requestSigningPassword]).includes('prime256v1'))
      throw new Error('The request-signing key is not P-256.');
    open('idp', pki.ewp[o.idpSigningPfx], pki.ewp[o.idpSigningPassword]);
    open('dp', pki.ewp[o.dataProtectionPfx], pki.ewp[o.dataProtectionPassword]);
    if (pki.partner[o.trustRoot] !== pki.ewp[o.trustRoot] || !pki.partner[o.requestSigningCer].includes('BEGIN CERTIFICATE'))
      throw new Error('The Partner Bank must get the same root and the public request-signing certificate.');
    if (Object.keys(pki.partner).some((n) => n.includes('client') || n.includes('signing-pfx')))
      throw new Error("The Partner Bank must not get any of EWP's private keys.");
    console.log('PKI OK: 5 .pfx open with their passwords; server and client chain to the root; Partner hosts named; client subject as the Partner Bank expects.');
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
  process.exit(0);
}

// ── Key Vault (REST API 7.4) ─────────────────────────────────────────────────────────────────────

const token = execFileSync('az', ['account', 'get-access-token', '--resource', 'https://vault.azure.net', '--query', 'accessToken', '-o', 'tsv'],
  { encoding: 'utf8', shell: process.platform === 'win32' }).trim();

async function vaultCall(vault, method, name, body) {
  for (let attempt = 1; ; attempt++) {
    const response = await fetch(`https://${vault}.vault.azure.net/secrets/${name}?api-version=7.4`, {
      method, headers: { Authorization: `Bearer ${token}`, 'Content-Type': 'application/json' }, body: body && JSON.stringify(body),
    });
    if (method === 'GET' && response.status === 404) return undefined;
    if (response.ok) return method === 'GET' ? (await response.json()).value : undefined;
    // RESIL: a new role assignment can take minutes to reach Key Vault (403), and Key Vault
    // throttles bursts (429): wait and try again, for up to about 5 minutes.
    if ((response.status === 403 || response.status === 429 || response.status >= 500) && attempt < 20) {
      await new Promise((resolve) => setTimeout(resolve, 15000));
      continue;
    }
    throw new Error(`Key Vault ${vault}: ${method} ${name} answered ${response.status} ${(await response.text()).slice(0, 300)}`);
  }
}

// A few calls at a time: Key Vault allows bursts, not hundreds at once.
async function inBatches(items, action, size = 8) {
  for (let i = 0; i < items.length; i += size) await Promise.all(items.slice(i, i + size).map(action));
}

// ── Run ──────────────────────────────────────────────────────────────────────────────────────────

const ewpVault = env('EWP_KEY_VAULT');
const partnerVault = env('PARTNER_KEY_VAULT');
const partnerDomain = env('PARTNER_DOMAIN');
const postgresHost = env('POSTGRES_HOST');
const outDir = env('OUT_DIR');

// Every base value and how to make it the first time.
const base = { ewp: {}, partner: {} };
for (const role of dbRoles) base.ewp[o.dbRole(role)] = () => password();
for (const user of kafkaUsers) base.ewp[o.kafka(user)] = () => password();
for (const id of [...Object.keys(clients), grafanaClientId]) base.ewp[o.client(id)] = () => password(40);
for (const name of ['screening-provider', 'core-banking', 'payment-network']) base.ewp[o.apiKey(name)] = () => password(40);
base.ewp[o.simulatorAdminKey] = () => password(40);
for (const name of ['kyc-bff', 'audit-web', 'operations-web', 'partner-products-web']) base.ewp[o.sessionKey(name)] = key32;
base.ewp[o.kycSessionSecret] = () => password(48);
base.ewp[o.documentKey] = key32;
base.ewp[o.anchorKey] = ecPkcs8;
base.ewp[o.keystorePassword] = () => password();
base.ewp[o.clientKey('selfservice-web')] = publicJwk;
base.ewp[o.clientKey('selfservice-journeyapi')] = publicJwk;
base.partner[o.windowClient] = () => password(40);

const values = { ewp: {}, partner: {} };
let made = 0;
for (const [side, vault] of [['ewp', ewpVault], ['partner', partnerVault]]) {
  const names = Object.keys(base[side]);
  await inBatches(names, async (name) => {
    let value = await vaultCall(vault, 'GET', name);
    if (value === undefined) {
      value = base[side][name]();
      await vaultCall(vault, 'PUT', name, { value, contentType: 'ewp-demo' });
      made++;
    }
    values[side][name] = value;
  });
}

// The PKI: all or nothing (a re-run after a half-written PKI issues a complete new one).
const pkiNames = {
  ewp: [o.trustRoot, o.partnerClientPfx, o.partnerClientPassword, o.requestSigningPfx, o.requestSigningPassword,
    o.requestSigningCer, o.idpSigningPfx, o.idpSigningPassword, o.dataProtectionPfx, o.dataProtectionPassword],
  partner: [o.partnerServerPfx, o.partnerServerPassword, o.trustRoot, o.requestSigningCer],
};
const existing = { ewp: {}, partner: {} };
await inBatches(pkiNames.ewp, async (n) => { existing.ewp[n] = await vaultCall(ewpVault, 'GET', n); });
await inBatches(pkiNames.partner, async (n) => { existing.partner[n] = await vaultCall(partnerVault, 'GET', n); });
const pkiComplete = [...Object.values(existing.ewp), ...Object.values(existing.partner)].every((v) => v !== undefined);
const pki = pkiComplete ? existing : makePki(partnerDomain);
if (!pkiComplete) {
  await inBatches(Object.entries(pki.ewp), ([n, v]) => vaultCall(ewpVault, 'PUT', n, { value: v, contentType: 'ewp-demo-pki' }));
  await inBatches(Object.entries(pki.partner), ([n, v]) => vaultCall(partnerVault, 'PUT', n, { value: v, contentType: 'ewp-demo-pki' }));
  made += pkiNames.ewp.length + pkiNames.partner.length;
}
Object.assign(values.ewp, pki.ewp);
Object.assign(values.partner, pki.partner);

// ┌─ SEC ──────────────────────────────────────────────────────────────────────────────────────────
// │ Values built from others, written as their own secrets (rewritten only when they differ).
// │ The services verify the server's certificate and name: SSL Mode=VerifyFull (Npgsql) and
// │ sslmode=verify-full (node-postgres) - Azure's certificate chains to a public root.
// └────────────────────────────────────────────────────────────────────────────────────────────────
const derived = {};
for (const entries of Object.values(databases))
  for (const [, role, db] of entries)
    derived[o.conn(role, db)] = `Host=${postgresHost};Port=5432;Database=${db};Username=${role};Password=${values.ewp[o.dbRole(role)]};SSL Mode=VerifyFull`;
for (const [, role] of Object.values(databaseUrls))
  derived[o.dbUrl(role)] = `postgres://${role}:${values.ewp[o.dbRole(role)]}@${postgresHost}:5432/EwpBffStateDb?sslmode=verify-full`;
await inBatches(Object.entries(derived), async ([name, value]) => {
  if ((await vaultCall(ewpVault, 'GET', name)) !== value) {
    await vaultCall(ewpVault, 'PUT', name, { value, contentType: 'ewp-demo-derived' });
    made++;
  }
});

// The LaunchDarkly SDK key as given (rewritten only when the GitHub secret changed).
if (launchDarklySdkKey && (await vaultCall(ewpVault, 'GET', o.launchDarklySdkKey)) !== launchDarklySdkKey) {
  await vaultCall(ewpVault, 'PUT', o.launchDarklySdkKey, { value: launchDarklySdkKey, contentType: 'ewp-demo-given' });
  made++;
}

// Every object a list names must now be in its vault (the admin password from Terraform too).
const known = { ewp: new Set([...Object.keys(values.ewp), ...Object.keys(derived), o.dbAdmin, ...(launchDarklySdkKey ? [o.launchDarklySdkKey] : [])]), partner: new Set(Object.keys(values.partner)) };
if ((await vaultCall(ewpVault, 'GET', o.dbAdmin)) === undefined) throw new Error(`${o.dbAdmin} is missing from ${ewpVault} (Terraform makes it).`);

const settings = {
  ewp: [ewpVault, env('EWP_SECRETS_CLIENT_ID'), 'ewp'],
  'ewp-infra': [ewpVault, env('EWP_SECRETS_CLIENT_ID'), 'ewp'],
  observability: [ewpVault, env('EWP_SECRETS_CLIENT_ID'), 'ewp'],
  'partner-bank': [partnerVault, env('PARTNER_SECRETS_CLIENT_ID'), 'partner'],
};
for (const [ns, secrets] of Object.entries(namespaces)) {
  const [vault, clientId, side] = settings[ns];
  const list = Object.entries(secrets).map(([name, keys]) => ({
    name,
    keys: Object.entries(keys).map(([key, [object, encoding]]) => {
      if (!known[side].has(object)) throw new Error(`${ns}/${name}/${key}: ${object} is not in ${vault}.`);
      return encoding ? { key, object, encoding } : { key, object };
    }),
  }));
  writeFileSync(join(outDir, `keyvault-secrets-${ns}.json`), JSON.stringify({ keyVault: { name: vault, tenantId: env('TENANT_ID'), clientId }, secrets: list }, null, 2));
}
writeFileSync(join(outDir, 'partner-bank-ca.pem'), values.partner[o.trustRoot]);

console.log(`Key Vault: ${made} secret(s) written, ${Object.keys(values.ewp).length + Object.keys(derived).length} in ${ewpVault}, ${Object.keys(values.partner).length} in ${partnerVault}.`);
console.log(`Lists written to ${outDir} for ${Object.keys(namespaces).join(', ')}.`);