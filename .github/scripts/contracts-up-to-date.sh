#!/usr/bin/env bash
# ┌─ :: ARCHITECTURE :: The committed contracts are the ones the consumers write ──────────────────
# │ Why : The providers are verified against the COMMITTED Pact files. If a consumer's tests now
# │       write a different file (a field added, a status changed) that was not committed, the
# │       providers would be checked against an old promise - green while the consumer breaks.
# │ How : Run after the tests (which rewrite the consumers' files only when every one of their
# │       contract tests passed): any changed, new or deleted file in tst/contracts/pacts fails
# │       the job and shows the difference. Fix: run the consumer's tests locally and commit
# │       the files they write.
# │ Ref : tst/contracts/README.md | doc/CI.md
# └────────────────────────────────────────────────────────────────────────────────────────────────
set -euo pipefail

changes=$(git status --porcelain -- tst/contracts/pacts)
if [ -n "$changes" ]; then
  echo "$changes"
  git --no-pager diff -- tst/contracts/pacts
  echo "::error::The consumer contracts in tst/contracts/pacts differ from what the consumer tests write - run them and commit the files (tst/contracts/README.md)."
  exit 1
fi
echo "Contracts up to date: $(find tst/contracts/pacts -name '*.json' | wc -l) files."