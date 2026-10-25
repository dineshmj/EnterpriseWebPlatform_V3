#!/usr/bin/env bash
# ┌─ :: SECURITY :: The cloud demo's command-line tools, pinned ───────────────────────────────────
# │ Why : These tools run with the demo identity's rights (Azure, the cluster). A tool fetched
# │       as "latest", or swapped on its download server, would run with those rights too.
# │ How : Exact versions, each download kept only when its SHA-256 is the published one recorded
# │       here (as the CI job does for Helm). Installed into $RUNNER_TEMP/bin, added to the PATH
# │       of the following steps. The Azure CLI comes with the runner image.
# │       Arguments: the tools to install (terraform, kubectl, kubelogin, helm).
# │ Ref : doc/Cloud-Demo.md | OWASP A08 (software and data integrity) | SLSA "verify artifacts"
# └────────────────────────────────────────────────────────────────────────────────────────────────
set -euo pipefail

TERRAFORM_VERSION='1.16.7'
TERRAFORM_SHA256='5454efcf7fc87f0c2113111b9a5c7182f0ca1c6f0f23a1e110a50770a8fe473b'
KUBECTL_VERSION='v1.34.12'
KUBECTL_SHA256='90b7b9058ffeb5c10710bb1c73f541eaf426fb1b4e87df435db669de9a2564a2'
KUBELOGIN_VERSION='v0.2.20'
KUBELOGIN_SHA256='2e92450a929dd2aec4da818b40dd4fda97aaeae2d6cd9c1074cc1371210f4ab9'
# NOTE: the same Helm as the CI job "Helm charts" (.github/workflows/ci.yml).
HELM_VERSION='v4.3.1'
HELM_SHA256='0403a88ae20913d11b10a2467b50087721fea7581736da6b9d3992f5357fe54b'

bin="${RUNNER_TEMP:?}/bin"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
mkdir -p "$bin"

fetch() { # url file sha256
  curl -fsSL --retry 3 -o "$work/$2" "$1"
  echo "$3  $work/$2" | sha256sum --check --strict
}

for tool in "$@"; do
  case "$tool" in
    terraform)
      fetch "https://releases.hashicorp.com/terraform/$TERRAFORM_VERSION/terraform_${TERRAFORM_VERSION}_linux_amd64.zip" terraform.zip "$TERRAFORM_SHA256"
      unzip -q -o "$work/terraform.zip" terraform -d "$bin" ;;
    kubectl)
      fetch "https://dl.k8s.io/release/$KUBECTL_VERSION/bin/linux/amd64/kubectl" kubectl "$KUBECTL_SHA256"
      install -m 0755 "$work/kubectl" "$bin/kubectl" ;;
    kubelogin)
      fetch "https://github.com/Azure/kubelogin/releases/download/$KUBELOGIN_VERSION/kubelogin-linux-amd64.zip" kubelogin.zip "$KUBELOGIN_SHA256"
      unzip -q -o "$work/kubelogin.zip" -d "$work/kubelogin"
      install -m 0755 "$work/kubelogin/bin/linux_amd64/kubelogin" "$bin/kubelogin" ;;
    helm)
      fetch "https://get.helm.sh/helm-$HELM_VERSION-linux-amd64.tar.gz" helm.tgz "$HELM_SHA256"
      tar -xzf "$work/helm.tgz" -C "$bin" --strip-components 1 linux-amd64/helm ;;
    *)
      echo "Unknown tool: $tool" >&2
      exit 1 ;;
  esac
  echo "installed $tool"
done

echo "$bin" >> "${GITHUB_PATH:?}"