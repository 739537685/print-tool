param(
    [string]$Runtime = 'win-x64',
    [string]$Configuration = 'Release',
    [string]$Output = ''
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\PrintAssistant\PrintAssistant.csproj'

if (-not (Test-Path -LiteralPath $project)) {
    throw "Project file not found: $project"
}

if ([string]::IsNullOrWhiteSpace($Output)) {
    $Output = Join-Path $root 'dist'
}

$Output = [System.IO.Path]::GetFullPath($Output)

Write-Host "Publishing PrintAssistant"
Write-Host "  Runtime: $Runtime"
Write-Host "  Config:  $Configuration"
Write-Host "  Output:  $Output"

dotnet publish $project `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -o $Output

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

$exe = Join-Path $Output 'PrintAssistant.exe'
if (-not (Test-Path -LiteralPath $exe)) {
    throw "Publish finished but $exe was not found"
}

Get-ChildItem -LiteralPath $Output -Filter '*.pdb' -File | Remove-Item -Force

$size = [Math]::Round((Get-Item -LiteralPath $exe).Length / 1MB, 1)
Write-Host ""
Write-Host "Published: $exe ($size MB)" -ForegroundColor Green
Write-Host "Copy the whole output folder to the target machine." -ForegroundColor Green
