# ┌─ :: ARCHITECTURE + SECURITY :: The demo's network ─────────────────────────────────────────────
# │ Why : The nodes and the database need private addresses; the database must not be reachable
# │       from the internet at all.
# │ How : One virtual network, two subnets: the nodes (Azure CNI overlay: pods get addresses from
# │       pod_cidr, outside the VNet) and a subnet delegated to PostgreSQL Flexible Server (VNet
# │       integration: no public endpoint). A private DNS zone resolves the server's name inside
# │       the VNet only. The only public address is the gateway's (dns.tf).
# │ Ref : doc/Cloud-Demo.md | Microsoft "Azure CNI Overlay" | Microsoft "PostgreSQL networking with private access"
# └────────────────────────────────────────────────────────────────────────────────────────────────

locals {
  vnet_cidr     = "10.224.0.0/16"
  nodes_cidr    = "10.224.0.0/20"
  postgres_cidr = "10.224.16.0/24"
  # NOTE: pods and Services live outside the VNet (overlay); they must not overlap it.
  pod_cidr       = "10.244.0.0/16"
  service_cidr   = "10.0.0.0/16"
  dns_service_ip = "10.0.0.10"
}

resource "azurerm_virtual_network" "demo" {
  name                = "vnet-ewp-demo"
  location            = local.location
  resource_group_name = data.azurerm_resource_group.demo.name
  address_space       = [local.vnet_cidr]
  tags                = local.tags
}

resource "azurerm_subnet" "nodes" {
  name                 = "snet-aks-nodes"
  resource_group_name  = data.azurerm_resource_group.demo.name
  virtual_network_name = azurerm_virtual_network.demo.name
  address_prefixes     = [local.nodes_cidr]
}

resource "azurerm_subnet" "postgres" {
  name                 = "snet-postgres"
  resource_group_name  = data.azurerm_resource_group.demo.name
  virtual_network_name = azurerm_virtual_network.demo.name
  address_prefixes     = [local.postgres_cidr]

  delegation {
    name = "postgres-flexible-server"
    service_delegation {
      name    = "Microsoft.DBforPostgreSQL/flexibleServers"
      actions = ["Microsoft.Network/virtualNetworks/subnets/join/action"]
    }
  }
}

resource "azurerm_private_dns_zone" "postgres" {
  name                = "ewp-demo.private.postgres.database.azure.com"
  resource_group_name = data.azurerm_resource_group.demo.name
  tags                = local.tags
}

resource "azurerm_private_dns_zone_virtual_network_link" "postgres" {
  name                 = "vnet-ewp-demo"
  private_dns_zone_id  = azurerm_private_dns_zone.postgres.id
  virtual_network_id   = azurerm_virtual_network.demo.id
  registration_enabled = false
  tags                 = local.tags
}