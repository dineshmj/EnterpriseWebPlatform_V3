# The workflows pass the first four from the GitHub environment's variables (TF_VAR_*); the rest
# have the demo's defaults.

variable "demo_resource_group" {
  description = "The throw-away resource group the bootstrap made (AZURE_DEMO_RESOURCE_GROUP). Terraform fills it and empties it; it never creates or deletes the group itself."
  type        = string
}

variable "dns_resource_group" {
  description = "The permanent group holding the two DNS zones (AZURE_DNS_RESOURCE_GROUP)."
  type        = string
}

variable "ewp_domain" {
  description = "EWP's DNS zone (AZURE_DNS_ZONE_EWP); the browser reaches EWP at <host>.<ewp_domain>."
  type        = string
}

variable "partner_bank_domain" {
  description = "The Partner Bank's DNS zone (AZURE_DNS_ZONE_PARTNER_BANK)."
  type        = string

  validation {
    condition     = var.partner_bank_domain != var.ewp_domain
    error_message = "The Partner Bank needs its own registrable domain (doc/Azure.md A8)."
  }
}

variable "location" {
  description = "Azure region of the demo's resources (Demo up input). Empty: the demo resource group's region. Each region needs its own vCPU quota (doc/Cloud-Demo.md)."
  type        = string
  default     = ""

  validation {
    condition     = var.location == "" || can(regex("^[a-z0-9]+$", var.location))
    error_message = "An Azure region name such as centralindia or australiaeast (lower case, no spaces)."
  }
}

variable "started_at" {
  description = "When this demo was first brought up (UTC, RFC 3339). A tag only; the watchdog reads the resource group's tag."
  type        = string
  default     = ""
}

variable "kubernetes_version" {
  description = "AKS minor version; AKS picks its latest patch. The charts are checked against 1.34 (doc/Kubernetes.md)."
  type        = string
  default     = "1.34"
}

variable "node_vm_size" {
  description = "VM size of the cluster's nodes."
  type        = string
  default     = "Standard_D4as_v5"
}

variable "node_count" {
  description = "Number of nodes. Step 7a needs few; the whole platform (step 7b) needs more, and the subscription's vCPU quota must allow node_count x the size's vCPUs."
  type        = number
  default     = 2

  validation {
    condition     = var.node_count >= 1 && var.node_count <= 5
    error_message = "1 to 5 nodes: a demo needs no more, and a typo should not start 50 VMs."
  }
}

variable "postgres_version" {
  description = "PostgreSQL major version: the laptop runs 18."
  type        = string
  default     = "18"
}

variable "postgres_sku" {
  description = "PostgreSQL Flexible Server size. B2s allows a few hundred connections; B1ms (50) is too few for the 12 databases' pools."
  type        = string
  default     = "B_Standard_B2s"
}