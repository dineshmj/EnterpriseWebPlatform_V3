# CONTRACT: the "Demo up" workflow reads these by name (terraform output -raw <name>); step 7b's
# platform install (.github/scripts/demo-platform.sh) reads the rest. None is secret.

output "cluster_name" {
  value = azurerm_kubernetes_cluster.demo.name
}

output "resource_group" {
  value = data.azurerm_resource_group.demo.name
}

output "location" {
  value = local.location
}

output "gateway_ip" {
  value = azurerm_public_ip.gateway.ip_address
}

output "gateway_ip_name" {
  value = azurerm_public_ip.gateway.name
}

output "pod_cidr" {
  description = "The gateway pods' addresses come from here: the charts' global.forwardedHeaders.knownNetworks."
  value       = local.pod_cidr
}

output "key_vault_name" {
  value = azurerm_key_vault.demo.name
}

output "postgres_host" {
  value = azurerm_postgresql_flexible_server.demo.fqdn
}

output "postgres_admin_login" {
  value = azurerm_postgresql_flexible_server.demo.administrator_login
}

output "partner_bank_key_vault_name" {
  value = azurerm_key_vault.partner_bank.name
}

output "tenant_id" {
  value = data.azurerm_client_config.current.tenant_id
}

output "ewp_secrets_client_id" {
  description = "The identity EWP's secret sync pods read Key Vault with (workload-identity.tf)."
  value       = azurerm_user_assigned_identity.ewp_secrets.client_id
}

output "partner_bank_secrets_client_id" {
  description = "The identity the Partner Bank's secret sync pod reads its Key Vault with."
  value       = azurerm_user_assigned_identity.partner_bank_secrets.client_id
}