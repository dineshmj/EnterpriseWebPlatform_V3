# Azure demo: the bootstrap

What has to exist in Azure before a GitHub workflow can build EWP's demo there and tear it down
again: two resource groups, a store for the Terraform state, the identity the workflows sign in
as, and a budget alert. One script, [Initialize-AzureDemo.ps1](../deploy/azure/Initialize-AzureDemo.ps1),
run once by the subscription's owner, creates all of it. One workflow,
[Azure access check](../.github/workflows/azure-access-check.yml), proves the result.

In short: **GitHub signs in to Azure without any stored secret. Azure accepts the sign-in only
from this repository's `azure-demo` environment, which runs only from `main` and only after a
person approves. The identity may change one resource group (the throw-away one) and the
Terraform state, and nothing else in the subscription.**

This is step 5 of the cloud demo. Step 6 adds the domains (DNS zones in the permanent group),
step 7 the Terraform code and the demo up / down workflows ([Kubernetes.md](Kubernetes.md) is
what they install).

## Decisions

| # | Decision | Why |
|---|---|---|
| A1 | **OIDC federation, no secret.** A user-assigned managed identity with one federated credential: Entra ID accepts GitHub's short-lived token only for subject `repo:dineshmj@1352986/EnterpriseWebPlatform_V3@1377215876:environment:azure-demo` (owner and repository with their numeric ids, as GitHub issues it) and audience `api://AzureADTokenExchange` | A client secret in GitHub is a long-lived key that works from anywhere until rotated. A managed identity cannot even have one. A managed identity (not an app registration) needs no directory rights to create and lives in the permanent group |
| A2 | **Rights on two fixed resource groups, none on the subscription** (scope B, decided 24-OCT). `rg-ewp-foundation` is permanent; `rg-ewp-demo` always exists, a demo fills it and the teardown empties it | A workflow that may create resource groups needs subscription rights, and then it can reach everything in the subscription. With fixed groups the most a stolen token can damage is the demo |
| A3 | **No management rights on the permanent group.** The identity has only Storage Blob Data Contributor on the `tfstate` container | The permanent group holds the identity itself. Contributor there would let the workflow add a federated credential to its own identity (a new way in) or delete the state |
| A4 | **Contributor on `rg-ewp-demo`, plus Role Based Access Control Administrator limited by a condition**: it may create and delete only Network Contributor, Key Vault Secrets User / Certificate User / Secrets Officer and AKS RBAC Cluster Admin, and only for principals of type ServicePrincipal | Terraform must grant the cluster's identity its subnet, the pods' identity the Key Vault, and itself the cluster. Unconditioned, the same role could make the workflow Owner or give a person access |
| A5 | **Terraform state in Azure Storage with shared keys off**: Entra ID sign-in only (`use_azuread_auth`), TLS 1.2+, no anonymous blobs, blob versions and 30-day soft delete, a CanNotDelete lock. Public endpoint, because GitHub's hosted runners have no fixed addresses | The state lists every resource and can hold generated secrets. With shared keys off there is no account key to leak. Versions bring back a corrupted state, and the lock stops the account being deleted by accident |
| A6 | **A monthly budget on the whole subscription** with e-mails at 50, 80 and 100% spent and 100% forecast | A forgotten demo costs money every hour. The subscription scope catches the AKS node group and anything made by hand too. It only warns: the teardown and the watchdog (step 7) stop the spending |
| A7 | **The bootstrap is a script a person runs, not Terraform.** Run it again to repair anything that drifted; `-Remove` undoes all of it | It needs the owner's rights (role assignments, subscription budget, provider registration), which no workflow should hold. Terraform would also need somewhere to keep the state of the thing that holds the state |

## What exists afterwards

| Resource | Group | Notes |
|---|---|---|
| `rg-ewp-foundation` | (permanent) | Tag `ewp-purpose=foundation` |
| `stewptf<8 hex>` storage account, container `tfstate` | foundation | The name comes from the subscription id, so it is the same on every run (`-StateStorageAccountName` if it is taken). Lock `ewp-keep-terraform-state` |
| `id-ewp-github-demo` managed identity | foundation | Federated credential `github-azure-demo`. The script warns about any other federated credential it finds |
| `rg-ewp-demo` | (throw-away) | Empty after the bootstrap and after every teardown |
| Role assignments of the identity | | `rg-ewp-demo`: Contributor, Role Based Access Control Administrator (with the condition); `tfstate` container: Storage Blob Data Contributor |
| Budget `ewp-demo-monthly` | subscription | Amount in the subscription's **billing currency** (required; an Indian account is billed in INR) |
| Resource providers registered | subscription | Compute, ContainerService, DBforPostgreSQL, KeyVault, ManagedIdentity, Network, OperationalInsights, Insights, Storage, Consumption |

