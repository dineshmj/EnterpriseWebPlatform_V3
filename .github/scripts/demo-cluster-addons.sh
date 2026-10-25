#!/usr/bin/env bash
# ┌─ :: ARCHITECTURE + SECURITY :: From an empty AKS cluster to one the charts can use ────────────
# │ Why : doc/Kubernetes.md "What a cluster needs first": a Gateway API implementation, cert-
# │       manager + trust-manager, EWP's private CA and a Gateway with a public certificate. In
# │       the demo all of it is made per run, so it must be one repeatable script.
# │ How : In dependency order, each step idempotent (helm upgrade --install, kubectl apply):
# │         1. Envoy Gateway (brings the Gateway API CRDs)
# │         2. cert-manager (Gateway API support needs those CRDs at start-up)
# │         3. trust-manager
# │         4. deploy/helm/ewp-cluster (private CA + trust bundles; on a staging run the bundle
# │            also trusts Let's Encrypt's staging roots, which the pods meet calling the IDP)
# │         5. namespace gateway-system + deploy/helm/ewp-gateway (Envoy bound to the public IP,
# │            Gateway with EWP's and the Partner Bank's listeners, Let's Encrypt issuers, the
# │            two public certificates, gateway check)
# │       then waits for the public IP on the gateway and for both certificates.
# │       Chart versions are exact; the charts come from their publishers' OCI registries.
# │       Needs: kubectl (KUBECONFIG set), helm; EWP_DOMAIN, PARTNER_DOMAIN, GATEWAY_IP_NAME,
# │       GATEWAY_IP, DEMO_GROUP, CERT_ISSUER.
# │ Ref : doc/Cloud-Demo.md | doc/Kubernetes.md | deploy/cluster-addons/*.yaml
# └────────────────────────────────────────────────────────────────────────────────────────────────
set -euo pipefail

: "${EWP_DOMAIN:?}" "${PARTNER_DOMAIN:?}" "${GATEWAY_IP_NAME:?}" "${GATEWAY_IP:?}" "${DEMO_GROUP:?}" "${CERT_ISSUER:?}"

ENVOY_GATEWAY_VERSION='v1.9.2'
CERT_MANAGER_VERSION='v1.21.2'
TRUST_MANAGER_VERSION='v0.25.0'

repo="$(cd "$(dirname "$0")/../.." && pwd)"
addons="$repo/deploy/cluster-addons"

step() { echo; echo "== $1"; }

# RESIL: a webhook (cert-manager's, Envoy Gateway's) can refuse for a few seconds after its
# Deployment reports ready; the objects that need it are applied up to three times.
retry() {
  for attempt in 1 2 3; do
    "$@" && return 0
    [ "$attempt" -lt 3 ] && { echo "retrying in 20 s ($attempt/3)"; sleep 20; }
  done
  return 1
}

step "Envoy Gateway $ENVOY_GATEWAY_VERSION"
helm upgrade --install eg oci://docker.io/envoyproxy/gateway-helm --version "$ENVOY_GATEWAY_VERSION" \
  --namespace envoy-gateway-system --create-namespace -f "$addons/envoy-gateway.yaml" --wait --timeout 10m

step "cert-manager $CERT_MANAGER_VERSION"
helm upgrade --install cert-manager oci://quay.io/jetstack/charts/cert-manager --version "$CERT_MANAGER_VERSION" \
  --namespace cert-manager --create-namespace -f "$addons/cert-manager.yaml" --wait --timeout 10m

step "trust-manager $TRUST_MANAGER_VERSION"
helm upgrade --install trust-manager oci://quay.io/jetstack/charts/trust-manager --version "$TRUST_MANAGER_VERSION" \
  --namespace cert-manager -f "$addons/trust-manager.yaml" --wait --timeout 10m

step "ewp-cluster (private CA, trust bundles)"
# SEC: staging certificates are untrusted by design; only a staging run adds their roots, and
# only to the full bundle (never to the private-root bundle the Gateway checks pods against).
extra_roots="$(mktemp)"
if [ "$CERT_ISSUER" = "letsencrypt-staging" ]; then
  cat "$repo"/deploy/demo-platform/letsencrypt-roots/staging-root-x*.pem > "$extra_roots"
fi
retry helm upgrade --install ewp-cluster "$repo/deploy/helm/ewp-cluster" --set-file "extraRoots=$extra_roots" --wait --timeout 5m

# ┌─ SEC ────────────────────────────────────────────────────────────────────────────────────────
# │ Only namespaces carrying ewp.dev/gateway-routes=true may attach routes to the https listener
# │ (deploy/helm/ewp-gateway). Step 7b labels EWP's namespace the same way.
# └──────────────────────────────────────────────────────────────────────────────────────────────
step "gateway-system + ewp-gateway (issuer $CERT_ISSUER)"
kubectl create namespace gateway-system --dry-run=client -o yaml | kubectl apply -f -
kubectl label namespace gateway-system ewp.dev/gateway-routes=true --overwrite
retry helm upgrade --install ewp-gateway "$repo/deploy/helm/ewp-gateway" --namespace gateway-system \
  -f "$repo/deploy/helm/ewp-gateway/values-demo.yaml" \
  --set "domain=$EWP_DOMAIN" \
  --set "partnerBank.domain=$PARTNER_DOMAIN" \
  --set "publicIp.name=$GATEWAY_IP_NAME" \
  --set "publicIp.resourceGroup=$DEMO_GROUP" \
  --set "certificates.issuer=$CERT_ISSUER" \
  --wait --timeout 5m

# ┌─ RESIL ──────────────────────────────────────────────────────────────────────────────────────
# │ Envoy Gateway makes the proxy's Service a moment after the Gateway; Azure then binds the
# │ public IP. Wait for exactly Terraform's address: any other means the annotations or the
# │ cluster identity's right on the IP are wrong, and DNS would point elsewhere.
# └──────────────────────────────────────────────────────────────────────────────────────────────
step "Waiting for the gateway on $GATEWAY_IP"
selector="gateway.envoyproxy.io/owning-gateway-name=ewp-gateway"
for attempt in $(seq 1 60); do
  address="$(kubectl get service -n gateway-system -l "$selector" -o jsonpath='{.items[0].status.loadBalancer.ingress[0].ip}' 2>/dev/null || true)"
  [ "$address" = "$GATEWAY_IP" ] && break
  [ "$attempt" -eq 60 ] && { echo "The gateway's Service has address '${address:-none}', not $GATEWAY_IP."; kubectl describe service -n gateway-system -l "$selector" || true; exit 1; }
  sleep 10
done
echo "gateway on $address"

step "Waiting for the public certificates (Let's Encrypt, HTTP-01)"
for certificate in ewp-public-tls partner-bank-public-tls; do
  if ! kubectl wait "certificate/$certificate" -n gateway-system --for=condition=Ready --timeout=15m; then
    kubectl describe certificate "$certificate" -n gateway-system || true
    kubectl get certificaterequests,orders.acme.cert-manager.io,challenges.acme.cert-manager.io -n gateway-system -o wide || true
    kubectl describe challenges.acme.cert-manager.io -n gateway-system || true
    exit 1
  fi
  echo "$certificate ready"
done

# NOTE: only now: until the certificate's Secret exists, the https listener refers to nothing.
kubectl wait gateway/ewp-gateway -n gateway-system --for=condition=Programmed --timeout=5m