#!/usr/bin/env bash
# ┌─ :: ARCHITECTURE + SECURITY :: The platform on the demo cluster (step 7b) ─────────────────────
# │ Why : After demo-cluster-addons.sh the cluster has a way in and a private CA, but nothing to
# │       serve. This puts the whole platform on it - in one repeatable, idempotent script that
# │       Demo up runs after every apply (and an operator can run from a laptop).
# │ How : In dependency order:
# │         1. Key Vault first (the Azure CLI's fresh sign-in can mint new tokens for 5 minutes
# │            only): demo-secrets.mjs fills both vaults (first run) and writes, per namespace,
# │            which Key Vault secret becomes which Kubernetes Secret key
# │         2. namespaces: ewp-infra (Kafka, Temporal, database job), ewp, partner-bank - with
# │            the labels the trust bundles and the Gateway's listeners select on
# │         3. Strimzi operator (watches ewp-infra only); the keyvault-secrets chart in each
# │            namespace syncs the Secrets (CSI driver, workload identity); waits until all exist
# │         4. ewp-infra: Kafka (users, ACLs, topics), Temporal, the database job (the repository's
# │            SQL scripts as a ConfigMap) - waits for all of them
# │         5. the Partner Bank: its chart from its own repository (PARTNER_CHART), browser ports
# │            ending at the Gateway, its PKI's root for the Gateway, Let's Encrypt's roots for its
# │            own calls
# │         6. EWP: the chart with every image pinned to the digest its "main" tag has now
# │       then checks from outside: the IDP's and the Partner Bank's discovery documents through
# │       the Gateway; and from inside, EWP's own token at the
# │       Partner Bank over mutual TLS. Nothing secret is printed; values pass only through Key Vault.
# │       Needs: kubectl (KUBECONFIG set), helm, node, openssl, curl, az (signed in); env below.
# │ Ref : doc/Cloud-Demo.md (7b, CD14-CD18) | doc/Kubernetes.md
# └────────────────────────────────────────────────────────────────────────────────────────────────
set -euo pipefail

: "${EWP_DOMAIN:?}" "${PARTNER_DOMAIN:?}" "${CERT_ISSUER:?}" "${POD_CIDR:?}"
: "${EWP_KEY_VAULT:?}" "${PARTNER_KEY_VAULT:?}" "${TENANT_ID:?}" "${EWP_SECRETS_CLIENT_ID:?}" "${PARTNER_SECRETS_CLIENT_ID:?}"
: "${POSTGRES_HOST:?}" "${POSTGRES_ADMIN_LOGIN:?}" "${PARTNER_CHART:?}"
export EWP_KEY_VAULT PARTNER_KEY_VAULT TENANT_ID EWP_SECRETS_CLIENT_ID PARTNER_SECRETS_CLIENT_ID PARTNER_DOMAIN POSTGRES_HOST

STRIMZI_VERSION='1.2.0'
IMAGE_OWNER='dineshmj'
IMAGE_TAG='main'

repo="$(cd "$(dirname "$0")/../.." && pwd)"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
export OUT_DIR="$work"

step() { echo; echo "== $1"; }

# RESIL: as in demo-cluster-addons.sh - a webhook may refuse for a few seconds after it starts.
retry() {
  for attempt in 1 2 3; do
    "$@" && return 0
    [ "$attempt" -lt 3 ] && { echo "retrying in 20 s ($attempt/3)"; sleep 20; }
  done
  return 1
}

# Waits until the named Secrets exist in a namespace (the CSI driver writes them on mount).
wait_for_secrets() {
  local namespace="$1" list="$2" missing
  for attempt in $(seq 1 30); do
    missing=""
    for secret in $(node -e 'for (const s of JSON.parse(require("fs").readFileSync(process.argv[1])).secrets) console.log(s.name)' "$list"); do
      kubectl get secret "$secret" -n "$namespace" > /dev/null 2>&1 || missing="$missing $secret"
    done
    [ -z "$missing" ] && { echo "all Secrets of $namespace synced from Key Vault"; return 0; }
    echo "waiting for$missing ($attempt/30)"; sleep 10
  done
  kubectl describe pod -n "$namespace" -l app.kubernetes.io/name=keyvault-secrets || true
  kubectl get events -n "$namespace" --field-selector involvedObject.kind=Pod | tail -20 || true
  return 1
}

