<#
.SYNOPSIS
    Secures the local Kafka broker (C:\Kafka, KRaft) for EWP V3: SCRAM-SHA-512
    authentication, one Kafka user per deployable, least-privilege ACLs, and no
    topic auto-creation.

.DESCRIPTION
    Run the phases in order. Each phase checks its precondition and is safe to re-run.

      1. -Phase Prepare   Kafka RUNNING (still PLAINTEXT, no authorizer):
                          creates the SCRAM users and all topics, and points the new
                          consumer groups at the end of their topics.
      2. (stop Kafka)
         -Phase Secure    Kafka STOPPED: backs up server.properties and switches the
                          client listener to SASL_PLAINTEXT with the StandardAuthorizer;
                          writes config\admin.properties for the CLI tools.
      3. (start Kafka)
         -Phase Acls      Kafka RUNNING with SASL: grants each user only what it needs
                          and prints the resulting ACLs.

      -Revert             Kafka STOPPED: restores the most recent server.properties backup.

    The passwords below are DEVELOPMENT-ONLY. Each component reads its own password
    from its appsettings.Development.json (Kafka:SaslPassword); outside Development
    they come from the environment or a secret store.

    Local development uses SASL_PLAINTEXT on localhost: SCRAM never sends the password
    itself, but the data is not encrypted. Production uses SASL_SSL (or mTLS).

.EXAMPLE
    .\kafka\Setup-KafkaSecurity.ps1 -Phase Prepare
#>
[CmdletBinding()]
param(
    [ValidateSet('Prepare', 'Secure', 'Acls')]
    [string] $Phase,

    [switch] $Revert,

    [string] $KafkaHome = 'C:\Kafka'
)

$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------------------------------
# Users (one per deployable) - DEVELOPMENT-ONLY passwords
# ---------------------------------------------------------------------------------------
$Users = [ordered]@{
    'admin'                             = 'ewp-kafka-admin-dev'              # super user: broker + CLI tools only
    'ewp-co-outbox-relay'               = 'ewp-co-outbox-relay-kafka-dev'    # CustomerOutboxPublisher
    'ewp-kyc-api'                       = 'ewp-kyc-api-kafka-dev'            # Customer KYC API (in-process relay)
    'ewp-kyc-case-opening-subscriber'   = 'ewp-kyc-case-opening-kafka-dev'   # KycCaseOpeningSubscriber
    'ewp-onboarding-outcome-subscriber' = 'ewp-onboarding-outcome-kafka-dev' # OnboardingOutcomeSubscriber
    'ewp-kafka-ui'                      = 'ewp-kafka-ui-dev'                 # Kafka UI (read-only)
}

$Topics = @(
    'customer.created',
    'onboarding.application.submitted',
    'onboarding.application.status.changed',
    'kyc.case.created',
    'kyc.case.approved',
    'kyc.case.rejected',
    'kyc.identity.verification.approved',
    'kyc.identity.verification.rejected',
    'kyc.document.verification.approved',
    'kyc.document.verification.rejected',
    'customer-kyc.case-opening-subscriber.dlq',
    'customer-onboarding.outcome-subscriber.dlq'
)

# Consumer groups introduced with these security settings, and the topics they read.
$NewGroups = [ordered]@{
    'customer-kyc.case-opening-subscriber'  = @('onboarding.application.submitted')
    'customer-onboarding.outcome-subscriber' = @('kyc.case.created', 'kyc.case.approved', 'kyc.case.rejected')
}

$Bootstrap   = 'localhost:9092'
$Bin         = Join-Path $KafkaHome 'bin\windows'
$ServerProps = Join-Path $KafkaHome 'config\server.properties'
$AdminProps  = Join-Path $KafkaHome 'config\admin.properties'

# On Windows the Java launcher expands a "*" argument into the file names of the
# CURRENT folder (quotes do not help: the .bat wrapper strips them), so
# "--topic *" from the repository root became "--topic .git". The tools therefore
# run from an empty folder, where "*" stays literal.
$ToolWorkingDir = Join-Path $env:TEMP 'ewp-kafka-tools'
New-Item -ItemType Directory -Force $ToolWorkingDir | Out-Null

