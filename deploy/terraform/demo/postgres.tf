# ┌─ :: ARCHITECTURE + SECURITY :: The demo's PostgreSQL ──────────────────────────────────────────
# │ Why : EWP's 12 databases need one PostgreSQL server; a managed one matches production and
# │       costs nothing to look after for a few hours.
# │ How : Flexible Server (Burstable) inside the VNet: public access off, reachable only from the
# │       VNet through the private DNS zone. Password sign-in for the admin. The password is
# │       generated during the apply as an EPHEMERAL value and written once to Key Vault; the
# │       server takes it back from Key Vault (ephemeral read). Both are write-only attributes,
# │       so it never appears in the plan or the state, and Key Vault is the single source: a
# │       re-run after a half-finished apply cannot give the server a different password from
# │       the vault. Step 7b's database job reads it there and creates the databases and users.
# │       Minimum backups, no high availability, no geo-redundancy: the data is demo data that a
# │       teardown deletes anyway.
# │ Ref : doc/Cloud-Demo.md | HashiCorp "Ephemeral values / write-only arguments" | OWASP ASVS V13.3 (secret management)
# └────────────────────────────────────────────────────────────────────────────────────────────────

ephemeral "random_password" "postgres_admin" {
  length      = 32
  special     = false
  min_lower   = 4
  min_upper   = 4
  min_numeric = 4
}

resource "azurerm_key_vault_secret" "postgres_admin_password" {
  name         = "postgres-admin-password"
  key_vault_id = azurerm_key_vault.demo.id
  # ┌─ SEC ──────────────────────────────────────────────────────────────────────────────────────
  # │ Write-only: sent to Key Vault once, never stored in the state. Raise the version to rotate
  # │ (the server follows through administrator_password_wo_version).
  # └────────────────────────────────────────────────────────────────────────────────────────────
  value_wo         = ephemeral.random_password.postgres_admin.result
  value_wo_version = 1
  content_type     = "PostgreSQL admin password (login ewpadmin)"

  depends_on = [time_sleep.key_vault_role]
}

# NOTE: the secret's version is unknown until it exists, so on the first apply this read waits
# for the secret; on a re-run it reads the value already stored.
ephemeral "azurerm_key_vault_secret" "postgres_admin" {
  name         = azurerm_key_vault_secret.postgres_admin_password.name
  key_vault_id = azurerm_key_vault.demo.id
  version      = azurerm_key_vault_secret.postgres_admin_password.version
}

resource "azurerm_postgresql_flexible_server" "demo" {
  name                          = "psql-ewp-demo-${local.suffix}"
  location                      = local.location
  resource_group_name           = data.azurerm_resource_group.demo.name
  version                       = var.postgres_version
  sku_name                      = var.postgres_sku
  storage_mb                    = 32768
  auto_grow_enabled             = false
  backup_retention_days         = 7
  geo_redundant_backup_enabled  = false
  delegated_subnet_id           = azurerm_subnet.postgres.id
  private_dns_zone_id           = azurerm_private_dns_zone.postgres.id
  public_network_access_enabled = false
  tags                          = local.tags

  authentication {
    password_auth_enabled         = true
    active_directory_auth_enabled = false
  }

  administrator_login               = "ewpadmin"
  administrator_password_wo         = ephemeral.azurerm_key_vault_secret.postgres_admin.value
  administrator_password_wo_version = 1

  lifecycle {
    # Azure picks a zone when none is given; reading it back must not plan a change.
    ignore_changes = [zone]
  }

  depends_on = [azurerm_private_dns_zone_virtual_network_link.postgres]
}

# ┌─ SEC ────────────────────────────────────────────────────────────────────────────────────────
# │ TLS required for every connection (the Azure default, stated so a change shows in review).
# └──────────────────────────────────────────────────────────────────────────────────────────────
resource "azurerm_postgresql_flexible_server_configuration" "require_tls" {
  name      = "require_secure_transport"
  server_id = azurerm_postgresql_flexible_server.demo.id
  value     = "on"
}