# ┌─ SEC ────────────────────────────────────────────────────────────────────────────────────────
# │ The image a tag points to NOW, by digest (anonymous pull token; the images are public). The
# │ releases then pull exactly these, whatever "main" points to later during the demo.
# └──────────────────────────────────────────────────────────────────────────────────────────────
digest_of() {
  local image="$1" token
  token="$(curl -fsS "https://ghcr.io/token?scope=repository:$IMAGE_OWNER/$image:pull" | node -e 'process.stdin.on("data", d => process.stdout.write(JSON.parse(d).token))')"
  curl -fsSI -H "Authorization: Bearer $token" \
    -H 'Accept: application/vnd.oci.image.index.v1+json, application/vnd.docker.distribution.manifest.list.v2+json, application/vnd.oci.image.manifest.v1+json, application/vnd.docker.distribution.manifest.v2+json' \
    "https://ghcr.io/v2/$IMAGE_OWNER/$image/manifests/$IMAGE_TAG" | tr -d '\r' | awk 'tolower($1) == "docker-content-digest:" { print $2 }'
}

# Writes {"global":{"image":{"tag":..., "digests":{<component>: <digest>}}}} for a chart.
image_values() {
  local out="$1"; shift
  local pairs=() component image digest
  for entry in "$@"; do
    component="${entry%%=*}"; image="${entry#*=}"
    digest="$(digest_of "$image")"
    [ -n "$digest" ] || { echo "No image $IMAGE_OWNER/$image:$IMAGE_TAG on GHCR."; return 1; }
    pairs+=("$component=$digest")
  done
  node -e '
    const digests = Object.fromEntries(process.argv.slice(3).map(p => p.split("=")));
    require("fs").writeFileSync(process.argv[1], JSON.stringify({ global: { image: { tag: process.argv[2], digests } } }, null, 2));
  ' "$out" "$IMAGE_TAG" "${pairs[@]}"
  echo "${#pairs[@]} image(s) pinned by digest"
}

step "Secrets in Key Vault"
node "$repo/.github/scripts/demo-secrets.mjs"

step "Namespaces"
for namespace in ewp-infra ewp partner-bank; do
  kubectl create namespace "$namespace" --dry-run=client -o yaml | kubectl apply -f -
done
# trust-manager copies the bundles into these two; EWP's routes may use the https listener; the
# Partner Bank's routes may use its four listeners only.
kubectl label namespace ewp-infra ewp.dev/trust=true --overwrite
kubectl label namespace ewp ewp.dev/trust=true ewp.dev/gateway-routes=true --overwrite
kubectl label namespace partner-bank ewp.dev/partner-bank-routes=true --overwrite

step "Strimzi $STRIMZI_VERSION"
helm upgrade --install strimzi oci://quay.io/strimzi-helm/strimzi-kafka-operator --version "$STRIMZI_VERSION" \
  --namespace strimzi --create-namespace -f "$repo/deploy/cluster-addons/strimzi.yaml" --wait --timeout 10m

step "Secrets: Key Vault -> namespaces"
for namespace in ewp-infra ewp partner-bank; do
  retry helm upgrade --install keyvault-secrets "$repo/deploy/helm/keyvault-secrets" --namespace "$namespace" \
    -f "$work/keyvault-secrets-$namespace.json" --wait --timeout 5m
done
for namespace in ewp-infra ewp partner-bank; do
  wait_for_secrets "$namespace" "$work/keyvault-secrets-$namespace.json"
done

step "ewp-infra: Kafka, Temporal, databases"
# Helm cannot read files outside a chart: the scripts go in as a ConfigMap (server-side apply,
# as the scripts are too large for kubectl's last-applied annotation).
scripts=(db/EwpServiceDbUsers.sql db/EwpBffStateDb.sql src/IDP/IdentityAccessDB/IdentityAccessDb.sql src/Shell/MenuDB/EwpBssShellDb.sql)
while IFS= read -r file; do scripts+=("$file"); done < <(cd "$repo" && ls src/Microservices/*/API/*/Ewp*Db.sql)
from_files=(); for file in "${scripts[@]}"; do from_files+=("--from-file=$repo/$file"); done
kubectl create configmap ewp-db-scripts -n ewp-infra "${from_files[@]}" --dry-run=client -o yaml \
  | kubectl apply --server-side --force-conflicts -f -
retry helm upgrade --install ewp-infra "$repo/deploy/helm/ewp-infra" --namespace ewp-infra \
  -f "$repo/deploy/helm/ewp-infra/values-demo.yaml" \
  --set "database.host=$POSTGRES_HOST" \
  --set "database.adminLogin=$POSTGRES_ADMIN_LOGIN" \
  --timeout 10m
