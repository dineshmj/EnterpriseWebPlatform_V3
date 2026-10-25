# ┌─ SEC ────────────────────────────────────────────────────────────────────────────────────────
# │ Signs in with the workflow's GitHub OIDC token (ARM_USE_OIDC, ARM_CLIENT_ID, ARM_TENANT_ID,
# │ ARM_SUBSCRIPTION_ID from the job): no client secret anywhere. Registers no resource provider:
# │ that is a subscription right the identity does not have; the bootstrap registered them.
# └──────────────────────────────────────────────────────────────────────────────────────────────
provider "azurerm" {
  resource_provider_registrations = "none"
  storage_use_azuread             = true

  features {
    key_vault {
      # ┌─ SEC ──────────────────────────────────────────────────────────────────────────────────
      # │ Purging needs a subscription-wide right the identity does not have. A destroyed vault
      # │ stays soft-deleted for 7 days (free); each demo's vault has a new random name, so the
      # │ old one is never in the way.
      # └────────────────────────────────────────────────────────────────────────────────────────
      purge_soft_delete_on_destroy          = false
      purge_soft_deleted_secrets_on_destroy = false
      recover_soft_deleted_key_vaults       = false
      recover_soft_deleted_secrets          = false
    }
  }
}