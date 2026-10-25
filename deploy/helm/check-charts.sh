#!/usr/bin/env bash
# ┌─ :: ARCHITECTURE :: Check the Helm charts without a cluster ───────────────────────────────────
# │ Why : The same check in CI (.github/workflows/ci.yml, job "Helm charts") and on a laptop.
# │ How : helm lint + helm template for every values file, kubeconform (strict) on the result,
# │       and the required values must stop a render. kubeconform is skipped with a warning
# │       when it is not installed (laptop); CI always has it. Then the cloud demo's contracts
# │       (check-contracts.mjs: secrets, database roles, Kafka users and topics) and its
# │       throw-away PKI (demo-secrets.mjs --check-pki).
# │       Needs: helm, node, openssl, optionally kubeconform; KUBERNETES_VERSION and
# │       CRDS_CATALOG_COMMIT from the CI job (defaults below are the same).
# │ Ref : doc/Kubernetes.md
# └────────────────────────────────────────────────────────────────────────────────────────────────
set -euo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
kubernetes_version="${KUBERNETES_VERSION:-1.34.0}"
crds_commit="${CRDS_CATALOG_COMMIT:-501527ab9c40c827144c8f45945b25a023e344ce}"
out="$(mktemp -d)"
trap 'rm -rf "$out"' EXIT

validate() {
  if command -v kubeconform >/dev/null 2>&1; then
    kubeconform -strict -summary -kubernetes-version "$kubernetes_version" \
      -schema-location default \
      -schema-location "https://raw.githubusercontent.com/datreeio/CRDs-catalog/$crds_commit/{{.Group}}/{{.ResourceKind}}_{{.ResourceAPIVersion}}.json" \
      "$1"
  else
    echo "::warning::kubeconform not installed - schema check skipped for $1"
  fi
}

echo "== ewp-cluster"
helm lint --strict "$here/ewp-cluster"
helm template ewp-cluster "$here/ewp-cluster" > "$out/ewp-cluster.yaml"
validate "$out/ewp-cluster.yaml"

# Every environment's values file (values.yaml alone lacks the required values on purpose).
for values in "$here"/ewp/values-*.yaml; do
  env_name="$(basename "$values" .yaml)"
  echo "== ewp with $env_name"
  helm lint --strict "$here/ewp" -f "$values"
  helm template ewp "$here/ewp" --namespace ewp -f "$values" > "$out/ewp-$env_name.yaml"
  validate "$out/ewp-$env_name.yaml"
done

# SEC: the chart refuses to render without the values that must come from the environment.
values="$here/ewp/values-demo.yaml"
for missing in global.domain global.image.tag global.kafka.bootstrapServers global.temporal.address global.partnerBank.apiBaseUrl; do
  if helm template ewp "$here/ewp" -f "$values" --set "$missing=" > /dev/null 2>&1; then
    echo "::error::The ewp chart rendered without $missing - it must refuse."
    exit 1
  fi
  echo "refused without $missing (as it must)"
done

echo "== ewp-gateway"
gateway_values="$here/ewp-gateway/values-demo.yaml"
helm lint --strict "$here/ewp-gateway" -f "$gateway_values"
helm template ewp-gateway "$here/ewp-gateway" --namespace gateway-system -f "$gateway_values" > "$out/ewp-gateway.yaml"
validate "$out/ewp-gateway.yaml"
for missing in domain publicIp.name publicIp.resourceGroup partnerBank.domain; do
  if helm template ewp-gateway "$here/ewp-gateway" -f "$gateway_values" --set "$missing=" > /dev/null 2>&1; then
    echo "::error::The ewp-gateway chart rendered without $missing - it must refuse."
    exit 1
  fi
  echo "refused without $missing (as it must)"
done
if helm template ewp-gateway "$here/ewp-gateway" -f "$gateway_values" --set certificates.issuer=other > /dev/null 2>&1; then
  echo "::error::The ewp-gateway chart accepted an unknown certificate issuer - it must refuse."
  exit 1
fi
echo "refused an unknown certificate issuer (as it must)"

# CONTRACT: the public certificate must name every public host of the EWP chart, or a browser
# meets a certificate error on the host it lacks.
ewp_hosts="$(sed -n 's/^ *public: *{host: *\([a-z0-9-]*\)}.*/\1/p' "$here/ewp/values.yaml" | sort | tr '\n' ' ')"
gateway_hosts="$(sed -n '/^hosts:/,/^[a-z]/s/^  - *\([a-z0-9-]*\)$/\1/p' "$here/ewp-gateway/values.yaml" | sort | tr '\n' ' ')"
if [ -z "$ewp_hosts" ] || [ "$ewp_hosts" != "$gateway_hosts" ]; then
  echo "::error::ewp-gateway hosts ($gateway_hosts) differ from the ewp chart's public hosts ($ewp_hosts)."
  exit 1
fi
echo "public certificate covers the ewp chart's public hosts: $ewp_hosts"

echo "== ewp-infra"
infra_values="$here/ewp-infra/values-demo.yaml"
helm lint --strict "$here/ewp-infra" -f "$infra_values"
helm template ewp-infra "$here/ewp-infra" --namespace ewp-infra -f "$infra_values" > "$out/ewp-infra.yaml"
validate "$out/ewp-infra.yaml"
for missing in database.host database.adminLogin; do
  if helm template ewp-infra "$here/ewp-infra" -f "$infra_values" --set "$missing=" > /dev/null 2>&1; then
    echo "::error::The ewp-infra chart rendered without $missing - it must refuse."
    exit 1
  fi
  echo "refused without $missing (as it must)"
done

echo "== keyvault-secrets"
kvs_values="$here/keyvault-secrets/values-check.yaml"
helm lint --strict "$here/keyvault-secrets" -f "$kvs_values"
helm template keyvault-secrets "$here/keyvault-secrets" --namespace ewp -f "$kvs_values" > "$out/keyvault-secrets.yaml"
validate "$out/keyvault-secrets.yaml"
for missing in keyVault.name keyVault.tenantId keyVault.clientId; do
  if helm template keyvault-secrets "$here/keyvault-secrets" -f "$kvs_values" --set "$missing=" > /dev/null 2>&1; then
    echo "::error::The keyvault-secrets chart rendered without $missing - it must refuse."
    exit 1
  fi
  echo "refused without $missing (as it must)"
done

echo "== cloud demo contracts"
node "$here/check-contracts.mjs" "$out/ewp-values-demo.yaml" "$out/ewp-infra.yaml"
node "$here/../../.github/scripts/demo-secrets.mjs" --check-pki

echo "Helm charts OK"