function Invoke-KafkaTool([string] $Tool, [string] $Arguments, [switch] $AllowFailure) {
    $exe = Join-Path $Bin $Tool
    $output = cmd.exe /d /c "cd /d `"$ToolWorkingDir`" && `"$exe`" $Arguments 2>&1"
    if ($LASTEXITCODE -ne 0 -and -not $AllowFailure) {
        throw "$Tool failed (exit $LASTEXITCODE):`n$($output -join "`n")"
    }
    return $output
}

function Test-KafkaPort {
    $client = New-Object System.Net.Sockets.TcpClient
    try { $client.Connect('localhost', 9092); return $true } catch { return $false } finally { $client.Dispose() }
}

function Set-Property([string[]] $Lines, [string] $Key, [string] $Value) {
    $pattern = '^\s*#?\s*' + [regex]::Escape($Key) + '\s*='
    $found = $false
    $result = @(foreach ($line in $Lines) {
        if (-not $found -and $line -match $pattern) { $found = $true; "$Key=$Value" }
        elseif ($found -and $line -match ('^\s*' + [regex]::Escape($Key) + '\s*=')) { }   # drop duplicates
        else { $line }
    })
    if (-not $found) { $result += "$Key=$Value" }
    return ,$result
}

# =======================================================================================
if ($Revert) {
    if (Test-KafkaPort) { throw 'Stop Kafka before reverting (Ctrl+C in its window).' }
    $backup = Get-ChildItem (Join-Path $KafkaHome 'config') -Filter 'server.properties.pre-sasl-*' |
        Sort-Object Name -Descending | Select-Object -First 1
    if (-not $backup) { throw 'No server.properties.pre-sasl-* backup found.' }
    Copy-Item $backup.FullName $ServerProps -Force
    Write-Host "Restored $($backup.Name) -> server.properties. Start Kafka; it runs PLAINTEXT without an authorizer again." -ForegroundColor Green
    Write-Host 'Set Kafka:SecurityProtocol to "Plaintext" in the components (or remove the SASL settings) to match.'
    return
}

if (-not $Phase) { throw 'Specify -Phase Prepare | Secure | Acls (in that order), or -Revert.' }

switch ($Phase) {

# ---------------------------------------------------------------------------------------
'Prepare' {
    if (-not (Test-KafkaPort)) { throw 'Kafka is not running on localhost:9092. Start it (StartKafka.bat) and re-run.' }

    Write-Host '1/3  SCRAM-SHA-512 users' -ForegroundColor Cyan
    foreach ($user in $Users.Keys) {
        Invoke-KafkaTool 'kafka-configs.bat' "--bootstrap-server $Bootstrap --alter --entity-type users --entity-name $user --add-config `"SCRAM-SHA-512=[iterations=8192,password=$($Users[$user])]`"" | Out-Null
        Write-Host "     $user"
    }

    Write-Host '2/3  Topics (explicit; auto-creation will be disabled)' -ForegroundColor Cyan
    foreach ($topic in $Topics) {
        Invoke-KafkaTool 'kafka-topics.bat' "--bootstrap-server $Bootstrap --create --if-not-exists --topic $topic --partitions 1 --replication-factor 1" | Out-Null
        Write-Host "     $topic"
    }

    Write-Host '3/3  New consumer groups start at the end of their topics' -ForegroundColor Cyan
    Write-Host '     (a brand-new group would otherwise replay every retained message)'
    foreach ($group in $NewGroups.Keys) {
        $describe = Invoke-KafkaTool 'kafka-consumer-groups.bat' "--bootstrap-server $Bootstrap --describe --group $group" -AllowFailure
        # Older Kafka: "... does not exist"; Kafka 4.x: GroupIdNotFoundException "Group ... not found".
        if (($describe -join "`n") -match 'does not exist|GroupIdNotFound|not found') {
            foreach ($topic in $NewGroups[$group]) {
                Invoke-KafkaTool 'kafka-consumer-groups.bat' "--bootstrap-server $Bootstrap --group $group --reset-offsets --to-latest --topic $topic --execute" | Out-Null
            }
            Write-Host "     $group -> latest"
        } else {
            Write-Host "     $group already has offsets; left unchanged"
        }
    }

    Write-Host "`nNext: stop Kafka (Ctrl+C in its window), then run:  .\kafka\Setup-KafkaSecurity.ps1 -Phase Secure" -ForegroundColor Green
}

# ---------------------------------------------------------------------------------------
'Secure' {
    if (Test-KafkaPort) { throw 'Stop Kafka first (Ctrl+C in its window): server.properties is read only at start-up.' }

    $backup = "$ServerProps.pre-sasl-$(Get-Date -Format 'yyyyMMdd-HHmmss')"
    Copy-Item $ServerProps $backup
    Write-Host "Backup: $backup" -ForegroundColor Cyan

    $adminPassword = $Users['admin']
    $lines = [string[]](Get-Content $ServerProps)
    $settings = [ordered]@{
        # Clients authenticate with SCRAM; both listeners bound to localhost only.
        'listeners'                            = 'SASL_PLAINTEXT://localhost:9092,CONTROLLER://localhost:9093'
        'advertised.listeners'                 = 'SASL_PLAINTEXT://localhost:9092'
        'inter.broker.listener.name'           = 'SASL_PLAINTEXT'
        'sasl.enabled.mechanisms'              = 'SCRAM-SHA-512'
        'sasl.mechanism.inter.broker.protocol' = 'SCRAM-SHA-512'
        'listener.name.sasl_plaintext.scram-sha-512.sasl.jaas.config' =
            "org.apache.kafka.common.security.scram.ScramLoginModule required username=`"admin`" password=`"$adminPassword`";"
        # Deny by default; only ACLs grant access.
        'authorizer.class.name'                = 'org.apache.kafka.metadata.authorizer.StandardAuthorizer'
        'allow.everyone.if.no.acl.found'       = 'false'
        # admin: the broker itself and the CLI tools. ANONYMOUS: the broker's own requests
        # to the controller over the CONTROLLER listener (PLAINTEXT, localhost only).
        'super.users'                          = 'User:admin;User:ANONYMOUS'
        # Topics are created explicitly; a mistyped name must fail, not create a topic.
        'auto.create.topics.enable'            = 'false'
    }
    foreach ($key in $settings.Keys) { $lines = Set-Property $lines $key $settings[$key] }
    Set-Content -Path $ServerProps -Value $lines -Encoding ASCII

    Set-Content -Path $AdminProps -Encoding ASCII -Value @(
        '# Kafka CLI tools as the admin super user (DEVELOPMENT ONLY), e.g.:',
        '#   bin\windows\kafka-topics.bat --bootstrap-server localhost:9092 --command-config config\admin.properties --list',
        'security.protocol=SASL_PLAINTEXT',
        'sasl.mechanism=SCRAM-SHA-512',
        "sasl.jaas.config=org.apache.kafka.common.security.scram.ScramLoginModule required username=`"admin`" password=`"$adminPassword`";"
    )

    Write-Host 'server.properties now uses SASL_PLAINTEXT + StandardAuthorizer; config\admin.properties written.' -ForegroundColor Green
    Write-Host "Next: start Kafka (StartKafka.bat), then run:  .\kafka\Setup-KafkaSecurity.ps1 -Phase Acls" -ForegroundColor Green
}

