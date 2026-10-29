# Kubernetes deployment (Helm)

How EWP's 35 images ([Containers.md](Containers.md)) run in a Kubernetes cluster: the Helm
charts under [deploy/helm](../deploy/helm), what a cluster must have before them, and every
secret an environment must create.

In short: **one chart deploys the whole platform into one namespace, the same way for every
service: non-root, read-only, no Kubernetes token, TLS on every connection between pods,
incoming connections only from the components that call it. The chart holds no secret: it names
the Kubernetes Secrets it reads, and a missing one stops the pod with that name.**

| Chart | Installed | What it makes |
|---|---|---|
| [ewp-cluster](../deploy/helm/ewp-cluster) | Once per cluster | EWP's private CA (cert-manager ClusterIssuer `ewp-internal-ca`) and two trust bundles (trust-manager), copied into every namespace labelled `ewp.dev/trust=true` |
| [ewp-gateway](../deploy/helm/ewp-gateway) | Once per cluster, in `gateway-system` | The public way in: Envoy Gateway's proxy bound to the environment's public IP, the Gateway (`http` for Let's Encrypt and the redirect, `https` with the public certificate), the Let's Encrypt issuers, one certificate for every EWP host, and a fixed-answer gateway check ([Cloud-Demo.md](Cloud-Demo.md)) |
| [ewp](../deploy/helm/ewp) | Once per environment (namespace) | Per component: Deployment, Service, Certificate, NetworkPolicy, PodDisruptionBudget (2+ instances); per browser-facing component: HTTPRoute + BackendTLSPolicy; the Platform Health list (ConfigMap); the two volumes |
| [ewp-infra](../deploy/helm/ewp-infra) | Once per environment, namespace `ewp-infra` (the cloud demo) | Kafka through Strimzi (one KRaft node, TLS + SCRAM, the laptop's users, ACLs and topics), Temporal's development server, the job that makes the 12 databases and their users from the repository's scripts ([Cloud-Demo.md](Cloud-Demo.md) CD16-CD18) |
| [keyvault-secrets](../deploy/helm/keyvault-secrets) | Once per namespace (the cloud demo) | The Kubernetes Secrets the other charts name, synced from Azure Key Vault by the Secrets Store CSI driver with workload identity ([Cloud-Demo.md](Cloud-Demo.md) CD14) |

## Decisions

| # | Decision | Why |
|---|---|---|
| K1 | **One chart, a list of components** ([values.yaml](../deploy/helm/ewp/values.yaml), `components:`); one template per Kubernetes kind ranges over it. An environment overrides only what differs (`values-<environment>.yaml`) | The 35 services are deployed the same way; that sameness is reviewed once, in the templates. A component's entry says only what is its own: settings, callers, secrets |
| K2 | A component's key is its **Service name** and its image name without `ewp-`; services call each other by that short name (`https://payments-api`) | One EWP per namespace; the addresses are the same in every environment, only the public domain changes |
| K3 | **TLS between pods from a private CA** (cert-manager), not a service mesh: each Service gets an ECDSA P-256 certificate for its names (90 days, renewed 30 days before, new key each time). .NET: `ASPNETCORE_URLS=https://+:8443` with the PEM files (`Kestrel__Certificates__Default__*`); Node: `<PREFIX>_PLAIN_HTTP=false` with the PKCS#12 keystore (`<PREFIX>_TLS_PFX_*`). Callers trust the private root: .NET through `SSL_CERT_FILE` (public roots + private root), Node through `NODE_EXTRA_CA_CERTS` (the same bundle, added to Node's own roots) | The BFFs and workers insist on https API addresses ([Containers.md](Containers.md), the gap this closes); no code change, no sidecar per pod, no extra memory. The .NET Kafka workers serve only probes and stay on http 8080 |
| K4 | **Gateway API** for the browser's way in (HTTPRoute per public component), the Gateway re-encrypting to the pod and checking its certificate (**BackendTLSPolicy**) | The successor of Ingress (ingress-nginx is retired); no plain HTTP on the pod network even behind the Gateway. The Gateway itself, with the public certificate, belongs to the environment (step 6/7) |
| K5 | **Secrets by name only.** A component's own secret settings are keys of the Secret `ewp-<component>`, named exactly like the setting; shared ones (Data Protection, keystore password) are their own Secrets; files (signing and client certificates) are Secrets mounted read-only. Who fills them is the environment's business (Azure Key Vault + CSI driver on AKS; a script for a local cluster) | Nothing secret in the repository, the chart or Helm's release history; one chart for every secret store. A missing key fails the pod with `CreateContainerConfigError` naming it |
| K6 | **Default-deny NetworkPolicy**; each component admits only its `allowFrom` list, the Gateway's namespace (public components) and the Operations Journey API (Platform Health asks every readiness) | A compromised pod cannot even reach an API it never calls (defence in depth behind the token checks) |
| K7 | **Same pod hardening for all:** `runAsNonRoot` with the image's user (1654 / 65532), read-only root file system with an emptyDir `/tmp`, no privilege escalation, all capabilities dropped, `RuntimeDefault` seccomp, no service account token, no service-link variables | The images are built for it ([Containers.md](Containers.md), run-time contract); Pod Security Standard "restricted" |
| K8 | **Probes** on `/health/live` (start-up: up to 2.5 minutes; liveness) and `/health/ready` (readiness; the simulators: `/health`), with a `Host` header | `AllowedHosts` would refuse the pod IP the kubelet sends otherwise; readiness includes the database, so an instance without it stops getting traffic |
| K9 | **Platform Health's list is generated** from the components (`global.platformHealth.order`) | A list written by hand drifts from what is deployed |
| K10 | **Required values refuse to render**: the domain, the image tag (the full commit SHA), Kafka, Temporal, the Partner Bank addresses, the egress addresses (the Gateway's IP, PostgreSQL's subnet) | A half-configured release must not reach a cluster; CI checks that each refusal still happens |
| K11 | **An image may be pinned by digest** (`global.image.digests.<component>`: `repository:tag@digest`) | The cloud demo pins each image to the digest its `main` tag has at install time ([Cloud-Demo.md](Cloud-Demo.md) CD20): images are built per changed folder, so no one commit SHA names them all |
| K12 | **Outgoing connections limited too** (`global.egress`): every EWP pod may reach the cluster DNS, the components that admit it (the mirror of their `allowFrom`; the Operations Journey API: all), and - derived from its own entry, no second list - PostgreSQL's subnet (a connection string), Kafka's brokers (a `kafka` entry), Temporal (`TEMPORAL_ADDRESS`), the Gateway (a public EWP or Partner Bank address: the IDP, the Partner Bank, the IDP's back-channel logout to the clients). The Gateway twice: its pods (Cilium hands a connection to the load balancer's IP straight to them) and its public IP on the listener ports. ewp-infra: Temporal and the Key Vault sync pods connect nowhere, the database job only to DNS and PostgreSQL | A compromised pod cannot download a second stage, send data to an outside server, scan the VNet or reach the cloud's metadata service - only what its own entry says it calls. Kubernetes NetworkPolicy has no host names, so the outside addresses come from Terraform (`gateway_ip`, `postgres_cidr`) |
| K13 | **Feature flags from a ConfigMap, or LaunchDarkly** (`global.featureFlags`): the components with `featureFlags: true` (the Payments, Accounts, Customer Onboarding, Documents Management and Partner Products APIs) mount the ConfigMap `ewp-feature-flags` as a directory and read `FeatureFlags__File`; with `launchDarkly.enabled` they also get `EWP_LAUNCHDARKLY_SDK_KEY` from the Secret `ewp-launchdarkly`, and their NetworkPolicy one more egress rule: TCP 443 to public addresses only (0.0.0.0/0 except 10/8, 172.16/12, 192.168/16, 169.254/16, 100.64/10) | A flag changes without a redeploy: `kubectl -n ewp edit configmap ewp-feature-flags` reaches the pods in about a minute (a directory mount is updated, a subPath one never is). LaunchDarkly publishes its IP ranges, but they change (normally with two weeks' notice) and it advises allowing its domains, which a NetworkPolicy cannot name - the trade-off is accepted for the demo only while LaunchDarkly is on (off by default; the demo turns it on only when the GitHub secret `LAUNCHDARKLY_SDK_KEY` exists). [feature-flags/README.md](../feature-flags/README.md) |

## What a cluster needs first

| Prerequisite | Why |
|---|---|
| Kubernetes **1.34+** | The version the charts are checked against (CI, kubeconform); `Chart.yaml` refuses older clusters |
| **cert-manager** and **trust-manager** (trust namespace `cert-manager`), then `helm install ewp-cluster deploy/helm/ewp-cluster` | K3 |
| A **Gateway API** implementation with **BackendTLSPolicy** (Gateway API 1.4+), and a Gateway with an https listener and the public certificate. The demo: Envoy Gateway 1.9 (Gateway API 1.6) and [ewp-gateway](../deploy/helm/ewp-gateway) | K4 |
| A CNI that enforces **NetworkPolicy** (Azure CNI with Cilium on AKS) | K6 |
| The namespace labelled `ewp.dev/trust=true` and `ewp.dev/gateway-routes=true` | trust-manager copies the bundles only to the first; the Gateway's https listener accepts routes only from the second |
| PostgreSQL with the 12 databases and their users (`db/`), Kafka (SASL_SSL, the users and ACLs of `kafka/`), Temporal | The chart deploys EWP's services, not their infrastructure. The cloud demo: [ewp-infra](../deploy/helm/ewp-infra) on Azure Database for PostgreSQL |
| The **Partner Bank**, from its own chart in its own namespace ([its doc/Kubernetes.md](https://github.com/dineshmj/EnterpriseWebPlatform_V3_PartnerBank/blob/main/doc/Kubernetes.md)): TLS passed through at the same Gateway (four TLS listeners in Passthrough mode), the home PC's port numbers on `partner-auth`, `partner-api`, `partner-products` `.<Partner Bank domain>`, a registrable domain of its own ([Azure.md](Azure.md) A8) | `global.partnerBank`: EWP calls it like any outside client, through the Gateway, with its client certificate |

## Installing

```text
helm install ewp-cluster deploy/helm/ewp-cluster                      # once per cluster
helm install ewp-gateway deploy/helm/ewp-gateway -n gateway-system ... # once per cluster
kubectl create namespace ewp
kubectl label namespace ewp ewp.dev/trust=true ewp.dev/gateway-routes=true
# create the Secrets listed below (secret store / script)
helm install ewp deploy/helm/ewp -n ewp -f deploy/helm/ewp/values-demo.yaml \
  --set global.domain=<domain> --set global.image.tag=<full commit SHA> ...
```

[values-demo.yaml](../deploy/helm/ewp/values-demo.yaml) is the short-lived cloud demo: one
instance each, environment `Test` (the Operations screen may switch the simulators), MFA on. The
values marked `<set by the pipeline>` come from Terraform's outputs.

## Secrets an environment creates

Component Secrets (`ewp-<component>`; each key = the setting's name):

| Secret | Keys |
|---|---|
| `ewp-idp` | `ConnectionStrings__IdentityDbConnection`, `SigningCredential__CertificatePassword`, and `ClientSecrets__<client id>` for each of the 21 confidential clients ([values.yaml](../deploy/helm/ewp/values.yaml), `idp.secretEnv`), plus Grafana's (`ClientSecrets__Grafana.ClientID`) while `global.observability.enabled` |
| `ewp-grafana-oidc` (namespace `observability`) | `client-secret`: Grafana's client secret at the IDP - the same value as `ewp-idp`'s `ClientSecrets__Grafana.ClientID` ([ewp-observability](../deploy/helm/ewp-observability), only with `global.observability.enabled`) |
| `ewp-shell` | `ConnectionStrings__MenuDbConnection`, `ConnectionStrings__BffStateDbConnection`, `Oidc__ClientSecret` |
| `ewp-customer-onboarding-bff-web`, `ewp-compliance-bff-web`, `ewp-accounts-bff-web`, `ewp-payments-bff-web` | `ConnectionStrings__BffStateDbConnection`, `Oidc__ClientSecret` |
| `ewp-customer-kyc-bff-web` | `KYC_BFF_CLIENT_SECRET`, `KYC_BFF_SESSION_SECRET`, `KYC_BFF_SESSION_KEY`, `KYC_BFF_DATABASE_URL` |
| `ewp-audit-bff-web`, `ewp-operations-bff-web`, `ewp-partner-products-bff-web` | `<PREFIX>_CLIENT_SECRET`, `<PREFIX>_SESSION_KEY`, `<PREFIX>_DATABASE_URL` (`AUDIT_WEB_`, `OPERATIONS_WEB_`, `PARTNER_PRODUCTS_WEB_`) |
| `ewp-customer-onboarding-api` | `ConnectionStrings__CustomerDbConnection` |
| `ewp-customer-kyc-api` | `ConnectionStrings__KycDbConnection`, `Kafka__SaslPassword` |
| `ewp-documents-management-api` | `ConnectionStrings__DocumentsManagementDbConnection`, `DocumentStorage__Encryption__Keys__key-1` |
| `ewp-compliance-api` | `ConnectionStrings__ComplianceDbConnection`, `Kafka__SaslPassword`, `ScreeningProvider__ApiKey` |
| `ewp-accounts-api` | `ConnectionStrings__AccountsDbConnection`, `Kafka__SaslPassword`, `CoreBanking__ApiKey` |
| `ewp-payments-api` | `ConnectionStrings__PaymentsDbConnection`, `Kafka__SaslPassword`, `PaymentNetwork__ApiKey` |
| `ewp-notifications-api` | `ConnectionStrings__NotificationsDbConnection` |
| `ewp-audit-api` | `ConnectionStrings__AuditDbConnection`, `Kafka__SaslPassword`, `Audit__Anchors__SigningKey` |
| `ewp-partner-products-api` | `ConnectionStrings__PartnerProductsDbConnection`, `Kafka__SaslPassword` |
| `ewp-audit-journey-api`, `ewp-operations-journey-api`, `ewp-partner-products-journey-api` | `<PREFIX>_CLIENT_SECRET` (`AUDIT_JOURNEY_`, `OPERATIONS_JOURNEY_`, `PARTNER_PRODUCTS_JOURNEY_`) |
| `ewp-customer-outbox-publisher` | `ConnectionStrings__CustomerDbConnection`, `Kafka__SaslPassword` |
| `ewp-<subscriber>` (the 8 Kafka subscribers) | `Kafka__SaslPassword`, `<Subscriber>__ClientSecret` (e.g. `PaymentsSagaReplySubscriber__ClientSecret`) |
| `ewp-partner-products-worker` | `PARTNER_PRODUCTS_WORKER_CLIENT_SECRET`, `PARTNER_PRODUCTS_WORKER_KAFKA_PASSWORD` |
| `ewp-<simulator>` (the 3 simulators) | `Simulator__ApiKey` - equal to its caller's `ApiKey`; `Simulator__AdminKey` - equal to the Operations Journey API's `OPERATIONS_JOURNEY_SIMULATOR_ADMIN_KEY` (the behaviour switch from another pod) |

Shared Secrets and files:

| Secret | Keys | Used by |
|---|---|---|
| `ewp-data-protection` | `certificate.pfx`, `password` | IDP, Shell, the 4 .NET BFFs: encrypts their Data Protection key rings at rest (`DataProtection__CertificatePath` / `__CertificatePassword`, required outside Development) |
| `ewp-tls-keystore-password` | `password` | cert-manager (writes the Node services' PKCS#12 keystores) and the Node services (read them) |
| `ewp-idp-signing` | `signing.pfx` | IDP: the token-signing certificate |
| `ewp-idp-client-keys` | `SelfService.Web.ClientID.jwk`, `SelfService.JourneyApi.ClientID.jwk` | IDP: the private_key_jwt clients' PUBLIC keys |
| `ewp-partner-pki` | `ewp-partner-client.pfx` (+ `.password`), `ewp-request-signing.pfx` (+ `.password`), `ewp-trust-root.cer` | Partner Products API: mutual TLS and signed instructions to the Partner Bank |
| ConfigMap `ewp-kafka-trust` | `ca.crt` | Every Kafka client: the Kafka trust root |
| `ewp-launchdarkly` (only with `global.featureFlags.launchDarkly.enabled`) | `sdk-key` | The 5 APIs that read feature flags: the LaunchDarkly environment's SDK key (`EWP_LAUNCHDARKLY_SDK_KEY`, K13); the chart's own ConfigMap `ewp-feature-flags` holds the flag file |

The `<component>-tls` Secrets are made by cert-manager, the `ewp-internal-ca` and
`ewp-trust-bundle` ConfigMaps by trust-manager.

## Checking the charts

[check-charts.sh](../deploy/helm/check-charts.sh) - in CI (job "Helm charts", part of
**CI result**, [CI.md](CI.md)) and on a laptop with `helm` on the PATH: `helm lint --strict` and
`helm template` of the three charts with every `values-*.yaml`, kubeconform (strict) against the
Kubernetes 1.34 schemas and the cert-manager / trust-manager / Gateway API / Envoy Gateway
schemas, a check that each required value still stops a render, and a check that the public
certificate (ewp-gateway `hosts`) lists exactly the EWP chart's public hosts. Nothing here needs
a cluster or Docker.

## Known gaps

| Gap | Effect | Planned fix |
|---|---|---|
| The EWP chart is not yet installed on a cluster: it is checked by rendering and schemas only | Problems only a running cluster shows (the Gateway's BackendTLSPolicy support, a probe timing, a missing grant) are still to find | Step 7b's Demo up installs it (built 25-OCT-2026, first run pending) |
| Kestrel and Node read their certificate at start-up only | A renewed certificate is used after the next restart | 30 days' margin; a demo cluster lives hours. Longer-lived: a reloader that restarts on Secret change, or certificate reload in the hosts |
| Strimzi's pods (Kafka broker, entity operator) are not limited outgoing | A compromised broker pod could open connections out | Its operators call the Kubernetes API server, whose address is the cluster's; an egress rule for it (`kubectl get endpoints kubernetes`) at a later step, or Kafka as a managed service |
| TLS between pods is server-authenticated, not mutual | A caller proves who it is with its token (every API checks it), not with a certificate | Accepted (Threat-Model R-9); a mesh with mTLS remains the production option |
| PostgreSQL schemas and users, Kafka users and ACLs, Temporal are not deployed by this chart | An environment prepares them first | Done for the cloud demo by [ewp-infra](../deploy/helm/ewp-infra); another environment brings its own |
| The Audit API's checkpoints and the Documents Management API's documents are on ReadWriteOnce volumes | One instance each | A ReadWriteMany class (Azure Files) or object storage, then more instances |
| The Notifications API pushes over SignalR without a backplane | One instance ([Containers.md](Containers.md), known gaps) | Redis or Azure SignalR Service |