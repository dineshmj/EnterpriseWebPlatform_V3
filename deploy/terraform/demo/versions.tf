# ┌─ :: ARCHITECTURE + SECURITY :: The cloud demo's Azure resources (step 7a) ───────────────────────
# │ Why : A demo runs a few hours every few months. Everything it needs must appear in one go and
# │       disappear in one go, so nothing keeps costing money and nothing drifts between demos.
# │ How : One Terraform root module, applied by the "Demo up" workflow and destroyed by
# │       "Demo down" / the watchdog. It fills only rg-ewp-demo (made by the bootstrap) and writes
# │       the demo's address records into the two permanent DNS zones. The state lives in the
# │       bootstrap's storage account, reached with Entra ID only (no account key exists).
# │       Exact provider versions; .terraform.lock.hcl pins their checksums.
# │ Ref : doc/Cloud-Demo.md | doc/Azure.md A2, A5 | HashiCorp "Dependency Lock File"
# └────────────────────────────────────────────────────────────────────────────────────────────────

terraform {
  required_version = ">= 1.16.0, < 2.0.0"

  required_providers {
    azurerm = {
      source  = "hashicorp/azurerm"
      version = "5.9.0"
    }
    random = {
      source  = "hashicorp/random"
      version = "3.9.1"
    }
    time = {
      source  = "hashicorp/time"
      version = "0.14.2"
    }
  }

  # ┌─ SEC ──────────────────────────────────────────────────────────────────────────────────────
  # │ Partial configuration: the account, container and key come from -backend-config at init.
  # │ use_azuread_auth: Entra ID sign-in to the blob service (the account has shared keys off);
  # │ use_oidc: the workflow's GitHub token, exchanged by Entra (doc/Azure.md A1, A5).
  # └────────────────────────────────────────────────────────────────────────────────────────────
  backend "azurerm" {
    use_azuread_auth = true
    use_oidc         = true
  }
}