# Cloud demo (Azure)

How a demo of EWP appears in Azure, runs for a few hours, and disappears again. **Demo up** makes
everything in one approved run, **Demo down** removes all of it, and a **watchdog** removes any
demo that was forgotten.

In short: **one Terraform root module ([deploy/terraform/demo](../deploy/terraform/demo)) makes
the network, an AKS cluster, a PostgreSQL server, two Key Vaults (EWP's, the Partner Bank's), a
public IP and the two domains' address records. A script then puts the cluster's way in on top
(Envoy Gateway, cert-manager, Let's Encrypt), and a second one the platform: Kafka, Temporal, the
12 databases, every secret (made once per demo, kept in Key Vault only), the Partner Bank from
its own repository, and EWP's 35 services. Nothing is kept between demos except what the
bootstrap made ([Azure.md](Azure.md)). No secret is stored in GitHub, the Terraform state or the
repository** - except one you may add: the LaunchDarkly SDK key (CD22), which LaunchDarkly makes,
not Demo up.

Step 7 of the cloud demo, in two parts:

| Part | What | Status |
|---|---|---|
| **7a** | Azure resources (Terraform), cluster add-ons and the public gateway, the three workflows | Built 25-OCT-2026; first runs 25-OCT-2026 (Central India, staging): Demo up and Demo down green |
| **7b** | The platform: Kafka, Temporal, the 12 databases and their users, the secrets in Key Vault, the EWP and Partner Bank charts | Built 25-OCT-2026; first run 25-OCT-2026 (Central India, staging): Demo up green, all 8 demo users signed in at the Shell; production certificates 26-OCT-2026: the first run found three faults (Digital Gold's token, the simulators' switch, a re-run conflict - all fixed, see below); **every business journey then passed in the browser, 26-OCT-2026 (Central India, production certificates)**: onboarding with notifications, cash deposit, payments ($100 automatic, $1,100 approved by the payments officer), Digital Gold ($20 automatic, $1,100 approved) with the Partner Bank's staff view, Platform Health and the simulators for both operators, the auditor's trail and its integrity check |

## What Demo up makes

Everything in `rg-ewp-demo` (the bootstrap's throw-away group), in the region the run chooses
(CD13), plus two records in the permanent DNS zones:

| Resource | Name | Notes |
|---|---|---|
| Virtual network | `vnet-ewp-demo` (10.224.0.0/16) | Subnets `snet-aks-nodes` (/20) and `snet-postgres` (/24, delegated to PostgreSQL) |
| AKS cluster | `aks-ewp-demo` | Free tier, Kubernetes 1.34, `node_count` x `Standard_D4as_v5` (default 4), Azure Linux. Azure CNI overlay with Cilium (pods 10.244.0.0/16), Entra ID sign-in only, workload identity, Key Vault CSI driver. AKS makes its own node group `rg-ewp-demo-nodes` and deletes it with the cluster |
| Managed identity | `id-ewp-demo-aks` | The cluster's control plane. Network Contributor on its subnet and on the gateway's public IP |
| PostgreSQL Flexible Server | `psql-ewp-demo-<5 random>` | Version 18, Burstable B2s, 32 GB, no public access, TLS required. Admin `ewpadmin` |
| Private DNS zone | `ewp-demo.private.postgres.database.azure.com` | Resolves the server inside the VNet only |
| Key Vaults | `kv-ewp-demo-<5 random>`, `kv-pb-demo-<5 random>` | RBAC only, 7-day soft delete. EWP's holds `postgres-admin-password` and every EWP secret; the Partner Bank's only the Partner Bank's (CD15) |
| Managed identities | `id-ewp-demo-secrets`, `id-pb-demo-secrets` | Key Vault Secrets User on their own vault only; federated credentials for the service account `keyvault-secrets` in `ewp` and `ewp-infra`, and in `partner-bank` (CD14) |
| Public IP | `pip-ewp-demo-gateway` | Static, Standard. Envoy's Service binds it by name |
| DNS records | `*.<EWP domain>`, `*.<Partner Bank domain>` | A records to the public IP, TTL 5 minutes |
| Role assignments | | The workflow: AKS RBAC Cluster Admin on the cluster, Key Vault Secrets Officer on both vaults; the two identities above (all allowed by the bootstrap's condition, [Azure.md](Azure.md) A4) |

In the cluster ([demo-cluster-addons.sh](../.github/scripts/demo-cluster-addons.sh), values in
[deploy/cluster-addons](../deploy/cluster-addons)):

| Add-on | Version | Namespace | Why |
|---|---|---|---|
| Envoy Gateway | 1.9.2 (Gateway API 1.6) | `envoy-gateway-system`; proxies in `gateway-system` | HTTPRoute + BackendTLSPolicy for EWP, TLSRoute pass-through for the Partner Bank (7b) |
| cert-manager | 1.21.2 | `cert-manager` | Let's Encrypt for the browser; EWP's private CA for TLS between pods |
| trust-manager | 0.25.0 | `cert-manager` | The trust bundles in every EWP namespace |
| [ewp-cluster](../deploy/helm/ewp-cluster) | | `cert-manager` | EWP's private CA and bundles ([Kubernetes.md](Kubernetes.md)) |
| [ewp-gateway](../deploy/helm/ewp-gateway) | | `gateway-system` | Envoy bound to the public IP, the Gateway (EWP's listeners and the Partner Bank's four, CD19), Let's Encrypt issuers, one certificate for every EWP host and one for the Partner Bank's browser hosts, the gateway check |

Then the platform ([demo-platform.sh](../.github/scripts/demo-platform.sh)):

| What | Namespace | Notes |
|---|---|---|
| Secrets | (Key Vault) | [demo-secrets.mjs](../.github/scripts/demo-secrets.mjs) makes every secret and the demo PKI the first time and writes them to the two vaults (CD15) |
| [keyvault-secrets](../deploy/helm/keyvault-secrets) | `ewp-infra`, `ewp`, `partner-bank` | One per namespace: the Kubernetes Secrets the other charts name, synced from Key Vault (CD14) |
| Strimzi 1.2.0 | `strimzi` | The Kafka operator, watching `ewp-infra` only ([strimzi.yaml](../deploy/cluster-addons/strimzi.yaml)) |
| [ewp-infra](../deploy/helm/ewp-infra) | `ewp-infra` | Kafka 4.3.1 (one KRaft node, TLS + SCRAM, the laptop's users, ACLs and topics; CD16), Temporal (CD17), the database job (CD18) |
| [partner-bank](https://github.com/dineshmj/EnterpriseWebPlatform_V3_PartnerBank/tree/main/deploy/helm/partner-bank) | `partner-bank` | From the Partner Bank's repository (`main`), its three images by digest (CD19, CD20) |
| [ewp](../deploy/helm/ewp) | `ewp` | 35 services, one instance each ([values-demo.yaml](../deploy/helm/ewp/values-demo.yaml)), images by digest (CD20) |

## Decisions

| # | Decision | Why |
|---|---|---|
| CD1 | **Two parts: 7a (Azure + the way in), 7b (the platform)** (decided 25-OCT) | Each part gets its own pull request and first run. A failure in the first run of 7a is about Azure, DNS or certificates, not about 35 services |
| CD2 | **One Terraform root module for everything that costs money**, applied by Demo up and destroyed by Demo down. It reads the demo group and the DNS zones and never creates or deletes them | One `terraform destroy` removes the whole demo. The group carries the identity's rights and the zones carry the name servers, so both must outlive every demo ([Azure.md](Azure.md) A2, A8) |
| CD3 | **Kubernetes add-ons by script (Helm CLI), not by Terraform's Helm provider** | A provider that talks to a cluster made in the same apply needs a second apply or fragile ordering. The script is idempotent and runs from a laptop too. Terraform holds what Azure bills |
| CD4 | **Envoy Gateway** in the cluster (decided 25-OCT), not Application Gateway for Containers | No extra Azure charge. It supports BackendTLSPolicy (EWP chart) and TLSRoute pass-through, which the Partner Bank's mutual TLS needs: its handshake must end in its own pods |
| CD5 | **Azure Database for PostgreSQL Flexible Server** (decided 25-OCT), Burstable B2s, VNet integration only | Managed, as in production, and nothing to look after for a few hours. B1ms allows 50 connections, too few for the 12 databases' pools |
| CD6 | **The database admin password never reaches the state.** An ephemeral `random_password` is written once to Key Vault; the server takes it back from Key Vault (ephemeral read). Both through write-only arguments | A state file holds every attribute in plain text. Write-only arguments (Terraform 1.11+) keep a generated secret out of the plan and the state. Key Vault as the single source: a re-run after a half-finished apply cannot give the server another password than the vault holds |
| CD7 | **A static public IP made by Terraform, wildcard A records to it** | The records exist before the cluster, in the same state as the IP, so they are destroyed together (no dangling name). A wildcard needs no host list to keep in step with the charts |
| CD8 | **Let's Encrypt over HTTP-01 through the Gateway**, one certificate for every EWP host. A run chooses production (trusted, 5 certificates for the same names per week) or staging (test runs) | Nothing in the cluster gets DNS rights ([Azure.md](Azure.md) A8). One certificate means one order per demo |
| CD9 | **Cluster sign-in through Entra ID only**: local accounts off, Azure RBAC for Kubernetes, the workflow is Cluster Admin of this cluster alone; kubelogin turns the Azure CLI's sign-in into a token | No static admin certificate exists to leak. The role comes from the bootstrap's short list |
| CD10 | **Watchdog: hourly, 6 hours** (decided 25-OCT). It removes a demo whose start stamp (a tag on `rg-ewp-demo`, set by the first Demo up) is 6 hours old, or anything in the group without a stamp. It runs in `azure-demo-teardown` (no reviewer, [Azure.md](Azure.md) A10) | The budget only warns. A re-run of Demo up keeps the first stamp, so it cannot extend a demo |
| CD11 | **Demo down verifies**: after `terraform destroy`, the group must be empty and neither zone may keep its wildcard record. Leftovers are deleted and the run fails | Terraform removes only what its state lists. A wildcard record left pointing at a released Azure IP could serve someone else's site under EWP's name (sub-domain takeover) |
| CD12 | **Every tool pinned and checksum-verified** ([demo-tools.sh](../.github/scripts/demo-tools.sh)): Terraform 1.16.7, kubectl 1.34.12, kubelogin 0.2.20, Helm 4.3.1; providers exact (`azurerm` 5.9.0, `random` 3.9.1, `time` 0.14.2) with `.terraform.lock.hcl`, `init -lockfile=readonly`. Dependabot proposes provider updates | These tools run with the identity's rights. Versions at least two weeks old when chosen |
| CD13 | **The region is chosen per run**: `centralindia` (default) or `australiaeast` (decided 25-OCT). The group stays in Australia East; only its resources move. A run refuses another region while a demo is up (the group is stamped with the region) | The demo should be close to whoever uses it: from Bangalore, Central India answers in about 20 ms instead of about 140 ms, and its nodes cost half. An audience in Australia gets Australia East. A group's region only holds its metadata, so nothing permanent has to move |
| CD14 | **Secrets reach the pods through the Secrets Store CSI driver, one sync pod per namespace** ([keyvault-secrets](../deploy/helm/keyvault-secrets)): a SecretProviderClass lists which Key Vault secret becomes which key of which Kubernetes Secret; a pause pod mounts it, so the driver writes the Secrets (and refreshes them every 2 minutes). The driver signs in with **workload identity**: a federated credential trusts the namespace's `keyvault-secrets` service account | The charts already name Secrets (K5) and stay unchanged. No pod but the sync pod depends on Key Vault to start, and only it carries the Key Vault identity; no client secret exists, and no Kubernetes token is mounted (the driver asks for the token itself) |
| CD15 | **Every secret is made once per demo by Demo up and kept in Key Vault only**: random passwords and keys, and a throw-away PKI in the laptop PKI's shapes (root, the Partner Bank's server certificate, EWP's mutual-TLS client and request-signing certificates, the IDP's signing and the Data Protection certificates; 7 days). A re-run reads them back. **Two vaults, one per organisation**: the Partner Bank's gets only its server certificate and the public certificates of EWP's root and request-signing key. The root's private key is never stored | Nothing to carry from the laptop, nothing that outlives the demo. A re-run must not change a password a database role or Kafka user already has. EWP's identities cannot read the Partner Bank's keys, nor the other way round. Values are never printed; CI checks that every key a chart reads is made, and the other way round ([check-contracts.mjs](../deploy/helm/check-contracts.mjs)) |
| CD16 | **Kafka by Strimzi** (decided 25-OCT): one KRaft node; the listener's certificate from EWP's private CA (the clients already trust it), SCRAM-SHA-512, simple authorization, no auto-created topics; one KafkaUser per deployable with exactly the laptop's ACLs, its password from Key Vault | The laptop's security, declared instead of scripted. CI checks the users and topics against [Setup-KafkaSecurity.ps1](../ps/kafka/Setup-KafkaSecurity.ps1) |
| CD17 | **Temporal as the development server in one pod** (decided 25-OCT), its SQLite file on a 1 GB volume, gRPC open to the Journey API and the worker only | As on the laptop. No TLS or authorization on its frontend (Threat-Model R-16): the NetworkPolicy is the control; production: Temporal Cloud or the full chart with mTLS |
| CD18 | **The databases by a Job running the repository's full scripts** on the private server: create the missing databases, the users script, each NEW database's script, then the roles' passwords from Key Vault. Two adaptations on the fly: `FOR ROLE postgres` names the admin login, `NOSUPERUSER` is dropped (Azure has no superuser) | The same schema and least-privilege users as the laptop, no copy to drift. A re-run of Demo up never erases data |
| CD19 | **The Partner Bank's browser ports end TLS at the Gateway** with a Let's Encrypt certificate and are re-encrypted to the pod (checked against the demo PKI's root); its **mutual-TLS ports are passed through** untouched. Its deployables' own calls to their issuer address may also chain to Let's Encrypt's roots (the Partner Bank's `Tls:BackchannelTrustRootPaths`, never for client certificates) | A browser would not trust the demo PKI: the consent and staff pages would show a certificate warning. EWP's client certificate must still reach the Partner Bank's pod in the handshake (CD4) |
| CD20 | **Every image pinned to the digest its `main` tag has when Demo up runs** | Images are built only for the folders a commit changed, so no single commit SHA names all 38 images. A digest cannot move during the demo |
| CD21 | **A staging run adds Let's Encrypt's staging roots to the pods' trust bundle** (and to the Partner Bank's own calls), never to the private-root bundle the Gateway checks pods with | The pods call the IDP through the Gateway; with an untrusted staging certificate every sign-in would fail, so staging could not test the platform |
| CD22 | **Feature flags from the chart's flag file - or LaunchDarkly when the GitHub secret `LAUNCHDARKLY_SDK_KEY` exists**: Demo up then writes the key to Key Vault (`launchdarkly-sdk-key` → Secret `ewp-launchdarkly`) and installs EWP with `global.featureFlags.launchDarkly.enabled=true` ([Kubernetes.md](Kubernetes.md) K13) | The demo works without any account (the ConfigMap `ewp-feature-flags`, flipped with `kubectl edit`); with one, flags are flipped in LaunchDarkly's console during a demo, as the customer does. The key is the only secret made outside Demo up, so it is the only one GitHub holds; the five APIs that read flags then may open HTTPS to public addresses (LaunchDarkly's IP ranges change and it advises allowing its domains, which a NetworkPolicy cannot name) |

## Before the first run

1. **The domains are delegated.** `nslookup -type=NS <domain> 8.8.8.8` lists the four
   `azure-dns` name servers for **both** domains. Demo up checks this first and stops if not.
2. **Run the bootstrap once more** (same parameters, with both domains): it adds the federated
   credential for `azure-demo-teardown` ([Azure.md](Azure.md) A10).
3. **Create the environment `azure-demo-teardown`** with the same ten variables
   ([Azure.md](Azure.md), GitHub side): no reviewer, `main` only.
4. **Check the vCPU quota** in each region you use (quotas are per region): Azure portal →
   Subscriptions → your subscription → Usage + quotas → Region filter = the region, search
   `Total Regional` and `DASv5`. *Total Regional vCPUs* and *Standard DASv5 Family vCPUs* must
   each allow `node_count` x 4 (16 for the default four nodes, so ask for 20). Raising a quota is free (the row's pencil). The identity cannot read quotas, so a
   shortfall shows only as an apply error. State on 25-OCT-2026: Central India 20 / 20 requested
   (Total Regional approved, DASv5 under review), Australia East 10 / 10.
5. **Merge this step** to `main`: the workflows run only from there.
6. *Optional:* **LaunchDarkly** - add the repository secret `LAUNCHDARKLY_SDK_KEY` (the SDK key of
   the LaunchDarkly environment the demo should use) and create the two flags there
   ([feature-flags/README.md](../feature-flags/README.md)). Without it the flags come from the
   chart (CD22).

## Running a demo

1. **Actions → Demo up → Run workflow** (branch `main`). Choose the **region** closest to the
   audience (`centralindia` from India, `australiaeast` for Australia). For test runs choose
   `letsencrypt-staging`; for the real demo keep `letsencrypt-production`.
2. **Approve** the `azure-demo` deployment. The run takes about 45-60 minutes: AKS and
   PostgreSQL take the longest, then the platform about 20-30.
3. On the way it runs the **gateway check**: `https://gateway-check.<EWP domain>/` answers
   `EWP gateway OK`, and `http://` redirects to `https://`. It ends when the IDP's and the Partner
   Bank's discovery documents answer through the gateway, and EWP gets its own token from the
   Partner Bank over mutual TLS from a pod in the cluster (as Digital Gold does; on a failure the
   run stops with the reason and the Partner pods' warnings). Its summary shows the start time, the
   gateway's address and `https://shell.<EWP domain>/`; sign in with the same demo users as on
   the laptop.
4. When finished: **Actions → Demo down → Run workflow**. No approval needed. It takes about
   10-15 minutes and ends with "rg-ewp-demo is empty". Before the teardown it prints every pod's
   recent warnings and errors into its log (step "The pods' warnings and errors"), so a fault seen
   in the browser can still be explained afterwards.

Looking at the cluster from the laptop (Azure CLI signed in as the subscription owner, who
first needs a cluster role, e.g. "Azure Kubernetes Service RBAC Reader" on the cluster):

```text
az aks get-credentials -g rg-ewp-demo -n aks-ewp-demo --format azure
kubelogin convert-kubeconfig -l azurecli
kubectl get pods -A
```

## Cost

Pay-as-you-go retail prices (Azure retail prices API, 25-OCT-2026, USD per hour; billed in the
subscription's currency):

| Item | `centralindia` | `australiaeast` |
|---|---|---|
| 4 nodes `Standard_D4as_v5` (Linux) | 0.44 (0.111 each) | 0.86 (0.216 each) |
| PostgreSQL B2s (+ 32 GB storage, about 0.006) | 0.10 | 0.11 |
| 4 OS disks (P6), the platform's small data disks, load balancer, public IP | about 0.07 | about 0.09 |
| AKS control plane (Free tier), Key Vaults, DNS queries | about 0 | about 0 |
| **Total** | **about USD 0.61 (₹54)** | **about USD 1.06 (₹94)** |

A 3-hour demo costs about USD 1.85 (₹165) in Central India, USD 3.20 (₹285) in Australia East.
The 6-hour watchdog limit caps a forgotten one at about ₹330 / ₹565: in Australia East that is
more than the ₹420 monthly budget, so end an Australian demo with Demo down rather than leave it
to the watchdog. A test run of 7a alone can use `node_count` 2 (Terraform variable).

## When something goes wrong

| What you see | Meaning | What to do |
|---|---|---|
| `... is not delegated to Azure DNS` (first step) | The registrar still lists other name servers | Set the zone's four name servers at the registrar ([Azure.md](Azure.md), Domains), wait, run again |
| `OperationNotAllowed ... quota` in `terraform apply` | Too few vCPUs in the region | Raise the quota (Before the first run, 4), or lower `node_count` |
| `AuthorizationFailed` on a role assignment | The bootstrap's condition refused a role or principal type | The role must be on the bootstrap's list, with `principal_type = "ServicePrincipal"` |
| `403` writing the Key Vault secret | The new role assignment had not reached Key Vault yet | Run Demo up again (it continues from the state) |
| `waiting for the cluster role to apply` many times | AKS RBAC assignments take a few minutes | Nothing, unless it gives up after 10 minutes |
| `AADSTS700024: Client assertion is not within its valid time range` | The Azure CLI still held the GitHub token it signed in with, which Entra ID accepts for 5 minutes only (first run, 25-OCT-2026: the apply took 18 minutes) | Fixed: both workflows sign in again after the long Terraform step. Seen again: the new step did not run |
| `ERROR: Unexpected 'properties_to_remove' was not empty` (Demo down, last step) | Current Azure CLI versions no longer remove a tag through `az group update --remove` (first run, 25-OCT-2026) | Fixed: the workflows set and remove the stamp with `az tag update` |
| `The gateway's Service has address ..., not ...` | Azure did not bind Terraform's IP: the annotations or the cluster identity's right on the IP | `kubectl describe service -n gateway-system` shows Azure's event |
| `certificate/ewp-public-tls` not Ready | Let's Encrypt could not reach `http://<host>/.well-known/acme-challenge/...` | The run prints the challenges. Usually DNS (delegation, or a resolver's cached "no such name"); with production, possibly the weekly limit (use staging) |
| Demo up failed half-way | What it made stays (and costs) | Run **Demo down**, or let the watchdog remove it within 6 hours. Or fix and run Demo up again: every step continues from where it is (secrets are read back, databases already made are kept) |
| `waiting for <Secret> ...` (the platform, 30 times) | The CSI driver did not write the namespace's Secrets: usually the sync pod cannot sign in to Key Vault (federated credential, role not yet effective) | The run prints the sync pod's events; `kubectl describe pod -n <namespace> -l app.kubernetes.io/name=keyvault-secrets` shows the driver's message (AADSTS... or 403) |
| `job/database-init-<n>` did not complete | A script failed on the managed server | The run prints the job's last 40 lines (`ON_ERROR_STOP`: the failing statement is named) |
| `kafka/ewp` not Ready | Strimzi could not start the broker | `kubectl describe kafka ewp -n ewp-infra` and the operator's log (`kubectl logs -n strimzi deploy/strimzi-cluster-operator`) |
| `No image ...:main on GHCR` | An image was never published from `main` | Run **Container images** for that image (Actions), then Demo up again |
| `... did not answer with its issuer` (last step) | The platform is up but an issuer is not reachable through the gateway | `kubectl get pods -A`, then the pod's log; for the Partner Bank also `kubectl get httproute,backendtlspolicy -n partner-bank` |
| `ResourceGroupDeletionBlocked` / "Rolling Upgrade in progress" in Demo down | Azure was upgrading the nodes' VM agent and would not delete the node group (first Demo down, 25-OCT-2026) | Wait until the upgrade ends (it did, by itself, within the hour) or cancel it (owner: `az vmss rolling-upgrade cancel`), then run Demo down again |
| The Shell shows its menu, but a micro front end's frame stays empty or shows the browser's error page (staging run) | The browser does not trust Let's Encrypt's staging certificate and cannot show its "accept the risk" page inside a frame (first sign-in, 25-OCT-2026). Not a fault of the platform: the BFFs answered correctly | Expected with staging only. Open each micro front end's host once in its own tab and accept the warning, then reload the Shell; or run with `letsencrypt-production` |
| `EWP could not get its token from the Partner Bank` (Demo up's last check, or Digital Gold's screen) | EWP's mutual-TLS call to `partner-auth.<Partner domain>:47411` failed (first production-certificate run, 26-OCT-2026, found in the browser) | The check prints the HTTP status, the Partner Bank's error and curl's TLS steps, then the Partner pods' warnings: a TLS error points at the certificates or the Gateway's passthrough listener, `invalid_client` at the Partner Bank's registration of EWP's client. Seen 26-OCT-2026 (second production run): TLS fine both ways, `invalid_client` because the Partner Bank registered EWP's client with the code flow but no redirect URI, which IdentityServer refuses whole (its log: `ValidatingClientStore`). Fixed in the Partner Bank repository (PR #5: the code flow only with a callback) |
| The simulators show "Unknown" on Simulator & fault controls | The simulators refused the Operations Journey API: their switch answered loopback callers only, and in the cluster that API is another pod (first production-certificate run, 26-OCT-2026) | Fixed: the switch also accepts the admin key from Key Vault (`simulator-admin-key`). Seen again: compare the simulators' `Simulator__AdminKey` with the API's `OPERATIONS_JOURNEY_SIMULATOR_ADMIN_KEY` |
| Simulator & fault controls shows no platform APIs | Expected: the fault routes need Toxiproxy on the same machine, which runs on a developer's laptop only (the page says so) | Nothing; see Known gaps |
| `UPGRADE FAILED: conflict ... with "admissionsenforcer" ... namespaceSelector` (re-run of Demo up) | AKS rewrites every webhook's namespace selector after install; Helm 4's server-side apply then refuses the chart's own value on the next run (first re-run on a running cluster, 26-OCT-2026) | Fixed: the add-ons are installed with `--force-conflicts` (AKS adds its exclusions again). Run Demo up again |
| `Terraform left resources in rg-ewp-demo` (Demo down) | Something not in the state (made by hand, or a lost state) | The run deleted them; find out where they came from |

## Known gaps

| Gap | Effect | Closed by |
|---|---|---|
| No fault routes in the cloud | Cutting, slowing or hanging an API for its callers (Toxiproxy) is laptop-only; in the cluster the callers use direct addresses | Toxiproxy in the cluster with every caller routed through it: a later step if wanted |
| The AKS API server and the Key Vault have public endpoints | Reachable from the internet; every call still needs an Entra token with a role on that one resource, and no local account or key exists | Private cluster and private endpoints with self-hosted runners in a VNet; out of scope for the demo |
| `azure-demo-teardown` has no reviewer, yet signs in as the full deployment identity | A workflow on `main` asking for it could also create (Threat-Model R-19) | Accepted; production would give teardown its own identity with delete rights only |
| GitHub pauses scheduled workflows after 60 days without repository activity | The watchdog would stop | The Actions tab offers to enable it again; the budget e-mails still warn |
| No Azure Firewall; Strimzi's pods not limited outgoing | Every EWP, Partner Bank, Temporal and database-job pod may only reach what it calls ([Kubernetes.md](Kubernetes.md) K12), but the Kafka broker and its operators can still open connections out, and there is no FQDN filtering at the edge | An egress rule for the Kubernetes API server; Azure Firewall (or Cilium FQDN policies, a paid AKS feature) for production |
| One Kafka node, Temporal's development server | No replication; Temporal has no TLS or authorization on its frontend (Threat-Model R-16) | Enough for a demo; production: 3 brokers, Temporal Cloud |
| The database job first gives the roles the public development passwords | For seconds, on a private server with empty databases (Threat-Model R-20) | Accepted; the job sets Key Vault's passwords before any service starts |
| One Envoy proxy, one gateway IP, single-zone nodes | No high availability | Enough for a demo; production: 2+ proxies across zones |
| The watchdog's hourly check is a GitHub deployment each time | The `azure-demo-teardown` environment lists many deployments | Cosmetic |