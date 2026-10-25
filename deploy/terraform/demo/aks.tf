# ┌─ :: ARCHITECTURE + SECURITY :: The demo's Kubernetes cluster ──────────────────────────────────
# │ Why : The charts (doc/Kubernetes.md) need Kubernetes 1.34+, a CNI that enforces
# │       NetworkPolicy, and a way for pods to read Key Vault without stored credentials. The
# │       demo runs hours, so the cheapest control plane will do.
# │ How : AKS Free tier. Azure CNI overlay with Cilium (enforces the charts' NetworkPolicies).
# │       Sign-in only through Entra ID with Azure RBAC for Kubernetes: local accounts off, so no
# │       cluster-admin certificate exists to leak; the workflow is given "AKS RBAC Cluster Admin"
# │       on this cluster alone. OIDC issuer + workload identity and the Key Vault CSI driver for
# │       step 7b's secrets. No automatic upgrades: the cluster is gone before one would matter.
# │       The control plane's own identity is made first, so its rights on the subnet and the
# │       gateway's public IP exist before the cluster needs them.
# │ Ref : doc/Cloud-Demo.md | doc/Azure.md A4 | Microsoft "AKS-managed Entra ID integration" | CIS AKS Benchmark 5.x
# └────────────────────────────────────────────────────────────────────────────────────────────────

resource "azurerm_user_assigned_identity" "aks" {
  name                = "id-ewp-demo-aks"
  location            = local.location
  resource_group_name = data.azurerm_resource_group.demo.name
  tags                = local.tags
}

# ┌─ SEC ────────────────────────────────────────────────────────────────────────────────────────
# │ principal_type ServicePrincipal is required: the bootstrap's condition lets the workflow hand
# │ out only a few roles, and only to principals of that type (doc/Azure.md A4).
# └──────────────────────────────────────────────────────────────────────────────────────────────
resource "azurerm_role_assignment" "aks_subnet" {
  scope                            = azurerm_subnet.nodes.id
  role_definition_name             = "Network Contributor"
  principal_id                     = azurerm_user_assigned_identity.aks.principal_id
  principal_type                   = "ServicePrincipal"
  skip_service_principal_aad_check = true
}

# The gateway's Service binds the public IP of this group (not one in the node group).
resource "azurerm_role_assignment" "aks_gateway_ip" {
  scope                            = azurerm_public_ip.gateway.id
  role_definition_name             = "Network Contributor"
  principal_id                     = azurerm_user_assigned_identity.aks.principal_id
  principal_type                   = "ServicePrincipal"
  skip_service_principal_aad_check = true
}

resource "azurerm_kubernetes_cluster" "demo" {
  name                = "aks-ewp-demo"
  location            = local.location
  resource_group_name = data.azurerm_resource_group.demo.name
  dns_prefix          = "ewp-demo"
  node_resource_group = "${data.azurerm_resource_group.demo.name}-nodes"
  kubernetes_version  = var.kubernetes_version
  sku_tier            = "Free"
  tags                = local.tags

  # ┌─ ARCH ─────────────────────────────────────────────────────────────────────────────────────
  # │ Required since provider 5.0. Manual: the node pool below is the only one (no node
  # │ auto-provisioning), so the cost is exactly node_count VMs.
  # └────────────────────────────────────────────────────────────────────────────────────────────
  node_provisioning_profile {
    mode = "Manual"
  }

  default_node_pool {
    name                         = "system"
    vm_size                      = var.node_vm_size
    node_count                   = var.node_count
    vnet_subnet_id               = azurerm_subnet.nodes.id
    os_sku                       = "AzureLinux"
    os_disk_size_gb              = 64
    only_critical_addons_enabled = false
    temporary_name_for_rotation  = "rotation"
    tags                         = local.tags

    upgrade_settings {
      max_surge = "1"
    }
  }

  identity {
    type         = "UserAssigned"
    identity_ids = [azurerm_user_assigned_identity.aks.id]
  }

  network_profile {
    network_plugin      = "azure"
    network_plugin_mode = "overlay"
    network_data_plane  = "cilium"
    network_policy      = "cilium"
    pod_cidr            = local.pod_cidr
    service_cidr        = local.service_cidr
    dns_service_ip      = local.dns_service_ip
    load_balancer_sku   = "standard"
    outbound_type       = "loadBalancer"
  }

  # ┌─ SEC ──────────────────────────────────────────────────────────────────────────────────────
  # │ Entra ID sign-in, Azure RBAC decides; no local accounts (no static admin credential).
  # │ DEBT: the API server is public (GitHub's hosted runners have no fixed addresses);
  # │ every call still needs an Entra token with a role on this cluster. Production: private
  # │ cluster or authorized IP ranges with self-hosted runners.
  # └────────────────────────────────────────────────────────────────────────────────────────────
  local_account_disabled = true
  azure_active_directory_role_based_access_control {
    tenant_id          = data.azurerm_client_config.current.tenant_id
    azure_rbac_enabled = true
  }

  oidc_issuer_enabled       = true
  workload_identity_enabled = true

  key_vault_secrets_provider {
    secret_rotation_enabled = true
  }

  # Short-lived: nothing should change under a running demo.
  node_os_upgrade_channel = "None"
  run_command_enabled     = false
  image_cleaner_enabled   = false

  depends_on = [
    azurerm_role_assignment.aks_subnet,
    azurerm_role_assignment.aks_gateway_ip,
  ]
}

# ┌─ SEC ────────────────────────────────────────────────────────────────────────────────────────
# │ The workflow (this identity) may deploy into this cluster and nothing else: the role is
# │ scoped to the cluster, and the bootstrap's condition allows exactly this role.
# └──────────────────────────────────────────────────────────────────────────────────────────────
resource "azurerm_role_assignment" "workflow_cluster_admin" {
  scope                            = azurerm_kubernetes_cluster.demo.id
  role_definition_name             = "Azure Kubernetes Service RBAC Cluster Admin"
  principal_id                     = data.azurerm_client_config.current.object_id
  principal_type                   = "ServicePrincipal"
  skip_service_principal_aad_check = true
}