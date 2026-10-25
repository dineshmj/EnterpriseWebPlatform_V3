<#
.SYNOPSIS
    Prepares an Azure subscription for EWP's cloud demo (steps 5 and 6): the two resource groups,
    the Terraform state store, the GitHub Actions identity (OIDC, no secret), the two domains' DNS
    zones and a budget alert.

.DESCRIPTION
    Run ONCE by a person who is Owner of the subscription (or Contributor + Role Based Access
    Control Administrator). Safe to run again: every step checks what exists and only creates or
    corrects what differs. Needs the Azure CLI (az) and PowerShell 5.1 or 7 - locally after
    "az login", or in Azure Cloud Shell (PowerShell), where both are already there.

    An account in several directories (tenants): sign in with "az login --tenant <tenant id>",
    because a plain "az login" skips a tenant that demands MFA ("No subscriptions found").
    Windows blocks local scripts by default: start it with
    powershell -ExecutionPolicy Bypass -File .\deploy\azure\Initialize-AzureDemo.ps1 ...
    Step by step, and the faults the first run found: doc/Azure.md, "Running it".

    What it makes (names are parameters; the defaults below):

        rg-ewp-foundation   PERMANENT. Kept between demos.
            stewptf<8 hex>      Storage account for the Terraform state: Entra ID sign-in only
                                (shared keys off), TLS 1.2+, no public blobs, versions and 30-day
                                soft delete, a CanNotDelete lock. Container "tfstate".
            id-ewp-github-demo  User-assigned managed identity that GitHub Actions signs in as.
                                One federated credential: only workflow jobs of
                                <repository> running in the GitHub environment <environment>.
            <EWP domain>        Public DNS zones of the two domains (-EwpDomain, -PartnerBankDomain).
            <Partner domain>    Apex CAA record (only Let's Encrypt may issue certificates) and a
                                CanNotDelete lock on the SOA record set (the zone cannot be deleted;
                                its records can). A re-created zone gets NEW name servers.
        rg-ewp-demo         THROW-AWAY. Always exists; a demo fills it, the teardown empties it.

    What the identity may do (and nothing else - no rights on the subscription):

        rg-ewp-demo         Contributor; Role Based Access Control Administrator limited by a
                            condition to a short list of roles, given to managed identities only
        tfstate container   Storage Blob Data Contributor (read and write the state blobs)
        each DNS zone       Custom role "EWP Demo DNS Record Writer": A and CNAME records only
                            (no NS, CAA or SOA records, no zone settings, no zone delete)

    Also: registers the resource providers the demo uses (the identity cannot: that is a
    subscription right), and a monthly budget on the subscription that e-mails at 50, 80 and 100%
    of the amount and when the forecast passes 100%. A budget only WARNS; it does not stop spending.

    At the end it prints the values for the GitHub environment (doc/Azure.md, "GitHub side").

.PARAMETER SubscriptionId
    The subscription to prepare. Default: the one "az account show" reports.

.PARAMETER AlertEmail
    Where the budget alerts go.

.PARAMETER BudgetAmount
    Monthly budget in the BILLING CURRENCY of the subscription (INR for an Indian account: 420
    means Rs 420, not USD 420). Required, so the amount is always a conscious choice.

.PARAMETER EwpDomain
    EWP's registered domain (e.g. ewp-demo.example), lower case, without a trailing dot. The
    browser reaches EWP at <host>.<EwpDomain>. Give it together with -PartnerBankDomain, on every
    run: without them the DNS step is skipped (existing zones are kept, never deleted).

.PARAMETER PartnerBankDomain
    The Partner Bank's registered domain, a DIFFERENT registrable domain from -EwpDomain (two
    organisations, so two sites to the browser).

.PARAMETER Location
    Azure region of the resource groups and the state store. Default australiaeast.

.PARAMETER GitHubRepository
    owner/name of the repository whose workflows may sign in. Default dineshmj/EnterpriseWebPlatform_V3.

.PARAMETER GitHubEnvironment
    The GitHub environment those workflows must run in. Default azure-demo.

.PARAMETER GitHubOwnerId
    Numeric id of the repository's owner, part of GitHub's OIDC subject. Default: read from
    api.github.com (public repositories). Pass it, with -GitHubRepositoryId, for a private one.

.PARAMETER GitHubRepositoryId
    Numeric id of the repository. Default: read from api.github.com.

.PARAMETER StateStorageAccountName
    Name of the state storage account, if the generated one is taken (3-24 lower-case letters and digits).

.PARAMETER Remove
    Undo everything above: budget, role assignments, the custom DNS role, locks, both resource
    groups (the Terraform state and the DNS zones go with them). Asks first. Providers stay
    registered (free).

.EXAMPLE
    .\deploy\azure\Initialize-AzureDemo.ps1 -AlertEmail you@example.com -BudgetAmount 420

