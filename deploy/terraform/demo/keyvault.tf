# ┌─ :: SECURITY :: The demo's secret store ───────────────────────────────────────────────────────
# │ Why : The charts hold no secret (doc/Kubernetes.md K5); something must hold them for the
# │       demo's lifetime and hand them to the pods without a key in GitHub.
# │ How : One Key Vault per organisation and demo: EWP's, and the Partner Bank's (another bank:
# │       EWP's identities never read its keys, and the other way round). RBAC authorization
# │       only (no access policies). The workflow is Key Vault Secrets Officer on these two
# │       vaults alone; the pods read through the CSI driver with workload identity (Key Vault
# │       Secrets User, workload-identity.tf). 7-day soft delete, no purge protection:
# │       the vault dies with the demo, and purge protection would keep its name for 90 days.
# │       DEBT: public endpoint (GitHub's hosted runners have no fixed addresses); every call
# │       still needs an Entra token with a role on this vault. Production: private endpoint.
# │ Ref : doc/Cloud-Demo.md | doc/Azure.md A4 | Microsoft "Key Vault RBAC guide" | OWASP ASVS V13.3
# └────────────────────────────────────────────────────────────────────────────────────────────────

resource "azurerm_key_vault" "demo" {
  name                          = "kv-ewp-demo-${local.suffix}"
  location                      = local.location
  resource_group_name           = data.azurerm_resource_group.demo.name
  tenant_id                     = data.azurerm_client_config.current.tenant_id
  sku_name                      = "standard"
  rbac_authorization_enabled    = true
  soft_delete_retention_days    = 7
  purge_protection_enabled      = false
  public_network_access_enabled = true
  tags                          = local.tags
}

resource "azurerm_key_vault" "partner_bank" {
  name                          = "kv-pb-demo-${local.suffix}"
  location                      = local.location
  resource_group_name           = data.azurerm_resource_group.demo.name
  tenant_id                     = data.azurerm_client_config.current.tenant_id
  sku_name                      = "standard"
  rbac_authorization_enabled    = true
  soft_delete_retention_days    = 7
  purge_protection_enabled      = false
  public_network_access_enabled = true
  tags                          = local.tags
}

resource "azurerm_role_assignment" "workflow_secrets_officer" {
  scope                            = azurerm_key_vault.demo.id
  role_definition_name             = "Key Vault Secrets Officer"
  principal_id                     = data.azurerm_client_config.current.object_id
  principal_type                   = "ServicePrincipal"
  skip_service_principal_aad_check = true
}

resource "azurerm_role_assignment" "workflow_partner_secrets_officer" {
  scope                            = azurerm_key_vault.partner_bank.id
  role_definition_name             = "Key Vault Secrets Officer"
  principal_id                     = data.azurerm_client_config.current.object_id
  principal_type                   = "ServicePrincipal"
  skip_service_principal_aad_check = true
}

# ┌─ RESIL ──────────────────────────────────────────────────────────────────────────────────────
# │ A new role assignment takes up to a few minutes to reach Key Vault's data plane; writing a
# │ secret straight away fails with 403. Secrets depend on this wait, so on destroy they go
# │ before the assignment that allows deleting them.
# └──────────────────────────────────────────────────────────────────────────────────────────────
resource "time_sleep" "key_vault_role" {
  create_duration = "90s"
  depends_on = [
    azurerm_role_assignment.workflow_secrets_officer,
    azurerm_role_assignment.workflow_partner_secrets_officer,
  ]
}