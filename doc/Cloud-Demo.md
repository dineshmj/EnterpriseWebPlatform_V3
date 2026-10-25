# Cloud demo (Azure)

How a demo of EWP appears in Azure, runs for a few hours, and disappears again. **Demo up** makes
everything in one approved run, **Demo down** removes all of it, and a **watchdog** removes any
demo that was forgotten.

In short: **one Terraform root module ([deploy/terraform/demo](../deploy/terraform/demo)) makes
the network, an AKS cluster, a PostgreSQL server, a Key Vault, a public IP and the two domains'
address records. A script then puts the cluster's way in on top (Envoy Gateway, cert-manager,
Let's Encrypt). Nothing is kept between demos except what the bootstrap made
([Azure.md](Azure.md)). No secret is stored in GitHub, the Terraform state or the repository.**

Step 7 of the cloud demo, in two parts:

| Part | What | Status |
|---|---|---|
| **7a** | Azure resources (Terraform), cluster add-ons and the public gateway, the three workflows | Built 25-OCT-2026, not yet run against Azure |
| 7b | The platform: Kafka, Temporal, the 12 databases and their users, the secrets in Key Vault, the EWP and Partner Bank charts | Next |

## What Demo up makes

Everything in `rg-ewp-demo` (the bootstrap's throw-away group), in the region the run chooses
(CD13), plus two records in the permanent DNS zones:

| Resource | Name | Notes |
|---|---|---|
| Virtual network | `vnet-ewp-demo` (10.224.0.0/16) | Subnets `snet-aks-nodes` (/20) and `snet-postgres` (/24, delegated to PostgreSQL) |
| AKS cluster | `aks-ewp-demo` | Free tier, Kubernetes 1.34, `node_count` x `Standard_D4as_v5` (default 2), Azure Linux. Azure CNI overlay with Cilium (pods 10.244.0.0/16), Entra ID sign-in only, workload identity, Key Vault CSI driver. AKS makes its own node group `rg-ewp-demo-nodes` and deletes it with the cluster |
| Managed identity | `id-ewp-demo-aks` | The cluster's control plane. Network Contributor on its subnet and on the gateway's public IP |
| PostgreSQL Flexible Server | `psql-ewp-demo-<5 random>` | Version 18, Burstable B2s, 32 GB, no public access, TLS required. Admin `ewpadmin` |
| Private DNS zone | `ewp-demo.private.postgres.database.azure.com` | Resolves the server inside the VNet only |
| Key Vault | `kv-ewp-demo-<5 random>` | RBAC only, 7-day soft delete. Holds `postgres-admin-password` (7b adds the platform's secrets) |
| Public IP | `pip-ewp-demo-gateway` | Static, Standard. Envoy's Service binds it by name |
| DNS records | `*.<EWP domain>`, `*.<Partner Bank domain>` | A records to the public IP, TTL 5 minutes |
| Role assignments | | The workflow: AKS RBAC Cluster Admin on the cluster, Key Vault Secrets Officer on the vault (both allowed by the bootstrap's condition, [Azure.md](Azure.md) A4) |

In the cluster ([demo-cluster-addons.sh](../.github/scripts/demo-cluster-addons.sh), values in
[deploy/cluster-addons](../deploy/cluster-addons)):

| Add-on | Version | Namespace | Why |
|---|---|---|---|
| Envoy Gateway | 1.9.2 (Gateway API 1.6) | `envoy-gateway-system`; proxies in `gateway-system` | HTTPRoute + BackendTLSPolicy for EWP, TLSRoute pass-through for the Partner Bank (7b) |
| cert-manager | 1.21.2 | `cert-manager` | Let's Encrypt for the browser; EWP's private CA for TLS between pods |
| trust-manager | 0.25.0 | `cert-manager` | The trust bundles in every EWP namespace |
| [ewp-cluster](../deploy/helm/ewp-cluster) | | `cert-manager` | EWP's private CA and bundles ([Kubernetes.md](Kubernetes.md)) |
| [ewp-gateway](../deploy/helm/ewp-gateway) | | `gateway-system` | Envoy bound to the public IP, the Gateway, Let's Encrypt issuers, one certificate for every EWP host, the gateway check |

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
   each allow `node_count` x 4 (8 for the default two nodes; about 16 for step 7b, so ask for
   20). Raising a quota is free (the row's pencil). The identity cannot read quotas, so a
   shortfall shows only as an apply error. State on 25-OCT-2026: Central India 20 / 20 requested
   (Total Regional approved, DASv5 under review), Australia East 10 / 10.
5. **Merge this step** to `main`: the workflows run only from there.

## Running a demo

1. **Actions → Demo up → Run workflow** (branch `main`). Choose the **region** closest to the
   audience (`centralindia` from India, `australiaeast` for Australia). For test runs choose
   `letsencrypt-staging`; for the real demo keep `letsencrypt-production`.
2. **Approve** the `azure-demo` deployment. The run takes about 20-30 minutes: AKS and
   PostgreSQL take the longest.
3. The run ends with the **gateway check**: `https://gateway-check.<EWP domain>/` answers
   `EWP gateway OK`, and `http://` redirects to `https://`. Its summary shows the start time and
   the gateway's address.
4. When finished: **Actions → Demo down → Run workflow**. No approval needed. It takes about
   10-15 minutes and ends with "rg-ewp-demo is empty".

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
| 2 nodes `Standard_D4as_v5` (Linux) | 0.22 (0.111 each) | 0.43 (0.216 each) |
| PostgreSQL B2s (+ 32 GB storage, about 0.006) | 0.10 | 0.11 |
| 2 OS disks (P6), load balancer, public IP | about 0.05 | about 0.06 |
| AKS control plane (Free tier), Key Vault, DNS queries | about 0 | about 0 |
| **Total** | **about USD 0.37 (₹33)** | **about USD 0.60 (₹53)** |

A 3-hour demo costs about USD 1.10 (₹100) in Central India, USD 1.80 (₹160) in Australia East.
The 6-hour watchdog limit caps a forgotten one at about ₹200 / ₹320 against the ₹420 monthly
budget. Step 7b adds nodes (the whole platform needs about three to four).

## When something goes wrong

| What you see | Meaning | What to do |
|---|---|---|
| `... is not delegated to Azure DNS` (first step) | The registrar still lists other name servers | Set the zone's four name servers at the registrar ([Azure.md](Azure.md), Domains), wait, run again |
| `OperationNotAllowed ... quota` in `terraform apply` | Too few vCPUs in the region | Raise the quota (Before the first run, 4), or lower `node_count` |
| `AuthorizationFailed` on a role assignment | The bootstrap's condition refused a role or principal type | The role must be on the bootstrap's list, with `principal_type = "ServicePrincipal"` |
| `403` writing the Key Vault secret | The new role assignment had not reached Key Vault yet | Run Demo up again (it continues from the state) |
| `waiting for the cluster role to apply` many times | AKS RBAC assignments take a few minutes | Nothing, unless it gives up after 10 minutes |
| `The gateway's Service has address ..., not ...` | Azure did not bind Terraform's IP: the annotations or the cluster identity's right on the IP | `kubectl describe service -n gateway-system` shows Azure's event |
| `certificate/ewp-public-tls` not Ready | Let's Encrypt could not reach `http://<host>/.well-known/acme-challenge/...` | The run prints the challenges. Usually DNS (delegation, or a resolver's cached "no such name"); with production, possibly the weekly limit (use staging) |
| Demo up failed half-way | What it made stays (and costs) | Run **Demo down**, or let the watchdog remove it within 6 hours |
| `Terraform left resources in rg-ewp-demo` (Demo down) | Something not in the state (made by hand, or a lost state) | The run deleted them; find out where they came from |

## Known gaps

| Gap | Effect | Closed by |
|---|---|---|
| Not yet run against Azure | Faults only Azure shows (provider 5.x details, the condition and the role assignments, AKS binding an IP outside its node group, PostgreSQL 18 and B2s in the chosen region) are still to find | The first Demo up run |
| The AKS API server and the Key Vault have public endpoints | Reachable from the internet; every call still needs an Entra token with a role on that one resource, and no local account or key exists | Private cluster and private endpoints with self-hosted runners in a VNet; out of scope for the demo |
| `azure-demo-teardown` has no reviewer, yet signs in as the full deployment identity | A workflow on `main` asking for it could also create (Threat-Model R-19) | Accepted; production would give teardown its own identity with delete rights only |
| GitHub pauses scheduled workflows after 60 days without repository activity | The watchdog would stop | The Actions tab offers to enable it again; the budget e-mails still warn |
| No egress NetworkPolicy, no Azure Firewall | Pods can open connections to the internet (as before, [Kubernetes.md](Kubernetes.md)) | 7b adds egress rules with the environment's addresses |
| One Envoy proxy, one gateway IP, single-zone nodes | No high availability | Enough for a demo; production: 2+ proxies across zones |
| The watchdog's hourly check is a GitHub deployment each time | The `azure-demo-teardown` environment lists many deployments | Cosmetic |