.EXAMPLE
    .\deploy\azure\Initialize-AzureDemo.ps1 -AlertEmail you@example.com -BudgetAmount 420 `
        -EwpDomain ewp-demo.example -PartnerBankDomain partnerbank-demo.example

.EXAMPLE
    .\deploy\azure\Initialize-AzureDemo.ps1 -Remove
#>
[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High', DefaultParameterSetName = 'Create')]
param(
    [string]$SubscriptionId,

    [Parameter(Mandatory, ParameterSetName = 'Create')]
    [ValidatePattern('^[^@\s]+@[^@\s]+\.[^@\s]+$')]
    [string]$AlertEmail,

    [Parameter(Mandatory, ParameterSetName = 'Create')]
    [ValidateRange(1, 10000000)]
    [int]$BudgetAmount,

    [Parameter(ParameterSetName = 'Create')]
    [ValidatePattern('^(?=.{4,253}$)([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z]{2,63}$')]
    [string]$EwpDomain,

    [Parameter(ParameterSetName = 'Create')]
    [ValidatePattern('^(?=.{4,253}$)([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z]{2,63}$')]
    [string]$PartnerBankDomain,

    [string]$Location = 'australiaeast',

    [ValidatePattern('^[A-Za-z0-9-]+/[A-Za-z0-9._-]+$')]
    [string]$GitHubRepository = 'dineshmj/EnterpriseWebPlatform_V3',

    [ValidatePattern('^[A-Za-z0-9._-]+$')]
    [string]$GitHubEnvironment = 'azure-demo',

    [long]$GitHubOwnerId,

    [long]$GitHubRepositoryId,

    [string]$FoundationResourceGroup = 'rg-ewp-foundation',

    [string]$DemoResourceGroup = 'rg-ewp-demo',

    [string]$IdentityName = 'id-ewp-github-demo',

    [ValidatePattern('^[a-z0-9]{3,24}$')]
    [string]$StateStorageAccountName,

    [Parameter(Mandatory, ParameterSetName = 'Remove')]
    [switch]$Remove
)

$ErrorActionPreference = 'Stop'

$stateContainerName = 'tfstate'
$budgetName = 'ewp-demo-monthly'
$federatedCredentialName = 'github-' + $GitHubEnvironment
$stateLockName = 'ewp-keep-terraform-state'
$dnsRoleName = 'EWP Demo DNS Record Writer'
$dnsLockName = 'ewp-keep-dns-zone'
$certificateAuthority = 'letsencrypt.org'     # step 7: cert-manager + Let's Encrypt

# NOTE: ValidatePattern ignores case; DNS does too, but the comparisons below and the zone names
# do not. Only a given value is re-assigned: the pattern stays on the variable and refuses "".
if ($EwpDomain) { $EwpDomain = $EwpDomain.ToLowerInvariant() }
if ($PartnerBankDomain) { $PartnerBankDomain = $PartnerBankDomain.ToLowerInvariant() }
if ([bool]$EwpDomain -ne [bool]$PartnerBankDomain) { throw 'Give -EwpDomain and -PartnerBankDomain together (or neither).' }
if ($EwpDomain -and ($EwpDomain -eq $PartnerBankDomain -or $EwpDomain.EndsWith(".$PartnerBankDomain") -or $PartnerBankDomain.EndsWith(".$EwpDomain"))) {
    # NOTE: a sub-domain of the other would be the same site to the browser (decision A8).
    throw '-EwpDomain and -PartnerBankDomain must be two different registrable domains, neither inside the other.'
}

# ┌─ :: SECURITY :: The roles the demo identity may hand out ────────────────────────────────────
# │ Why : Terraform must create a few role assignments (the cluster's identity on its subnet, the
# │       pods' identity on the Key Vault, the workflow on the cluster). An identity that may
# │       assign ANY role could make itself Owner of the resource group, or give a person access.
# │ How : Role Based Access Control Administrator on rg-ewp-demo with an ABAC condition: create and
# │       delete only these roles, and only for principals of type ServicePrincipal (managed
# │       identities). Owner, User Access Administrator and RBAC Administrator are not on the list.
# │ Ref : doc/Azure.md A4 | Azure ABAC "Delegate Azure role assignment management with conditions"
# └────────────────────────────────────────────────────────────────────────────────────────────────
$delegableRoleNames = @(
    'Network Contributor',                              # AKS cluster identity on its own subnet
    'Key Vault Secrets User',                           # the pods' workload identity reads secrets
    'Key Vault Certificate User',                       # ... and the certificates mounted as files
    'Key Vault Secrets Officer',                        # the workflow writes the demo's secrets
    'Azure Kubernetes Service RBAC Cluster Admin'       # the workflow deploys the Helm charts
)

$resourceProviders = @(
    'Microsoft.Compute', 'Microsoft.ContainerService', 'Microsoft.DBforPostgreSQL',
    'Microsoft.KeyVault', 'Microsoft.ManagedIdentity', 'Microsoft.Network',
    'Microsoft.OperationalInsights', 'Microsoft.Insights', 'Microsoft.Storage',
    'Microsoft.Consumption'
)

function Invoke-Az {
    # Runs az, returns its JSON output as objects, throws on a non-zero exit code.
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $output = & az @args --only-show-errors -o json
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previous
    }
    if ($exitCode -ne 0) { throw "az $($args -join ' ') failed (exit code $exitCode)." }
    if ($output) { return (($output -join "`n") | ConvertFrom-Json) }
}