revision="$(helm history ewp-infra -n ewp-infra --max 1 -o json | node -e 'process.stdin.on("data", d => process.stdout.write(String(JSON.parse(d).at(-1).revision)))')"
if ! kubectl wait "job/database-init-$revision" -n ewp-infra --for=condition=Complete --timeout=10m; then
  kubectl logs -n ewp-infra "job/database-init-$revision" --tail=40 || true
  exit 1
fi
kubectl logs -n ewp-infra "job/database-init-$revision" --tail=20
kubectl wait kafka/ewp -n ewp-infra --for=condition=Ready --timeout=15m
kubectl wait kafkauser --all -n ewp-infra --for=condition=Ready --timeout=5m
kubectl wait kafkatopic --all -n ewp-infra --for=condition=Ready --timeout=5m
kubectl rollout status deployment/temporal -n ewp-infra --timeout=5m

step "Partner Bank (chart: $PARTNER_CHART)"
# The demo PKI's root (public): the Gateway checks the Partner Bank's pods with it.
kubectl create configmap partner-bank-ca -n partner-bank --from-file="ca.crt=$work/partner-bank-ca.pem" \
  --dry-run=client -o yaml | kubectl apply -f -
# The roots of the Gateway's public certificate, for the Partner Bank's calls to its own issuer.
roots="production-isrg-root"; [ "$CERT_ISSUER" = "letsencrypt-staging" ] && roots="staging-root"
kubectl create configmap partner-bank-backchannel-roots -n partner-bank \
  --from-file="letsencrypt-root-x1.pem=$repo/deploy/demo-platform/letsencrypt-roots/$roots-x1.pem" \
  --from-file="letsencrypt-root-x2.pem=$repo/deploy/demo-platform/letsencrypt-roots/$roots-x2.pem" \
  --dry-run=client -o yaml | kubectl apply -f -
image_values "$work/partner-bank-images.json" \
  partner-auth=partner-bank-authorization-server partner-api=partner-bank-api partner-products=partner-bank-web
helm upgrade --install partner-bank "$PARTNER_CHART" --namespace partner-bank \
  -f "$PARTNER_CHART/values-demo.yaml" -f "$work/partner-bank-images.json" \
  --set "global.domain=$PARTNER_DOMAIN" \
  --wait --timeout 10m

