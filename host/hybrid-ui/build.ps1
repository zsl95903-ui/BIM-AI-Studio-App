[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$ToolLayerDir,
    [string]$RevitApiDir = "C:\Program Files\Autodesk\Revit 2027"
)

$ErrorActionPreference = "Stop"
$hostRoot = $PSScriptRoot
$repoRoot = (Resolve-Path (Join-Path $hostRoot "..\..")).Path

if ([string]::IsNullOrWhiteSpace($ToolLayerDir)) {
    $ToolLayerDir = Join-Path $repoRoot "external\BIM-AI-Studio\toolset"
}

$addinProject = Join-Path $hostRoot "RevitAi.Addin\RevitAi.Addin.csproj"
$pdfProject = Join-Path $hostRoot "RevitAi.PdfWorker\RevitAi.PdfWorker.csproj"
$generatorProject = Join-Path $ToolLayerDir "tools\ToolSetGenerator\ToolSetGenerator.csproj"
$catalogPath = Join-Path $ToolLayerDir "contracts\tool-contracts.json"
$generatedPath = Join-Path $env:TEMP "revitai-engine\generated"

if (-not (Test-Path -LiteralPath $catalogPath)) {
    throw "Tool contract catalog not found: $catalogPath"
}

dotnet run --project $generatorProject `
    -c $Configuration --nologo --disable-build-servers `
    -p:UseSharedCompilation=false -p:BuildInParallel=false `
    -- --catalog $catalogPath --out-tools $generatedPath --namespace ExtToolLib
if ($LASTEXITCODE -ne 0) {
    throw "Tool generation failed (exit=$LASTEXITCODE)."
}

$commonArguments = @(
    "-c", $Configuration,
    "--disable-build-servers",
    "-p:UseSharedCompilation=false",
    "-p:BuildInParallel=false",
    "-p:ToolLayerDir=$ToolLayerDir",
    "-p:RevitApiDir=$RevitApiDir"
)
$restoreArguments = @(
    "-p:UseSharedCompilation=false",
    "-p:BuildInParallel=false",
    "-p:ToolLayerDir=$ToolLayerDir",
    "-p:RevitApiDir=$RevitApiDir"
)

dotnet restore $addinProject @restoreArguments
if ($LASTEXITCODE -ne 0) {
    throw "RevitAi.Addin restore failed (exit=$LASTEXITCODE)."
}

dotnet build $addinProject @commonArguments --no-restore
if ($LASTEXITCODE -ne 0) {
    throw "RevitAi.Addin build failed (exit=$LASTEXITCODE)."
}

dotnet restore $pdfProject @restoreArguments
if ($LASTEXITCODE -ne 0) {
    throw "RevitAi.PdfWorker restore failed (exit=$LASTEXITCODE)."
}

dotnet build $pdfProject @commonArguments --no-restore
if ($LASTEXITCODE -ne 0) {
    throw "RevitAi.PdfWorker build failed (exit=$LASTEXITCODE)."
}
