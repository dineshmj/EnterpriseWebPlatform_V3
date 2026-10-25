# ┌─ :: SECURITY :: How pods read their secrets: workload identity, no key anywhere ───────────────
# │ Why : The pods' secrets are in Key Vault (keyvault.tf); reading them must need no stored
# │       credential, and EWP's pods must not be able to read the Partner Bank's vault.
# │ How : One managed identity per organisation, Key Vault Secrets User on its own vault only.
# │       Each trusts exactly one Kubernetes service account per namespace through a federated
# │       credential: the AKS OIDC issuer signs a short-lived token for that service account,
# │       Entra ID exchanges it for an Azure token. The service account is used by one pod per
# │       namespace only, the secret sync pod (deploy/helm/keyvault-secrets): the CSI driver
# │       asks Kubernetes for that pod's token; no pod has a Kubernetes token mounted.
# │         id-ewp-demo-secrets  <- ewp/keyvault-secrets, ewp-infra/keyvault-secrets
# │         id-pb-demo-secrets   <- partner-bank/keyvault-secrets
# │ Ref : doc/Cloud-Demo.md CD14-CD15 | Microsoft "Use Microsoft Entra Workload ID with AKS" |
# │       OWASP ASVS V13.3
# └────────────────────────────────────────────────────────────────────────────────────────────────

locals {
  # CONTRACT: deploy/helm/keyvault-secrets names its service account keyvault-secrets.
  secrets_service_account = "keyvault-secrets"

  ewp_secret_namespaces = ["ewp", "ewp-infra"]
}

resource "azurerm_user_assigned_identity" "ewp_secrets" {
  name                = "id-ewp-demo-secrets"
  location            = local.location
  resource_group_name = data.azurerm_resource_group.demo.name
  tags                = local.tags
}

resource "azurerm_user_assigned_identity" "partner_bank_secrets" {
  name                = "id-pb-demo-secrets"
  location            = local.location
  resource_group_name = data.azurerm_resource_group.demo.name
  tags                = local.tags
}

resource "azurerm_federated_identity_credential" "ewp_secrets" {
  for_each = toset(local.ewp_secret_namespaces)

  name                      = "aks-${each.key}-${local.secrets_service_account}"
  user_assigned_identity_id = azurerm_user_assigned_identity.ewp_secrets.id
  issuer                    = azurerm_kubernetes_cluster.demo.oidc_issuer_url
  subject                   = "system:serviceaccount:${each.key}:${local.secrets_service_account}"
  audience                  = ["api://AzureADTokenExchange"]
}

resource "azurerm_federated_identity_credential" "partner_bank_secrets" {
  name                      = "aks-partner-bank-${local.secrets_service_account}"
  user_assigned_identity_id = azurerm_user_assigned_identity.partner_bank_secrets.id
  issuer                    = azurerm_kubernetes_cluster.demo.oidc_issuer_url
  subject                   = "system:serviceaccount:partner-bank:${local.secrets_service_account}"
  audience                  = ["api://AzureADTokenExchange"]
}

resource "azurerm_role_assignment" "ewp_secrets_user" {
  scope                            = azurerm_key_vault.demo.id
  role_definition_name             = "Key Vault Secrets User"
  principal_id                     = azurerm_user_assigned_identity.ewp_secrets.principal_id
  principal_type                   = "ServicePrincipal"
  skip_service_principal_aad_check = true
}

resource "azurerm_role_assignment" "partner_bank_secrets_user" {
  scope                            = azurerm_key_vault.partner_bank.id
  role_definition_name             = "Key Vault Secrets User"
  principal_id                     = azurerm_user_assigned_identity.partner_bank_secrets.principal_id
  principal_type                   = "ServicePrincipal"
  skip_service_principal_aad_check = true
}