function Get-AzOrNull {
    # Like Invoke-Az, but "not found" (any failure) returns $null - for show-before-create checks.
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $output = & az @args --only-show-errors -o json 2>$null
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previous
    }
    if ($exitCode -ne 0 -or -not $output) { return $null }
    return (($output -join "`n") | ConvertFrom-Json)
}

function Write-TempFile([string]$Content) {
    # NOTE: values with quotes and braces (the ABAC condition, the budget JSON) go to az as
    # "@file" - passing them on the command line breaks in cmd.exe (az.cmd on Windows).
    $path = [IO.Path]::Combine([IO.Path]::GetTempPath(), 'ewp-azure-' + [Guid]::NewGuid().ToString('N') + '.txt')
    [IO.File]::WriteAllText($path, $Content, (New-Object Text.UTF8Encoding $false))
    return $path
}

function Write-Step([string]$Text) { Write-Host "`n== $Text" -ForegroundColor Cyan }

function Get-RoleDefinitionId([string]$RoleName) {
    # SEC: resolve names to their built-in GUIDs here, so a typo fails now instead of granting nothing.
    $id = Invoke-Az role definition list --name $RoleName --query '[0].name'
    if (-not $id) { throw "Built-in role '$RoleName' not found." }
    return $id
}

function Set-RoleAssignment([string]$PrincipalId, [string]$RoleName, [string]$Scope, [string]$Condition, [string]$RoleId) {
    # NOTE: a custom role assignable only below the subscription is not found by name there; its
    # caller passes the id.
    $roleId = if ($RoleId) { $RoleId } else { Get-RoleDefinitionId $RoleName }
    $existing = @(Invoke-Az role assignment list --scope $Scope --fill-principal-name false) |
        Where-Object { $_.principalId -eq $PrincipalId -and $_.scope -eq $Scope -and $_.roleDefinitionId -like "*/$roleId" }

    # NOTE: compared without white space, in case Azure stores the condition re-formatted.
    $wanted = "$Condition" -replace '\s+', ''
    $current = $existing | Where-Object { ("$($_.condition)" -replace '\s+', '') -eq $wanted }
    if ($current) {
        Write-Host "  ok      $RoleName"
        return
    }
    foreach ($stale in $existing) {
        # The same role with another condition: replace it, so a narrowed list really narrows.
        Invoke-Az role assignment delete --ids $stale.id | Out-Null
        Write-Host "  removed $RoleName (different condition)"
    }

    $arguments = @('role', 'assignment', 'create', '--assignee-object-id', $PrincipalId,
        '--assignee-principal-type', 'ServicePrincipal', '--role', $roleId, '--scope', $Scope)
    $conditionFile = $null
    if ($Condition) {
        $conditionFile = Write-TempFile $Condition
        $arguments += @('--condition', "@$conditionFile", '--condition-version', '2.0')
    }
    try {
        # RESIL: a new managed identity (or a new custom role) can take a minute to reach every replica.
        for ($attempt = 1; ; $attempt++) {
            try { Invoke-Az @arguments | Out-Null; break }
            catch {
                if ($attempt -ge 6) { throw }
                Write-Host "  waiting for the identity or role to replicate ($attempt/5)..."
                Start-Sleep -Seconds 15
            }
        }
    }
    finally {
        if ($conditionFile) { Remove-Item $conditionFile -ErrorAction SilentlyContinue }
    }
    Write-Host "  created $RoleName"
}

# ── Who and where ──────────────────────────────────────────────────────────────────────────────
if (-not (Get-Command az -ErrorAction SilentlyContinue)) {
    throw 'The Azure CLI (az) is not installed. Install it, or run this script in Azure Cloud Shell (PowerShell).'
}
$account = Get-AzOrNull account show
if (-not $account) { throw 'Not signed in to Azure. Run "az login" first (Cloud Shell is signed in already).' }
if ($SubscriptionId -and $account.id -ne $SubscriptionId) {
    Invoke-Az account set --subscription $SubscriptionId | Out-Null
    $account = Invoke-Az account show
}
$SubscriptionId = $account.id
$tenantId = $account.tenantId
$subscriptionScope = "/subscriptions/$SubscriptionId"

if (-not $StateStorageAccountName) {
    # Globally unique but stable per subscription: "stewptf" + the first 8 hex of SHA-256(subscription id).
    $sha = [Security.Cryptography.SHA256]::Create()
    $hash = -join ($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($SubscriptionId))[0..3] | ForEach-Object { $_.ToString('x2') })
    $StateStorageAccountName = 'stewptf' + $hash
}

Write-Host "Subscription : $($account.name) ($SubscriptionId)"
Write-Host "Tenant       : $tenantId"
Write-Host "Signed in as : $($account.user.name)"

$budgetUrl = "https://management.azure.com$subscriptionScope/providers/Microsoft.Consumption/budgets/$budgetName" + '?api-version=2023-05-01'

