param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[a-p]{32}$')]
    [string]$ExtensionId,
    [ValidateSet('Chrome', 'Edge')]
    [string]$Browser = 'Chrome'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$bridge = Join-Path $root 'src\AiCockpit.Bridge\bin\Debug\net9.0\AiCockpit.Bridge.exe'
if (-not (Test-Path $bridge)) {
    dotnet build (Join-Path $root 'src\AiCockpit.Bridge\AiCockpit.Bridge.csproj') -c Debug
    if ($LASTEXITCODE -ne 0) { throw 'Bridge build failed' }
}
$manifestPath = Join-Path $PSScriptRoot ("native-host-" + $Browser.ToLowerInvariant() + ".json")
$manifest = @{
    name = 'com.aicockpit.bridge'
    description = 'Local browser tabs to AI Cockpit bridge'
    path = $bridge
    type = 'stdio'
    allowed_origins = @("chrome-extension://$ExtensionId/")
}
$json = $manifest | ConvertTo-Json -Depth 5
[System.IO.File]::WriteAllText($manifestPath, $json, (New-Object System.Text.UTF8Encoding($false)))
$path = if ($Browser -eq 'Edge') {
    'Software\Microsoft\Edge\NativeMessagingHosts\com.aicockpit.bridge'
} else {
    'Software\Google\Chrome\NativeMessagingHosts\com.aicockpit.bridge'
}
$key = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($path)
try { $key.SetValue('', $manifestPath) } finally { $key.Dispose() }
Write-Host "Registered $Browser Native Messaging host for extension $ExtensionId"
Write-Host "Restart the browser extension or browser. AI Cockpit must be running."
