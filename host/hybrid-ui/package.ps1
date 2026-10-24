[CmdletBinding()]
param(
    [string]$OutputDirectory = (Join-Path $env:TEMP 'bim-ai-studio-app/packages'),
    [string]$Configuration = 'Release',
    [switch]$SkipBuild
)
$ErrorActionPreference = 'Stop'
if (-not $SkipBuild) { & (Join-Path $PSScriptRoot 'build.ps1') -Configuration $Configuration }
$buildRoot = Join-Path $env:TEMP 'bim-ai-studio-app/bin'
$addin = Join-Path $buildRoot "RevitAi.Addin/$Configuration"
$worker = Join-Path $buildRoot "RevitAi.PdfWorker/$Configuration"
foreach ($required in @((Join-Path $addin 'RevitAi.Addin.dll'), (Join-Path $addin 'ReVitAI.Bridge.dll'), (Join-Path $worker 'RevitAi.PdfWorker.exe'))) {
    if (-not (Test-Path -LiteralPath $required)) { throw "Build output missing: $required" }
}
$root = [IO.Path]::GetFullPath($OutputDirectory)
$package = Join-Path $root ('BIM-AI-Studio-App-candidate-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
if (Test-Path -LiteralPath $package) { throw "Package directory already exists: $package" }
$payload = Join-Path $package 'RevitAi.HybridUI'
New-Item -ItemType Directory -Path $payload -Force | Out-Null
Get-ChildItem -LiteralPath $addin | Where-Object { $_.Extension -ne '.addin' } | Copy-Item -Destination $payload -Recurse
Copy-Item -LiteralPath $worker -Destination (Join-Path $payload 'PdfWorker') -Recurse
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'RevitAi.HybridUI.addin') -Destination $package
if (Get-ChildItem -LiteralPath $payload -Recurse -File -Filter 'RevitAPI*.dll') { throw 'Package must not include Autodesk API assemblies.' }
@"
Candidate build for Revit 2027 / .NET 10 x64. Not installed automatically.
Preserve the currently working plugin. Do not load old and new HybridUI assemblies together.
Test in an isolated Revit environment before switching the installed HybridUI.
Original RevitAi.addin must remain enabled; do not overwrite its Loader.
Contains the shared bridge, tool dependencies, WebView2 libraries and PDF worker.
Compilation and browser tests do not establish full real-model parity.
"@ | Set-Content -LiteralPath (Join-Path $package 'README.txt') -Encoding utf8
$zip = $package + '.zip'
Compress-Archive -Path (Join-Path $package '*') -DestinationPath $zip
Get-Item -LiteralPath $zip | Select-Object FullName, Length