# ── Remove ─────────────────────────────────────────────────────────────────────────────────────
if ($Remove) {
    $target = "budget $budgetName, role '$dnsRoleName', resource groups $FoundationResourceGroup (Terraform state, identity, DNS zones - re-created zones get NEW name servers) and $DemoResourceGroup with everything in them"
    if (-not $PSCmdlet.ShouldProcess($target, 'Delete')) { return }
    $foundationGroupScope = "$subscriptionScope/resourceGroups/$FoundationResourceGroup"

    Write-Step 'Budget'
    if (Get-AzOrNull rest --method get --url $budgetUrl) { Invoke-Az rest --method delete --url $budgetUrl | Out-Null }

    Write-Step 'Role assignments of the identity'
    $identity = Get-AzOrNull identity show --name $IdentityName --resource-group $FoundationResourceGroup
    if ($identity) {
        # NOTE: deleting the identity would leave its assignments behind as "unknown identity"
        # rows; the container's one sits below the group, so list all and filter here.
        @(Invoke-Az role assignment list --all --fill-principal-name false) |
            Where-Object { $_.principalId -eq $identity.principalId } |
            ForEach-Object { Invoke-Az role assignment delete --ids $_.id | Out-Null; Write-Host "  removed $($_.roleDefinitionName) on $($_.scope)" }
    }

    Write-Step "Custom role '$dnsRoleName'"
    # NOTE: a role definition outlives the group it is assignable in; delete it once unassigned.
    $dnsRole = @(Get-AzOrNull role definition list --name $dnsRoleName --custom-role-only true --scope $foundationGroupScope) | Select-Object -First 1
    if ($dnsRole) { Invoke-Az role definition delete --name $dnsRole.name --scope $foundationGroupScope | Out-Null; Write-Host '  removed' }

    Write-Step 'DNS zone locks'
    foreach ($zone in @(Get-AzOrNull network dns zone list --resource-group $FoundationResourceGroup)) {
        if (-not $zone) { continue }
        $lockUrl = "https://management.azure.com$($zone.id)/SOA/@/providers/Microsoft.Authorization/locks/$dnsLockName" + '?api-version=2016-09-01'
        if (Get-AzOrNull rest --method get --url $lockUrl) { Invoke-Az rest --method delete --url $lockUrl | Out-Null; Write-Host "  removed $($zone.name)" }
    }

    Write-Step 'Lock and resource groups'
    if (Get-AzOrNull lock show --name $stateLockName --resource-group $FoundationResourceGroup --resource $StateStorageAccountName --resource-type Microsoft.Storage/storageAccounts) {
        Invoke-Az lock delete --name $stateLockName --resource-group $FoundationResourceGroup --resource $StateStorageAccountName --resource-type Microsoft.Storage/storageAccounts | Out-Null
    }
    foreach ($group in @($DemoResourceGroup, $FoundationResourceGroup)) {
        if (Get-AzOrNull group show --name $group) {
            Write-Host "  deleting $group (takes a few minutes)..."
            Invoke-Az group delete --name $group --yes | Out-Null
        }
    }
    Write-Host "`nRemoved. Delete the GitHub environment '$GitHubEnvironment' (or its variables) by hand."
    return
}

# ── 1. Resource providers ──────────────────────────────────────────────────────────────────────
Write-Step 'Resource providers'
foreach ($provider in $resourceProviders) {
    $state = Invoke-Az provider show --namespace $provider --query registrationState
    if ($state -ne 'Registered') {
        Invoke-Az provider register --namespace $provider | Out-Null
        $state = 'Registering (finishes in the background)'
    }
    Write-Host ("  {0,-32} {1}" -f $provider, $state)
}

# ── 2. Resource groups ─────────────────────────────────────────────────────────────────────────
# ┌─ :: ARCHITECTURE + SECURITY :: Two fixed resource groups ────────────────────────────────────
# │ Why : The workflow identity gets rights on resource groups, not on the subscription. A group
# │       it may create and delete would need subscription rights; so both groups exist before
# │       it ever runs, and the demo only fills and empties rg-ewp-demo.
# │ How : rg-ewp-foundation holds what must outlive a demo (state, identity, DNS zones);
# │       the identity has no management rights there at all. rg-ewp-demo holds everything else.
# │ Ref : doc/Azure.md A2, A3 | Azure Well-Architected, Security: SE:05 identity and access
# └────────────────────────────────────────────────────────────────────────────────────────────────
Write-Step 'Resource groups'
foreach ($group in @(@{ Name = $FoundationResourceGroup; Purpose = 'foundation' }, @{ Name = $DemoResourceGroup; Purpose = 'demo' })) {
    $existing = Get-AzOrNull group show --name $group.Name
    if ($existing -and $existing.location -ne $Location) {
        Write-Warning "$($group.Name) already exists in $($existing.location); keeping it there."
    }
    $groupLocation = if ($existing) { $existing.location } else { $Location }
    Invoke-Az group create --name $group.Name --location $groupLocation --tags "ewp-purpose=$($group.Purpose)" 'ewp-created-by=Initialize-AzureDemo' | Out-Null
    Write-Host "  ok      $($group.Name) ($groupLocation, $($group.Purpose))"
}
$foundationScope = "$subscriptionScope/resourceGroups/$FoundationResourceGroup"
$demoScope = "$subscriptionScope/resourceGroups/$DemoResourceGroup"

