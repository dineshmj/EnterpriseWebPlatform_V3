# CONTRACT: the "Demo up" workflow reads these by name (terraform output -raw <name>); step 7b's
# platform install adds the database and Key Vault ones to the charts' values. None is secret.

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

output "oidc_issuer_url" {
  description = "For step 7b's workload identity federation (pods -> Key Vault)."
  value       = azurerm_kubernetes_cluster.demo.oidc_issuer_url
}

output "key_vault_secrets_provider_client_id" {
  value = azurerm_kubernetes_cluster.demo.key_vault_secrets_provider[0].secret_identity[0].client_id
}