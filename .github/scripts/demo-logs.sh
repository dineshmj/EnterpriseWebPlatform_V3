#!/usr/bin/env bash
# ┌─ :: OBSERVABILITY + SECURITY :: The demo's evidence, before it is gone ─────────────────────────
# │ Why : The platform's pods log why something failed - and a demo cluster is destroyed within
# │       hours. Without this, a fault seen in the browser could only be guessed at afterwards
# │       (first production-certificate run, 26-OCT-2026: "EWP could not get its token").
# │ How : Prints, per namespace and pod, the recent lines that report a problem (warning, error,
# │       failure, refusal, exception) and the 4 lines after each -
# │       .NET's console format and Duende put the reason on the NEXT line ("fail: ...
# │       ValidatingClientStore" alone said nothing, 26-OCT-2026) - in collapsible groups of the
# │       job log. Only those lines:
# │       the services already mask account numbers and never log secrets or tokens, but the
# │       job log of a public repository is public, so routine lines stay out of it.
# │       Never fails: it is evidence for a person, not a gate.
# │       Usage: demo-logs.sh [pod-name regex]   (default: every pod)
# │       Needs: kubectl (KUBECONFIG set).
# │ Ref : doc/Cloud-Demo.md (troubleshooting) | Demo down, Demo up's Partner Bank check
# └────────────────────────────────────────────────────────────────────────────────────────────────
set -uo pipefail

only="${1:-.}"
problems='warn|fail|error|exception|refus|unreachable|denied|unauthori|forbidden|timed out|timeout'

for ns in ewp partner-bank ewp-infra; do
  pods="$(kubectl get pods -n "$ns" -o name 2>/dev/null | grep -E "$only" || true)"
  [ -z "$pods" ] && continue
  for pod in $pods; do
    lines="$(kubectl logs -n "$ns" "$pod" --all-containers --tail=400 --timestamps 2>/dev/null \
      | grep -iE -A 4 --no-group-separator "$problems" | tail -n 80 || true)"
    restarts="$(kubectl get -n "$ns" "$pod" -o jsonpath='{range .status.containerStatuses[*]}{.restartCount}{" "}{end}' 2>/dev/null || true)"
    echo "::group::$ns/${pod#pod/} (restarts: ${restarts:-?})"
    if [ -n "$lines" ]; then echo "$lines"; else echo "(no warning or error in the last 400 lines)"; fi
    echo "::endgroup::"
  done
done
exit 0