# ── 3. Terraform state store ───────────────────────────────────────────────────────────────────
# ┌─ :: SECURITY + RESILIENCE :: The Terraform state store ───────────────────────────────────────
# │ Why : The state lists every resource and may hold secret values Terraform generated. A
# │       storage account key would be a long-lived secret that opens all of it; a lost or
# │       overwritten state leaves resources nobody can tear down cleanly.
# │ How : Shared key access off (Entra ID sign-in only, so no key exists to leak), TLS 1.2+,
# │       https only, no anonymous blobs; blob versions + 30-day soft delete of blobs and the
# │       container; a CanNotDelete lock on the account. Terraform's own blob lease prevents two
# │       runs writing at once. Public endpoint kept: GitHub's runners have no fixed addresses.
# │ Ref : doc/Azure.md A5 | Terraform azurerm backend (use_azuread_auth) | CIS Azure 3.x storage
# └────────────────────────────────────────────────────────────────────────────────────────────────
Write-Step "Terraform state store ($StateStorageAccountName)"
$storage = Get-AzOrNull storage account show --name $StateStorageAccountName --resource-group $FoundationResourceGroup
$storageSettings = @('--min-tls-version', 'TLS1_2', '--allow-blob-public-access', 'false',
    '--allow-shared-key-access', 'false', '--https-only', 'true')
if (-not $storage) {
    $availability = Invoke-Az storage account check-name --name $StateStorageAccountName
    if (-not $availability.nameAvailable) {
        throw "Storage account name '$StateStorageAccountName' is taken: $($availability.message) Pass -StateStorageAccountName <another name>."
    }
    Invoke-Az storage account create --name $StateStorageAccountName --resource-group $FoundationResourceGroup `
        --location $Location --sku Standard_LRS --kind StorageV2 --public-network-access Enabled `
        --tags 'ewp-purpose=terraform-state' @storageSettings | Out-Null
    Write-Host '  created storage account'
}
else {
    Invoke-Az storage account update --name $StateStorageAccountName --resource-group $FoundationResourceGroup @storageSettings | Out-Null
    Write-Host '  ok      storage account (settings re-applied)'
}

Invoke-Az storage account blob-service-properties update --account-name $StateStorageAccountName `
    --resource-group $FoundationResourceGroup --enable-versioning true `
    --enable-delete-retention true --delete-retention-days 30 `
    --enable-container-delete-retention true --container-delete-retention-days 30 | Out-Null
Write-Host '  ok      versions + 30-day soft delete'

# NOTE: container-rm goes through Azure Resource Manager, so the signed-in person needs no data
# role on the account (shared keys are off, and "az storage container create" would need one).
if (-not (Get-AzOrNull storage container-rm show --storage-account $StateStorageAccountName --resource-group $FoundationResourceGroup --name $stateContainerName)) {
    Invoke-Az storage container-rm create --storage-account $StateStorageAccountName --resource-group $FoundationResourceGroup `
        --name $stateContainerName --public-access off | Out-Null
}
Write-Host "  ok      container $stateContainerName"

Invoke-Az lock create --name $stateLockName --lock-type CanNotDelete --resource-group $FoundationResourceGroup `
    --resource $StateStorageAccountName --resource-type Microsoft.Storage/storageAccounts `
    --notes 'Terraform state of the EWP demo. Remove with Initialize-AzureDemo.ps1 -Remove.' | Out-Null
Write-Host '  ok      CanNotDelete lock'
$stateContainerScope = "$foundationScope/providers/Microsoft.Storage/storageAccounts/$StateStorageAccountName/blobServices/default/containers/$stateContainerName"

