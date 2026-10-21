$ScriptFiles = @(
    "C:\Dinesh\Career\GitHub\EnterpriseWebPlatform_V3\ps\Reset.ps1",
    "C:\Dinesh\Career\GitHub\EnterpriseWebPlatform_V3\ps\kafka\Start-Kafka.ps1",
    "C:\Dinesh\Career\GitHub\EnterpriseWebPlatform_V3\\ps\temporal\Start-Temporal.ps1",
    "C:\Dinesh\Career\GitHub\EnterpriseWebPlatform_V3\ps\toxiproxy\Start-Toxiproxy.ps1",
    "C:\Dinesh\Career\GitHub\EnterpriseWebPlatform_V3\ps\build\CompileAndExportBFFClients_V3.ps1",
    "C:\Dinesh\Career\GitHub\EnterpriseWebPlatform_V3\ps\run\Start-NodeServices.ps1"
)

# Force-load them in sequence into your active ISE session
foreach ($File in $ScriptFiles) {
    if (Test-Path $File) {
        $psise.CurrentPowerShellTab.Files.Add($File)
    } else {
        Write-Warning "File not found: $File"
    }
}

cls;