step "EWP"
mapfile -t ewp_images < <(node -e '
  for (const { image } of JSON.parse(require("fs").readFileSync(process.argv[1])).images) console.log(`${image.replace(/^ewp-/, "")}=${image}`);
' "$repo/.github/container-images.json")
image_values "$work/ewp-images.json" "${ewp_images[@]}"
helm upgrade --install ewp "$repo/deploy/helm/ewp" --namespace ewp \
  -f "$repo/deploy/helm/ewp/values-demo.yaml" -f "$work/ewp-images.json" \
  --set "global.domain=$EWP_DOMAIN" \
  --set-json "global.forwardedHeaders.knownNetworks=[\"$POD_CIDR\"]" \
  --set "global.partnerBank.tokenEndpoint=https://partner-auth.$PARTNER_DOMAIN:47411/connect/token" \
  --set "global.partnerBank.apiBaseUrl=https://partner-api.$PARTNER_DOMAIN:47403" \
  --set "global.partnerBank.staffViewUrl=https://partner-products.$PARTNER_DOMAIN:47402/StaffView" \
  --wait --timeout 20m

# ┌─ OBS ────────────────────────────────────────────────────────────────────────────────────────
# │ From outside, as a browser: each issuer's discovery document through the Gateway, with the
# │ public certificate (staging runs skip only the trust check) - DNS, Gateway, routes, the
# │ re-encryption to the pod and the service itself in one request each.
# └──────────────────────────────────────────────────────────────────────────────────────────────
step "Checks through the Gateway"
trust=""; [ "$CERT_ISSUER" = "letsencrypt-staging" ] && trust="--insecure"
for issuer in "https://idp.$EWP_DOMAIN" "https://partner-auth.$PARTNER_DOMAIN:47401"; do
  for attempt in $(seq 1 30); do
    found="$(curl -fsS $trust --max-time 15 "$issuer/.well-known/openid-configuration" 2>/dev/null \
      | node -e 'let d = ""; process.stdin.on("data", c => d += c).on("end", () => { try { process.stdout.write(JSON.parse(d).issuer ?? "") } catch { } })' || true)"
    [ "$found" = "$issuer" ] && { echo "ok  $issuer (discovery document)"; break; }
    [ "$attempt" -eq 30 ] && { echo "::error::$issuer/.well-known/openid-configuration did not answer with its issuer."; exit 1; }
    echo "waiting for $issuer ($attempt/30)"; sleep 10
  done
done

# ┌─ OBS + SEC ──────────────────────────────────────────────────────────────────────────────────
# │ EWP's own token at the Partner Bank, asked for exactly as Partner Products asks for it: from
# │ a pod in the ewp namespace, mutual TLS with EWP's client certificate (the demo PKI), the
# │ Partner Bank's server certificate trusted only through the demo root, client credentials
# │ for scope partner.distribution, through the Gateway's passthrough port 47411. The browser
# │ checks above never touch this path (first production-certificate run, 26-OCT-2026: green,
# │ yet Digital Gold could not get its token). The token itself is never printed - only the
# │ status, the Partner Bank's error and, on a failure, curl's TLS steps and the logs.
# └──────────────────────────────────────────────────────────────────────────────────────────────
step "Check: EWP's token at the Partner Bank (mutual TLS, as Partner Products asks)"
token_url="https://partner-auth.$PARTNER_DOMAIN:47411/connect/token"
kubectl delete pod partner-token-check -n ewp --ignore-not-found --wait=true > /dev/null
kubectl apply -f - <<EOF
apiVersion: v1
kind: Pod
metadata:
  name: partner-token-check
  namespace: ewp
  labels: {app.kubernetes.io/name: partner-token-check}
spec:
  restartPolicy: Never
  automountServiceAccountToken: false
  securityContext: {runAsNonRoot: true, runAsUser: 100, runAsGroup: 101, seccompProfile: {type: RuntimeDefault}}
  containers:
    - name: curl
      image: curlimages/curl:8.22.0@sha256:58adaa4e8dca9c988bae2aba4ab3434a0bb2da16bbe3f92dec39ec7785166777
      securityContext: {allowPrivilegeEscalation: false, readOnlyRootFilesystem: true, capabilities: {drop: ["ALL"]}}
      resources: {requests: {cpu: 10m, memory: 16Mi}, limits: {memory: 64Mi}}
      command: ["sh", "-c"]
      args:
        - |
          ask() {
            curl -sS --max-time 20 "\$@" -o /tmp/body -w '%{http_code}' \
              --cacert /pki/ewp-trust-root.cer \
              --cert-type P12 --cert "/pki/ewp-partner-client.pfx:\$(cat /pki/ewp-partner-client.pfx.password)" \
              -d grant_type=client_credentials -d client_id=FourWalls.Ewp.PartnerProducts -d scope=partner.distribution \
              "$token_url"
          }
          code="\$(ask)"
          if [ "\$code" = "200" ] && grep -q '"access_token"' /tmp/body; then
            echo "ok  HTTP 200: the Partner Bank issued EWP's token"; exit 0
          fi
          echo "FAILED: HTTP \${code:-none}"
          [ -s /tmp/body ] && sed -e 's/"access_token":"[^"]*"/"access_token":"(hidden)"/' /tmp/body && echo
          echo "--- curl's connection and TLS steps:"
          ask -v 2>&1 | grep -E '^\* ' | grep -v -i 'password'
          exit 1
      volumeMounts:
        - {name: pki, mountPath: /pki, readOnly: true}
        - {name: tmp, mountPath: /tmp}
  volumes:
    - {name: pki, secret: {secretName: ewp-partner-pki}}
    - {name: tmp, emptyDir: {sizeLimit: 1Mi}}
EOF
phase=""
for attempt in $(seq 1 30); do
  phase="$(kubectl get pod partner-token-check -n ewp -o jsonpath='{.status.phase}' 2>/dev/null || true)"
  case "$phase" in Succeeded|Failed) break ;; esac
  sleep 5
done
kubectl logs partner-token-check -n ewp 2>/dev/null || true
kubectl delete pod partner-token-check -n ewp --ignore-not-found --wait=false > /dev/null
if [ "$phase" != "Succeeded" ]; then
  echo "::error::EWP could not get its token from the Partner Bank ($token_url; pod phase: ${phase:-unknown}). The Partner Products and Partner Bank log lines follow."
  bash "$repo/.github/scripts/demo-logs.sh" 'partner'
  exit 1
fi

echo
echo "Platform up: https://shell.$EWP_DOMAIN/"