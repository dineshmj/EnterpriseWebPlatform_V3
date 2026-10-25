data "azurerm_client_config" "current" {}

# ┌─ ARCH ───────────────────────────────────────────────────────────────────────────────────────
# │ The group is read, never managed: it must outlive every demo, because the identity's rights
# │ are granted on it (doc/Azure.md A2). A group's region only says where its metadata lives:
# │ the demo's resources go to var.location (closest to the audience), else the group's region.
# └──────────────────────────────────────────────────────────────────────────────────────────────
data "azurerm_resource_group" "demo" {
  name = var.demo_resource_group
}

# ┌─ ARCH ───────────────────────────────────────────────────────────────────────────────────────
# │ Key Vault and PostgreSQL names are global across Azure. A new suffix per demo means a vault
# │ still soft-deleted from the last demo never blocks the next one.
# └──────────────────────────────────────────────────────────────────────────────────────────────
resource "random_string" "suffix" {
  length  = 5
  upper   = false
  special = false
}

locals {
  location = var.location != "" ? var.location : data.azurerm_resource_group.demo.location
  suffix   = random_string.suffix.result

  tags = {
    ewp-purpose      = "demo"
    ewp-managed-by   = "terraform"
    ewp-demo-started = var.started_at
  }
}