# ---------------------------------------------------------------------------------------
'Acls' {
    if (-not (Test-KafkaPort)) { throw 'Kafka is not running on localhost:9092. Start it (StartKafka.bat) and re-run.' }
    if (-not (Test-Path $AdminProps)) { throw "Run -Phase Secure first ($AdminProps is missing)." }

    $base = "--bootstrap-server $Bootstrap --command-config `"$AdminProps`" --add"
    function Grant([string] $Principal, [string] $What) {
        Invoke-KafkaTool 'kafka-acls.bat' "$base --allow-principal User:$Principal $What" | Out-Null
    }

    Write-Host 'Customer Onboarding Outbox relay: write its three topics' -ForegroundColor Cyan
    foreach ($t in 'customer.created', 'onboarding.application.submitted', 'onboarding.application.status.changed') {
        Grant 'ewp-co-outbox-relay' "--operation Write --operation Describe --topic $t"
    }

    Write-Host 'Customer KYC API (in-process relay): write kyc.* only' -ForegroundColor Cyan
    Grant 'ewp-kyc-api' '--operation Write --operation Describe --resource-pattern-type prefixed --topic kyc.'

    Write-Host 'KycCaseOpeningSubscriber: read its topic and group; write its dead-letter topic' -ForegroundColor Cyan
    Grant 'ewp-kyc-case-opening-subscriber' '--operation Read --operation Describe --topic onboarding.application.submitted'
    Grant 'ewp-kyc-case-opening-subscriber' '--operation Read --group customer-kyc.case-opening-subscriber'
    Grant 'ewp-kyc-case-opening-subscriber' '--operation Write --operation Describe --topic customer-kyc.case-opening-subscriber.dlq'

    Write-Host 'OnboardingOutcomeSubscriber: read its topics and group; write its dead-letter topic' -ForegroundColor Cyan
    foreach ($t in 'kyc.case.created', 'kyc.case.approved', 'kyc.case.rejected') {
        Grant 'ewp-onboarding-outcome-subscriber' "--operation Read --operation Describe --topic $t"
    }
    Grant 'ewp-onboarding-outcome-subscriber' '--operation Read --group customer-onboarding.outcome-subscriber'
    Grant 'ewp-onboarding-outcome-subscriber' '--operation Write --operation Describe --topic customer-onboarding.outcome-subscriber.dlq'

    # Clean-up of an earlier run where "*" was expanded to ".git" (see Invoke-KafkaTool).
    Invoke-KafkaTool 'kafka-acls.bat' "--bootstrap-server $Bootstrap --command-config `"$AdminProps`" --remove --force --topic .git" -AllowFailure | Out-Null
    Invoke-KafkaTool 'kafka-acls.bat' "--bootstrap-server $Bootstrap --command-config `"$AdminProps`" --remove --force --group .git" -AllowFailure | Out-Null

    Write-Host 'Kafka UI: read-only view of everything (no Write, no Alter, no Delete)' -ForegroundColor Cyan
    Grant 'ewp-kafka-ui' '--operation Read --operation Describe --operation DescribeConfigs --topic *'
    Grant 'ewp-kafka-ui' '--operation Read --operation Describe --group *'
    Grant 'ewp-kafka-ui' '--operation Describe --operation DescribeConfigs --cluster'

    Write-Host "`nResulting ACLs:" -ForegroundColor Cyan
    Invoke-KafkaTool 'kafka-acls.bat' "--bootstrap-server $Bootstrap --command-config `"$AdminProps`" --list"

    Write-Host "`nDone. Every EWP component now authenticates as its own Kafka user, limited to its own topics and group." -ForegroundColor Green
}
}