# ── 4. GitHub Actions identity ─────────────────────────────────────────────────────────────────
# ┌─ :: SECURITY :: GitHub Actions signs in without a secret ────────────────────────────────────
# │ Why : A client secret or certificate stored in GitHub is a long-lived credential: whoever
# │       reads it can sign in from anywhere, until someone notices and rotates it.
# │ How : A user-assigned managed identity (no secret can even be created for one) with ONE
# │       federated credential: Entra accepts GitHub's short-lived OIDC token only when its
# │       subject is "repo:<owner>@<id>/<repo>@<id>:environment:<environment>" and its audience is
# │       api://AzureADTokenExchange. The GitHub environment adds the human gate (required
# │       reviewer) and admits only main.
# │ Ref : doc/Azure.md A1 | Microsoft Entra workload identity federation | GitHub OIDC hardening
# └────────────────────────────────────────────────────────────────────────────────────────────────
Write-Step "GitHub Actions identity ($IdentityName)"
$identity = Get-AzOrNull identity show --name $IdentityName --resource-group $FoundationResourceGroup
if (-not $identity) {
    $identity = Invoke-Az identity create --name $IdentityName --resource-group $FoundationResourceGroup --location $Location `
        --tags 'ewp-purpose=github-actions'
    Write-Host '  created managed identity'
}
else { Write-Host '  ok      managed identity' }

# ┌─ SEC ────────────────────────────────────────────────────────────────────────────────────────
# │ GitHub's subject carries the owner's and the repository's numeric ids next to their names
# │ (repo:owner@123/name@456:environment:x). A repository deleted and re-created under the same
# │ name gets a new id, so it cannot sign in. Ids and exact spelling come from GitHub's API.
# └────────────────────────────────────────────────────────────────────────────────────────────────
if (-not $GitHubOwnerId -or -not $GitHubRepositoryId) {
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    try { $repositoryInfo = Invoke-RestMethod -Uri "https://api.github.com/repos/$GitHubRepository" -Headers @{ Accept = 'application/vnd.github+json' } }
    catch { throw "Could not read https://api.github.com/repos/$GitHubRepository ($($_.Exception.Message)). For a private repository pass -GitHubOwnerId and -GitHubRepositoryId (the run's 'subject claim' line shows them)." }
    $GitHubOwnerId = $repositoryInfo.owner.id
    $GitHubRepositoryId = $repositoryInfo.id
    $GitHubRepository = $repositoryInfo.full_name
}
$ownerName, $repositoryName = $GitHubRepository -split '/', 2
$subject = "repo:$ownerName@$GitHubOwnerId/$repositoryName@${GitHubRepositoryId}:environment:$GitHubEnvironment"
$federatedArguments = @('--name', $federatedCredentialName, '--identity-name', $IdentityName,
    '--resource-group', $FoundationResourceGroup, '--issuer', 'https://token.actions.githubusercontent.com',
    '--subject', $subject, '--audiences', 'api://AzureADTokenExchange')
$federated = Get-AzOrNull identity federated-credential show --name $federatedCredentialName --identity-name $IdentityName --resource-group $FoundationResourceGroup
if (-not $federated) { Invoke-Az identity federated-credential create @federatedArguments | Out-Null; Write-Host "  created federated credential: $subject" }
elseif ($federated.subject -cne $subject) { Invoke-Az identity federated-credential update @federatedArguments | Out-Null; Write-Host "  updated federated credential: $subject (was $($federated.subject))" }
else { Write-Host "  ok      federated credential: $subject" }

# SEC: other federated credentials on this identity would be other ways in - report them.
$others = @(Invoke-Az identity federated-credential list --identity-name $IdentityName --resource-group $FoundationResourceGroup) |
    Where-Object { $_.name -ne $federatedCredentialName }
foreach ($other in $others) { Write-Warning "Unexpected federated credential '$($other.name)' (subject $($other.subject)) - remove it unless you added it on purpose." }

# ── 5. What the identity may do ────────────────────────────────────────────────────────────────
Write-Step 'Role assignments of the identity'
$roleIds = $delegableRoleNames | ForEach-Object { Get-RoleDefinitionId $_ }
$roleList = $roleIds -join ', '
$condition = @"
(
 (
  !(ActionMatches{'Microsoft.Authorization/roleAssignments/write'})
 )
 OR
 (
  @Request[Microsoft.Authorization/roleAssignments:RoleDefinitionId] ForAnyOfAnyValues:GuidEquals {$roleList}
  AND
  @Request[Microsoft.Authorization/roleAssignments:PrincipalType] ForAnyOfAnyValues:StringEqualsIgnoreCase {'ServicePrincipal'}
 )
)
AND
(
 (
  !(ActionMatches{'Microsoft.Authorization/roleAssignments/delete'})
 )
 OR
 (
  @Resource[Microsoft.Authorization/roleAssignments:RoleDefinitionId] ForAnyOfAnyValues:GuidEquals {$roleList}
  AND
  @Resource[Microsoft.Authorization/roleAssignments:PrincipalType] ForAnyOfAnyValues:StringEqualsIgnoreCase {'ServicePrincipal'}
 )
)
"@

Write-Host "  on $DemoResourceGroup"
Set-RoleAssignment $identity.principalId 'Contributor' $demoScope
Set-RoleAssignment $identity.principalId 'Role Based Access Control Administrator' $demoScope $condition
# SEC: data rights on the one container, not management rights on the account: the identity can
# read and write state blobs, but cannot change the account's settings, its lock or its network.
Write-Host "  on $StateStorageAccountName/$stateContainerName"
Set-RoleAssignment $identity.principalId 'Storage Blob Data Contributor' $stateContainerScope

# ── 6. DNS zones ───────────────────────────────────────────────────────────────────────────────
# ┌─ :: ARCHITECTURE :: Two domains, permanent zones, records per demo ───────────────────────────
# │ Why : EWP and the Partner Bank are two organisations: on two registrable domains the browser
# │       treats them as two sites (cookies, SameSite, CORS), as in production. A zone's name
# │       servers are fixed when it is created and are set by hand at the registrar, so the zones
# │       must outlive every demo; the gateway's address changes with every demo.
# │ How : The zones live in rg-ewp-foundation. Step 7's Terraform writes the A / CNAME records to
# │       the demo's gateway and its destroy removes them. Nothing in the cluster gets DNS rights
# │       (no external-dns); Let's Encrypt validates over HTTP-01.
# │ Ref : doc/Azure.md A8, A9 | Azure DNS "Delegate a domain to Azure DNS"
# └────────────────────────────────────────────────────────────────────────────────────────────────
# ┌─ :: SECURITY :: What the demo identity may do in the zones ───────────────────────────────────
# │ Why : DNS Zone Contributor would let a stolen token delete a zone (new name servers: the
# │       domain is dark until the registrar is changed), delegate a sub-domain to someone else's
# │       name servers (NS), or allow any certificate authority (CAA).
# │ How : A custom role, assignable only in rg-ewp-foundation and assigned per zone: read the
# │       zone, write and delete A and CNAME records. The apex CAA record (only Let's Encrypt may
# │       issue) is the owner's, set here. A CanNotDelete lock on the SOA record set stops a zone
# │       delete by anyone (Azure would have to delete the SOA with it) but, unlike a zone lock,
# │       leaves records deletable for terraform destroy.
# │ Ref : doc/Azure.md A9 | Azure DNS "Protecting DNS zones and records" | RFC 8659 (CAA)
# └────────────────────────────────────────────────────────────────────────────────────────────────
Write-Step 'DNS zones'
$dnsZones = @()
if (-not $EwpDomain) {
    Write-Host '  skipped (no -EwpDomain / -PartnerBankDomain; existing zones are kept)'
}
else {
    $dnsRoleDefinition = [ordered]@{
        Name             = $dnsRoleName
        IsCustom         = $true
        Description      = 'EWP cloud demo: writes the demo''s A and CNAME records. No NS, CAA or SOA records, no zone settings, no zone delete.'
        Actions          = @('Microsoft.Network/dnsZones/read', 'Microsoft.Network/dnsZones/A/*', 'Microsoft.Network/dnsZones/CNAME/*')
        NotActions       = @()
        AssignableScopes = @($foundationScope)
    }
    $dnsRole = @(Get-AzOrNull role definition list --name $dnsRoleName --custom-role-only true --scope $foundationScope) | Select-Object -First 1
    $wantedActions = ($dnsRoleDefinition.Actions | Sort-Object) -join ','
    $currentActions = if ($dnsRole) { (@($dnsRole.permissions[0].actions) | Sort-Object) -join ',' } else { '' }
    if ($dnsRole -and $currentActions -eq $wantedActions -and (@($dnsRole.assignableScopes) -join ',') -eq $foundationScope) {
        Write-Host "  ok      role '$dnsRoleName'"
    }
    else {
        $roleJson = if ($dnsRole) {
            # NOTE: the documented way to change a custom role: edit the definition "list" returned.
            $dnsRole.permissions[0].actions = $dnsRoleDefinition.Actions
            $dnsRole.assignableScopes = $dnsRoleDefinition.AssignableScopes
            $dnsRole.description = $dnsRoleDefinition.Description
            $dnsRole | ConvertTo-Json -Depth 6
        }
        else { $dnsRoleDefinition | ConvertTo-Json -Depth 4 }
        $roleFile = Write-TempFile $roleJson
        try {
            if ($dnsRole) { $dnsRole = Invoke-Az role definition update --role-definition "@$roleFile"; Write-Host "  updated role '$dnsRoleName'" }
            else { $dnsRole = Invoke-Az role definition create --role-definition "@$roleFile"; Write-Host "  created role '$dnsRoleName'" }
        }
        finally { Remove-Item $roleFile -ErrorAction SilentlyContinue }
    }

    foreach ($domain in @($EwpDomain, $PartnerBankDomain)) {
        $purpose = if ($domain -eq $EwpDomain) { 'dns-ewp' } else { 'dns-partner-bank' }
        $zone = Get-AzOrNull network dns zone show --name $domain --resource-group $FoundationResourceGroup
        if (-not $zone) {
            $zone = Invoke-Az network dns zone create --name $domain --resource-group $FoundationResourceGroup --tags "ewp-purpose=$purpose"
            Write-Host "  created zone $domain"
        }
        else { Write-Host "  ok      zone $domain" }

        # SEC: only Let's Encrypt may issue certificates for the domain and its sub-domains (no
        # issuewild record, so "issue" covers wildcards too). The identity cannot change CAA.
        $caa = Get-AzOrNull network dns record-set caa show --resource-group $FoundationResourceGroup --zone-name $domain --name '@'
        if (-not (@($caa.caaRecords) | Where-Object { $_.tag -eq 'issue' -and $_.value -eq $certificateAuthority })) {
            Invoke-Az network dns record-set caa add-record --resource-group $FoundationResourceGroup --zone-name $domain `
                --record-set-name '@' --flags 0 --tag issue --value $certificateAuthority | Out-Null
            Write-Host "          CAA issue $certificateAuthority added"
        }
        foreach ($other in @($caa.caaRecords | Where-Object { $_ -and -not ($_.tag -eq 'issue' -and $_.value -eq $certificateAuthority) })) {
            Write-Warning "$domain has another CAA record ($($other.flags) $($other.tag) $($other.value)) - remove it unless you added it on purpose."
        }

        # NOTE: a lock on the SOA record set, not on the zone: a zone lock is inherited by every
        # record set, so terraform destroy could not delete the demo's records. "az lock" cannot
        # address a record set, Resource Manager's REST API can.
        $lockUrl = "https://management.azure.com$($zone.id)/SOA/@/providers/Microsoft.Authorization/locks/$dnsLockName" + '?api-version=2016-09-01'
        $lockFile = Write-TempFile (@{ properties = @{ level = 'CanNotDelete'; notes = 'Keeps the EWP demo zone (and its name servers). Remove with Initialize-AzureDemo.ps1 -Remove.' } } | ConvertTo-Json -Depth 3)
        try { Invoke-Az rest --method put --url $lockUrl --body "@$lockFile" | Out-Null }
        finally { Remove-Item $lockFile -ErrorAction SilentlyContinue }
        Write-Host '          CanNotDelete lock on the SOA record set'

        Set-RoleAssignment $identity.principalId $dnsRoleName $zone.id -RoleId $dnsRole.name
        $dnsZones += [pscustomobject]@{ Name = $domain; NameServers = @($zone.nameServers) }
    }
}

