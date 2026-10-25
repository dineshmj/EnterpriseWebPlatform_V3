# ┌─ :: ARCHITECTURE + SECURITY :: The demo's public address and its names ───────────────────────
# │ Why : The browser reaches EWP at <host>.<EWP domain> and the Partner Bank at
# │       <host>.<Partner Bank domain>. Both go through one gateway; the gateway's address must
# │       be known before Let's Encrypt can check the names (HTTP-01).
# │ How : A static public IP made here (not by Kubernetes), so the DNS records can point at it
# │       in the same apply and destroy removes both. The gateway's Service binds it by name
# │       (deploy/helm/ewp-gateway). One wildcard A record per zone: every host of the charts
# │       resolves without a list to keep in step with them. The zones themselves are permanent
# │       and only read; the identity may write A and CNAME records there and nothing else.
# │ Ref : doc/Cloud-Demo.md | doc/Azure.md A8, A9 | RFC 4592 (wildcards in DNS)
# └────────────────────────────────────────────────────────────────────────────────────────────────

resource "azurerm_public_ip" "gateway" {
  name                = "pip-ewp-demo-gateway"
  location            = local.location
  resource_group_name = data.azurerm_resource_group.demo.name
  allocation_method   = "Static"
  sku                 = "Standard"
  tags                = local.tags
}

data "azurerm_dns_zone" "ewp" {
  name                = var.ewp_domain
  resource_group_name = var.dns_resource_group
}

data "azurerm_dns_zone" "partner_bank" {
  name                = var.partner_bank_domain
  resource_group_name = var.dns_resource_group
}

# ┌─ SEC ────────────────────────────────────────────────────────────────────────────────────────
# │ A wildcard, not the apex: the bare domain stays unanswered. CAA at the apex (set by the
# │ bootstrap) still allows only Let's Encrypt to issue for any of these names. TTL 5 minutes,
# │ so the next demo's new address is picked up quickly.
# └──────────────────────────────────────────────────────────────────────────────────────────────
resource "azurerm_dns_a_record" "ewp" {
  name                = "*"
  zone_name           = data.azurerm_dns_zone.ewp.name
  resource_group_name = var.dns_resource_group
  ttl                 = 300
  records             = [azurerm_public_ip.gateway.ip_address]
  tags                = local.tags
}

resource "azurerm_dns_a_record" "partner_bank" {
  name                = "*"
  zone_name           = data.azurerm_dns_zone.partner_bank.name
  resource_group_name = var.dns_resource_group
  ttl                 = 300
  records             = [azurerm_public_ip.gateway.ip_address]
  tags                = local.tags
}