Cost while no demo runs: a few cents a month (the state blobs). The identity, the budget and
empty resource groups cost nothing.

## Running it

You need an Azure subscription where you are **Owner**. The steps below are for the laptop
(Windows PowerShell 5.1). Azure Cloud Shell (PowerShell) works too: the CLI and the sign-in are
already there, so go straight to step 3 with `./Initialize-AzureDemo.ps1`.

1. **Install the Azure CLI**, once, in an **Administrator** PowerShell:

   ```powershell
   winget install Microsoft.AzureCLI
   ```

   Then **close that window**. Only a new window finds the `az` command.

2. **Sign in**, in a normal (not Administrator) PowerShell at the repository root. Name the
   subscription's tenant:

   ```powershell
   az login --tenant <tenant id>
   az account show --query "{name:name, subscription:id, tenant:tenantId}" -o table
   ```

   Windows shows its own account list instead of a browser page (the CLI signs in through
   Windows' account manager). Choose the account and complete the MFA prompt. The second command
   must show the intended subscription and tenant.

   Why `--tenant`: an account can belong to several directories (tenants), often all called
   "Default Directory". A plain `az login` signs in to all of them silently and skips any that
   demand MFA, so it can report "No subscriptions found" although the subscription exists.
   The tenant id is in that message (`az login --tenant TENANT_ID`), or in the Azure portal under
   Microsoft Entra ID → Overview → Tenant ID (switch to the subscription's directory first).

3. **Run the script.** `-ExecutionPolicy Bypass` because Windows blocks local scripts by default,
   and `-SubscriptionId` so it can never pick another directory's subscription:

   ```powershell
   powershell -ExecutionPolicy Bypass -File .\deploy\azure\Initialize-AzureDemo.ps1 `
       -SubscriptionId <subscription id> -AlertEmail <your e-mail> -BudgetAmount <monthly amount>
   ```

   **`-BudgetAmount` is in the subscription's billing currency**, not in dollars: an account in
   India is billed in INR, so `420` means ₹420 a month (this project's ₹5,000 a year). It is
   required, so the amount is always chosen on purpose.

   Optional: `-Location` (default `australiaeast`), `-GitHubRepository`, `-GitHubEnvironment`,
   `-StateStorageAccountName`. It takes 3-5 minutes. Each step prints `ok`, `created` or
   `removed`. It ends with the seven values for GitHub. If it stops, fix the cause and run it
   again: the finished steps only say `ok`.

The subject is compared **exactly, including upper and lower case**. GitHub puts the owner's and
the repository's numeric ids into it (`repo:<owner>@<id>/<repo>@<id>:environment:<environment>`).
The script reads them, and the exact spelling, from `api.github.com` (for a private repository,
pass `-GitHubOwnerId` and `-GitHubRepositoryId`). A repository deleted and re-created under the
same name gets a new id and cannot sign in. A renamed repository or environment means running the
script again.

### When something goes wrong

| What you see | Meaning | What to do |
|---|---|---|
| Red `az : WARNING: …` with `NativeCommandError` | Windows PowerShell 5.1 shows everything `az` writes as a warning in red | Nothing. Only lines starting with `ERROR:` are failures |
| `AADSTS50076 … you must use multi-factor authentication`, then `No subscriptions found` | The subscription's tenant needs MFA, which a plain `az login` skips | `az login --tenant <tenant id>` (step 2) |
| `az : The term 'az' is not recognized` | The window was open before the install | Open a new PowerShell window |
| `Storage account name '…' is taken` | Someone else in Azure has the generated name | `-StateStorageAccountName <3-24 lower-case letters and digits>` |
| `Resource providers … Registering` | Normal on a fresh subscription | Nothing. It finishes in the background within minutes |
| Workflow: `AADSTS700213: No matching federated identity record found for presented assertion subject '…'` | The federated credential's subject differs from the token's (the run log prints the token's "subject claim") | Run the script again: it rebuilds the subject from GitHub's API and prints `updated federated credential`. Compare it with the run log's line |
| `waiting for the identity to replicate (n/5)` | A new managed identity takes up to a minute to reach every Entra ID replica | Nothing. The script retries |

### First run (25-OCT-2026)

The first run against a real subscription (Pay-As-You-Go Dev/Test, `australiaeast`) found three
faults in the script. All are fixed. The third run of the script finished with every step `ok`,
and the first access check found the third fault:

| Fault | Cause | Fix in the script |
|---|---|---|
| `unrecognized arguments: --default-to-oauth-authentication` | The Azure CLI has no such option for storage accounts | Removed: the setting only picks the portal's default sign-in, and with shared keys off Entra ID is the only one anyway. Every other `az` option the script and the access check use was then checked against `az … --help` of the installed CLI |
| Budget: `Please enter a valid start date` | In a .NET date format `:` means the culture's time separator. On an `en-IN` machine that is `.`, so the script sent `2026-10-01T00.00.00Z` | The start date is formatted with the invariant culture and literal separators (`yyyy'-'MM'-01T00:00:00Z'`). An existing budget's date is re-read whether PowerShell returns a string or a DateTime |
| Access check: `AADSTS700213 … No matching federated identity record` | The credential had the old name-only subject (`repo:dineshmj/EnterpriseWebPlatform_V3:…`); GitHub issues the subject with numeric ids (`repo:dineshmj@1352986/EnterpriseWebPlatform_V3@1377215876:…`) | The script reads both ids from `api.github.com` and builds GitHub's form. It was compared with the token's subject in the run log (identical) |

Lesson for every script here: format dates sent to an API with `CultureInfo.InvariantCulture`,
and quote the separators.

## GitHub side

Repository **Settings → Environments → New environment** `azure-demo`:

| Setting | Value | Why |
|---|---|---|
| Required reviewers | You | Every run that signs in to Azure waits for an approval, so a changed workflow cannot spend money or reach Azure by itself |
| Prevent self-review | **Off** | One developer: on, nobody could approve |
| Allow administrators to bypass configured protection rules | **Off** (on by default: untick it, then **Save protection rules**) | The owner is the repository's administrator: with bypass on, the owner's runs could skip the approval |
| Deployment branches and tags | **Selected branches and tags**: `main` | Only reviewed, merged code may use the identity. A pull request branch cannot ask for the environment |
| Environment secrets | **None** | There is nothing secret (A1) |
| Environment variables | The seven the script printed: `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID`, `AZURE_DEMO_RESOURCE_GROUP`, `AZURE_TFSTATE_RESOURCE_GROUP`, `AZURE_TFSTATE_STORAGE_ACCOUNT`, `AZURE_TFSTATE_CONTAINER` | Identifiers, not secrets. Without a token GitHub issues for this repository and environment they open nothing |

## Checking it

**Actions → Azure access check → Run workflow** (branch `main`), then approve the deployment.
The run's summary lists each check. All of them passed on 25-OCT-2026, after the subject fix
(First run):

| Kind | Check |
|---|---|
| may | read `rg-ewp-demo`; tag it (Contributor) |
| may | write and read a blob in `tfstate` (deleted again) |
| must not | see any resource group but `rg-ewp-demo` |
| must not | create a resource group |
| must not | read the state account's settings or list its keys |
| must not | make itself Owner of `rg-ewp-demo` (the condition refuses; if it ever did not, the check removes the grant and fails) |

A run from any other branch does not reach Azure: GitHub refuses the environment.

## Removing it

```text
.\deploy\azure\Initialize-AzureDemo.ps1 -Remove
```

It asks first, then deletes the budget, the identity's role assignments, the lock and both
resource groups, **including the Terraform state**. Delete the GitHub environment by hand.

## Known gaps

| Gap | Effect | Closed by |
|---|---|---|
| Contributor on `rg-ewp-demo` includes deleting the group itself | A broken or hostile run could delete the group. It could not create it again, and its role assignments would go with it | Run the bootstrap again. The teardown (step 7) empties the group with `terraform destroy` and never deletes it |
| Inside `rg-ewp-demo` the identity may create managed identities and federated credentials (workload identity needs both) | A stolen token could leave a way back in, through an identity in the demo group federated to another issuer. It would reach no further than the demo group, and only until the next teardown | The teardown empties the group. Step 7's demo-down workflow checks the group is empty. A required reviewer approves every run |
| The budget warns but does not stop anything | A demo nobody tears down keeps costing until someone acts on the e-mail | Step 7: teardown at the end of every run, plus a scheduled watchdog that destroys anything older than the allowed time |
| The state account has a public endpoint | Reachable from the internet. Entra ID sign-in is still required, and no key exists | Self-hosted runners or a private endpoint, out of scope for the demo |
| DNS zones | No domains yet | Step 6 (zones in `rg-ewp-foundation`, DNS Zone Contributor on them only) |