# ── 7. Budget alert ────────────────────────────────────────────────────────────────────────────
# ┌─ :: ARCHITECTURE :: A budget on the whole subscription ──────────────────────────────────────
# │ Why : A demo left running (a failed teardown, a forgotten run) costs money every hour.
# │ How : A monthly cost budget on the subscription (not just the demo group: the AKS node group
# │       and anything made by hand count too), e-mailing at 50/80/100% actual and 100% forecast.
# │       It only warns; the demo workflows' own teardown and watchdog (step 7) stop the spending.
# │ Ref : doc/Azure.md A6 | Azure Cost Management budgets | FinOps "Anomaly management"
# └────────────────────────────────────────────────────────────────────────────────────────────────
Write-Step "Budget ($budgetName, $BudgetAmount per month)"
$existingBudget = Get-AzOrNull rest --method get --url $budgetUrl
# NOTE: a budget's start date cannot move later; keep the existing one on a re-run. Formatted with
# the invariant culture and literal separators: in a .NET format ":" is the CULTURE's time
# separator (en-IN gives "00.00.00", which Azure refuses); PowerShell 7 may also have turned the
# existing value into a DateTime.
$existingStart = if ($existingBudget) { $existingBudget.properties.timePeriod.startDate } else { $null }
$startMonth = if ($existingStart -is [DateTime]) { $existingStart.ToUniversalTime() }
    elseif ($existingStart) { [DateTime]::Parse($existingStart, [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::AdjustToUniversal) }
    else { (Get-Date).ToUniversalTime() }
$startDate = $startMonth.ToString("yyyy'-'MM'-01T00:00:00Z'", [Globalization.CultureInfo]::InvariantCulture)
function New-Notification([int]$Threshold, [string]$Type) {
    @{ enabled = $true; operator = 'GreaterThanOrEqualTo'; threshold = $Threshold; thresholdType = $Type; contactEmails = @($AlertEmail) }
}
$budget = @{
    properties = @{
        category      = 'Cost'
        amount        = $BudgetAmount
        timeGrain     = 'Monthly'
        timePeriod    = @{ startDate = $startDate }
        notifications = @{
            actual50    = New-Notification 50 'Actual'
            actual80    = New-Notification 80 'Actual'
            actual100   = New-Notification 100 'Actual'
            forecast100 = New-Notification 100 'Forecasted'
        }
    }
}
if ($existingBudget -and $existingBudget.eTag) { $budget.eTag = $existingBudget.eTag }
$budgetFile = Write-TempFile ($budget | ConvertTo-Json -Depth 6)
try { Invoke-Az rest --method put --url $budgetUrl --body "@$budgetFile" | Out-Null }
finally { Remove-Item $budgetFile -ErrorAction SilentlyContinue }
Write-Host "  ok      alerts to $AlertEmail"

# ── Hand-over ──────────────────────────────────────────────────────────────────────────────────
Write-Step 'Done. GitHub environment variables (Settings > Environments > azure-demo > Environment variables)'
# NOTE: these are identifiers, not secrets: without a token GitHub issues for this repository and
# environment they open nothing. Variables (not secrets) keep them readable in the logs.
$variables = [ordered]@{
    AZURE_CLIENT_ID               = $identity.clientId
    AZURE_TENANT_ID               = $tenantId
    AZURE_SUBSCRIPTION_ID         = $SubscriptionId
    AZURE_DEMO_RESOURCE_GROUP     = $DemoResourceGroup
    AZURE_TFSTATE_RESOURCE_GROUP  = $FoundationResourceGroup
    AZURE_TFSTATE_STORAGE_ACCOUNT = $StateStorageAccountName
    AZURE_TFSTATE_CONTAINER       = $stateContainerName
}
if ($dnsZones) {
    $variables.AZURE_DNS_RESOURCE_GROUP    = $FoundationResourceGroup
    $variables.AZURE_DNS_ZONE_EWP          = $EwpDomain
    $variables.AZURE_DNS_ZONE_PARTNER_BANK = $PartnerBankDomain
}
foreach ($key in $variables.Keys) { Write-Host ("  {0,-30} {1}" -f $key, $variables[$key]) }

if ($dnsZones) {
    Write-Step 'At each domain''s registrar, set the name servers to these (doc/Azure.md, "Domains")'
    foreach ($zone in $dnsZones) {
        Write-Host "  $($zone.Name)"
        foreach ($server in $zone.NameServers) { Write-Host "      $($server.TrimEnd('.'))" }
    }
    Write-Host '  Check: nslookup -type=NS <domain> 8.8.8.8 lists them (up to 48 hours, usually minutes).'
}
Write-Host "`nThen run the workflow 'Azure access check' on main (doc/Azure.md, 'Checking it')."