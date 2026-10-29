// ┌─ :: ARCHITECTURE :: The lists that must agree, checked without a cluster ──────────────────────
// │ Why : The cloud demo's secrets, Kafka users and topics are written in several places: the
// │       charts that read them, the script that makes them (.github/scripts/demo-secrets.mjs),
// │       and the laptop's scripts they copy (db/EwpServiceDbUsers.sql,
// │       ps/kafka/Setup-KafkaSecurity.ps1). A key one side forgets shows up only as a pod that
// │       cannot start, or a service Kafka refuses - in a demo, in front of people.
// │ How : Reads the rendered charts (helm template output) and the scripts as text, and fails on
// │       any difference:
// │         - every Secret key the EWP and ewp-infra charts read is made by demo-secrets.mjs, and
// │           every key it makes is read (Secrets mounted whole count as read);
// │         - the database roles it makes are exactly the users script's roles;
// │         - the Kafka users and topics are exactly the laptop's (admin and Kafka UI excepted).
// │ Ref : deploy/helm/check-charts.sh | doc/Kubernetes.md
// └────────────────────────────────────────────────────────────────────────────────────────────────
// Usage: node check-contracts.mjs <rendered ewp.yaml> <rendered ewp-infra.yaml>
import { execFileSync } from 'node:child_process';
import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const repo = join(dirname(fileURLToPath(import.meta.url)), '..', '..');
const [ewpFile, infraFile, observabilityFile] = process.argv.slice(2);
const ewp = readFileSync(ewpFile, 'utf8');
const infra = readFileSync(infraFile, 'utf8');
const observability = readFileSync(observabilityFile, 'utf8');

let failures = 0;
const fail = (message) => { console.log(`::error::${message}`); failures++; };
const sameSet = (what, expected, actual) => {
  const missing = [...expected].filter((x) => !actual.has(x));
  const extra = [...actual].filter((x) => !expected.has(x));
  if (missing.length) fail(`${what}: missing ${missing.join(', ')}`);
  if (extra.length) fail(`${what}: not expected ${extra.join(', ')}`);
  if (!missing.length && !extra.length) console.log(`ok  ${what} (${expected.size})`);
};
const matches = (text, regex) => [...text.matchAll(regex)];

// What the generator makes, per namespace: Set of "secret key", and the Secret names.
const made = {};
for (const line of execFileSync('node', [join(repo, '.github/scripts/demo-secrets.mjs'), '--list'], { encoding: 'utf8' }).trim().split('\n')) {
  const [ns, secret, key] = line.split(' ');
  (made[ns] ??= new Set()).add(`${secret} ${key}`);
}
const secretsOf = (set) => new Set([...set].map((x) => x.split(' ')[0]));

// What a rendered chart reads: env/password references (secret + key) and whole mounted Secrets.
function reads(rendered) {
  const keys = new Set(matches(rendered, /(?:secretKeyRef|passwordSecretRef):\s*\n\s*name: (\S+)\s*\n\s*key: (\S+)/g).map((m) => `${m[1]} ${m[2]}`));
  const mounted = new Set(matches(rendered, /secret:\s*\n\s*secretName: (\S+)/g).map((m) => m[1]).filter((n) => !n.endsWith('-tls')));
  return { keys, mounted };
}

for (const [ns, rendered] of [['ewp', ewp], ['ewp-infra', infra], ['observability', observability]]) {
  const { keys, mounted } = reads(rendered);
  const generated = made[ns];
  for (const key of keys) if (!generated.has(key)) fail(`${ns}: the chart reads "${key}", which demo-secrets.mjs does not make`);
  for (const secret of mounted) if (!secretsOf(generated).has(secret)) fail(`${ns}: the chart mounts Secret ${secret}, which demo-secrets.mjs does not make`);
  for (const entry of generated) if (!keys.has(entry) && !mounted.has(entry.split(' ')[0])) fail(`${ns}: demo-secrets.mjs makes "${entry}", which no chart reads`);
  console.log(`ok  ${ns}: ${keys.size} secret keys and ${mounted.size} mounted Secrets checked against demo-secrets.mjs`);
}

const sql = readFileSync(join(repo, 'db/EwpServiceDbUsers.sql'), 'utf8');
sameSet('database roles (db/EwpServiceDbUsers.sql vs demo-secrets.mjs)',
  new Set(matches(sql, /\('(ewp_[a-z_]+)',\s*'[^']*'\)/g).map((m) => m[1])),
  new Set([...made['ewp-infra']].filter((x) => x.startsWith('ewp-db-roles ')).map((x) => x.split(' ')[1])));

const ps1 = readFileSync(join(repo, 'ps/kafka/Setup-KafkaSecurity.ps1'), 'utf8');
const usersBlock = ps1.slice(ps1.indexOf('$Users = [ordered]@{'), ps1.indexOf('$Topics = @('));
const laptopUsers = new Set(matches(usersBlock, /^\s*'([a-z0-9-]+)'\s*=/gm).map((m) => m[1]).filter((u) => u !== 'admin' && u !== 'ewp-kafka-ui'));
const kafkaUsers = new Set(matches(infra, /kind: KafkaUser\s*\nmetadata:\s*\n\s*name: (\S+)/g).map((m) => m[1]));
sameSet('Kafka users (ps/kafka/Setup-KafkaSecurity.ps1 vs ewp-infra)', laptopUsers, kafkaUsers);
sameSet('Kafka user passwords (ewp-infra vs demo-secrets.mjs)', kafkaUsers,
  new Set([...made['ewp-infra']].filter((x) => x.startsWith('ewp-kafka-users ')).map((x) => x.split(' ')[1])));

const topicsBlock = ps1.slice(ps1.indexOf('$Topics = @('), ps1.indexOf(')', ps1.indexOf('$Topics = @(') + 12));
sameSet('Kafka topics (ps/kafka/Setup-KafkaSecurity.ps1 vs ewp-infra)',
  new Set(matches(topicsBlock, /'([a-z0-9.-]+)'/g).map((m) => m[1])),
  new Set(matches(infra, /kind: KafkaTopic\s*\nmetadata:\s*\n\s*name: (\S+)/g).map((m) => m[1])));

if (failures) process.exit(1);
console.log('Contracts OK');