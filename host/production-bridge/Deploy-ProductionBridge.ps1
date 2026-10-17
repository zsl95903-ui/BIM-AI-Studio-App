[CmdletBinding()]
param(
    [string]$BridgeDll = "$(RepoRoot)\production-bridge\RevitAi.CodexBridge\bin\Release\RevitAi.CodexBridge.dll",
    [string]$BridgeManifest = "$(RepoRoot)\production-bridge\RevitAi.CodexBridge.addin",
    [string]$AddinsRoot = "$env:APPDATA\Autodesk\Revit\Addins\2027"
)

$ErrorActionPreference = "Stop"

if (Get-Process -Name Revit -ErrorAction SilentlyContinue) {
    throw "Close Revit before deploying the production bridge."
}

if (-not (Test-Path -LiteralPath $BridgeDll)) {
    throw "Bridge DLL not found: $BridgeDll"
}
if (-not (Test-Path -LiteralPath $BridgeManifest)) {
    throw "Bridge manifest not found: $BridgeManifest"
}

$bridgeDirectory = Join-Path $AddinsRoot "RevitAi.CodexBridge"
New-Item -ItemType Directory -Force -Path $bridgeDirectory | Out-Null
Copy-Item -LiteralPath $BridgeDll -Destination (Join-Path $bridgeDirectory "RevitAi.CodexBridge.dll") -Force
Copy-Item -LiteralPath $BridgeManifest -Destination (Join-Path $AddinsRoot "RevitAi.CodexBridge.addin") -Force

foreach ($manifestName in @(
    "RevitAiLegacy.addin",
    "RevitAi.Engine.addin",
    "RevitAi.ParityBridge.addin",
    "ExternalParityBridge.addin"
)) {
    $manifestPath = Join-Path $AddinsRoot $manifestName
    if (Test-Path -LiteralPath $manifestPath) {
        Move-Item -LiteralPath $manifestPath -Destination ($manifestPath + ".disabled") -Force
    }
}

Get-ChildItem -LiteralPath $AddinsRoot -Filter "*.addin" |
    Select-Object Name,Length,LastWriteTime |
    Sort-Object Name |
    Format-Table -AutoSize

