# Kubernetes deployment (Helm)

How EWP's 35 images ([Containers.md](Containers.md)) run in a Kubernetes cluster: two Helm
charts under [deploy/helm](../deploy/helm), what a cluster must have before them, and every
secret an environment must create.

In short: **one chart deploys the whole platform into one namespace, the same way for every
service: non-root, read-only, no Kubernetes token, TLS on every connection between pods,
incoming connections only from the components that call it. The chart holds no secret: it names
the Kubernetes Secrets it reads, and a missing one stops the pod with that name.**

| Chart | Installed | What it makes |
|---|---|---|
| [ewp-cluster](../deploy/helm/ewp-cluster) | Once per cluster | EWP's private CA (cert-manager ClusterIssuer `ewp-internal-ca`) and two trust bundles (trust-manager), copied into every namespace labelled `ewp.dev/trust=true` |
| [ewp](../deploy/helm/ewp) | Once per environment (namespace) | Per component: Deployment, Service, Certificate, NetworkPolicy, PodDisruptionBudget (2+ instances); per browser-facing component: HTTPRoute + BackendTLSPolicy; the Platform Health list (ConfigMap); the two volumes |

## Decisions

| # | Decision | Why |
|---|---|---|
| K1 | **One chart, a list of components** ([values.yaml](../deploy/helm/ewp/values.yaml), `components:`); one template per Kubernetes kind ranges over it. An environment overrides only what differs (`values-<environment>.yaml`) | The 35 services are deployed the same way; that sameness is reviewed once, in the templates. A component's entry says only what is its own: settings, callers, secrets |
| K2 | A component's key is its **Service name** and its image name without `ewp-`; services call each other by that short name (`https://payments-api`) | One EWP per namespace; the addresses are the same in every environment, only the public domain changes |
| K3 | **TLS between pods from a private CA** (cert-manager), not a service mesh: each Service gets an ECDSA P-256 certificate for its names (90 days, renewed 30 days before, new key each time). .NET: `ASPNETCORE_URLS=https://+:8443` with the PEM files (`Kestrel__Certificates__Default__*`); Node: `<PREFIX>_PLAIN_HTTP=false` with the PKCS#12 keystore (`<PREFIX>_TLS_PFX_*`). Callers trust the private root: .NET through `SSL_CERT_FILE` (public roots + private root), Node through `NODE_EXTRA_CA_CERTS` | The BFFs and workers insist on https API addresses ([Containers.md](Containers.md), the gap this closes); no code change, no sidecar per pod, no extra memory. The .NET Kafka workers serve only probes and stay on http 8080 |
| K4 | **Gateway API** for the browser's way in (HTTPRoute per public component), the Gateway re-encrypting to the pod and checking its certificate (**BackendTLSPolicy**) | The successor of Ingress (ingress-nginx is retired); no plain HTTP on the pod network even behind the Gateway. The Gateway itself, with the public certificate, belongs to the environment (step 6/7) |
| K5 | **Secrets by name only.** A component's own secret settings are keys of the Secret `ewp-<component>`, named exactly like the setting; shared ones (Data Protection, keystore password) are their own Secrets; files (signing and client certificates) are Secrets mounted read-only. Who fills them is the environment's business (Azure Key Vault + CSI driver on AKS; a script for a local cluster) | Nothing secret in the repository, the chart or Helm's release history; one chart for every secret store. A missing key fails the pod with `CreateContainerConfigError` naming it |
| K6 | **Default-deny NetworkPolicy**; each component admits only its `allowFrom` list, the Gateway's namespace (public components) and the Operations Journey API (Platform Health asks every readiness) | A compromised pod cannot even reach an API it never calls (defence in depth behind the token checks) |
| K7 | **Same pod hardening for all:** `runAsNonRoot` with the image's user (1654 / 65532), read-only root file system with an emptyDir `/tmp`, no privilege escalation, all capabilities dropped, `RuntimeDefault` seccomp, no service account token, no service-link variables | The images are built for it ([Containers.md](Containers.md), run-time contract); Pod Security Standard "restricted" |
| K8 | **Probes** on `/health/live` (start-up: up to 2.5 minutes; liveness) and `/health/ready` (readiness; the simulators: `/health`), with a `Host` header | `AllowedHosts` would refuse the pod IP the kubelet sends otherwise; readiness includes the database, so an instance without it stops getting traffic |
| K9 | **Platform Health's list is generated** from the components (`global.platformHealth.order`) | A list written by hand drifts from what is deployed |
| K10 | **Required values refuse to render**: the domain, the image tag (the full commit SHA), Kafka, Temporal, the Partner Bank addresses | A half-configured release must not reach a cluster; CI checks that each refusal still happens |

## What a cluster needs first

| Prerequisite | Why |
|---|---|
| Kubernetes **1.34+** | The version the charts are checked against (CI, kubeconform); `Chart.yaml` refuses older clusters |
| **cert-manager** and **trust-manager** (trust namespace `cert-manager`), then `helm install ewp-cluster deploy/helm/ewp-cluster` | K3 |
| A **Gateway API** implementation with **BackendTLSPolicy** (Gateway API 1.4+), and a Gateway with an https listener and the public certificate | K4 |
| A CNI that enforces **NetworkPolicy** (Azure CNI with Cilium on AKS) | K6 |
| The namespace labelled `ewp.dev/trust=true` | trust-manager copies the bundles only there |
| PostgreSQL with the 12 databases and their users (`db/`), Kafka (SASL_SSL, the users and ACLs of `kafka/`), Temporal | The chart deploys EWP's services, not their infrastructure |
| The **Partner Bank**, from its own chart in its own namespace ([its doc/Kubernetes.md](https://github.com/dineshmj/EnterpriseWebPlatform_V3_PartnerBank/blob/main/doc/Kubernetes.md)): TLS passed through at the same Gateway (four TLS listeners in Passthrough mode), the home PC's port numbers on `partner-auth`, `partner-api`, `partner-products` `.<Partner Bank domain>`, a registrable domain of its own ([Azure.md](Azure.md) A8) | `global.partnerBank`: EWP calls it like any outside client, through the Gateway, with its client certificate |

## Installing

```text
helm install ewp-cluster deploy/helm/ewp-cluster                      # once per cluster
kubectl create namespace ewp && kubectl label namespace ewp ewp.dev/trust=true
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
| `ewp-idp` | `ConnectionStrings__IdentityDbConnection`, `SigningCredential__CertificatePassword`, and `ClientSecrets__<client id>` for each of the 21 confidential clients ([values.yaml](../deploy/helm/ewp/values.yaml), `idp.secretEnv`) |
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
| `ewp-<simulator>` (the 3 simulators) | `Simulator__ApiKey` - equal to its caller's `ApiKey` |

Shared Secrets and files:

| Secret | Keys | Used by |
|---|---|---|
| `ewp-data-protection` | `certificate.pfx`, `password` | IDP, Shell, the 4 .NET BFFs: encrypts their Data Protection key rings at rest (`DataProtection__CertificatePath` / `__CertificatePassword`, required outside Development) |
| `ewp-tls-keystore-password` | `password` | cert-manager (writes the Node services' PKCS#12 keystores) and the Node services (read them) |
| `ewp-idp-signing` | `signing.pfx` | IDP: the token-signing certificate |
| `ewp-idp-client-keys` | `SelfService.Web.ClientID.jwk`, `SelfService.JourneyApi.ClientID.jwk` | IDP: the private_key_jwt clients' PUBLIC keys |
| `ewp-partner-pki` | `ewp-partner-client.pfx` (+ `.password`), `ewp-request-signing.pfx` (+ `.password`), `ewp-trust-root.cer` | Partner Products API: mutual TLS and signed instructions to the Partner Bank |
| ConfigMap `ewp-kafka-trust` | `ca.crt` | Every Kafka client: the Kafka trust root |

The `<component>-tls` Secrets are made by cert-manager, the `ewp-internal-ca` and
`ewp-trust-bundle` ConfigMaps by trust-manager.

## Checking the charts

[check-charts.sh](../deploy/helm/check-charts.sh) - in CI (job "Helm charts", part of
**CI result**, [CI.md](CI.md)) and on a laptop with `helm` on the PATH: `helm lint --strict` and
`helm template` of both charts with every `values-*.yaml`, kubeconform (strict) against the
Kubernetes 1.34 schemas and the cert-manager / trust-manager / Gateway API schemas, and a check
that each required value still stops a render. Nothing here needs a cluster or Docker.

## Known gaps

| Gap | Effect | Planned fix |
|---|---|---|
| Not yet installed on a cluster: the charts are checked by rendering and schemas only | Problems only a running cluster shows (the Gateway's BackendTLSPolicy support, a probe timing, a missing grant) are still to find | The first demo run (steps 5-7: Azure bootstrap, domains, Terraform + workflows) |
| Kestrel and Node read their certificate at start-up only | A renewed certificate is used after the next restart | 30 days' margin; a demo cluster lives hours. Longer-lived: a reloader that restarts on Secret change, or certificate reload in the hosts |
| No egress NetworkPolicy | A compromised pod can open connections out of the namespace | With the environment's addresses (PostgreSQL, Kafka, Temporal, the Partner Bank, the Gateway's public IP) at the Terraform step |
| TLS between pods is server-authenticated, not mutual | A caller proves who it is with its token (every API checks it), not with a certificate | Accepted (Threat-Model R-9); a mesh with mTLS remains the production option |
| PostgreSQL schemas and users, Kafka users and ACLs, Temporal are not deployed by the chart | An environment prepares them first | The Terraform step (managed PostgreSQL + a schema Job running the full scripts; Kafka and Temporal) |
| The Audit API's checkpoints and the Documents Management API's documents are on ReadWriteOnce volumes | One instance each | A ReadWriteMany class (Azure Files) or object storage, then more instances |
| The Notifications API pushes over SignalR without a backplane | One instance ([Containers.md](Containers.md), known gaps) | Redis or Azure SignalR Service |