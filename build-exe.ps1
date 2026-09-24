# ---------------------------------------------------------------------------
# Swifter Browser - standalone single file publisher
# Produces .\dist\Swifter.exe (self contained, win-x64, single file)
# ---------------------------------------------------------------------------
param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$OutDir = "./dist"
)

$ErrorActionPreference = "Stop"

Write-Host "Swifter :: publishing $Configuration / $Runtime -> $OutDir" -ForegroundColor Cyan

dotnet publish -c $Configuration -r $Runtime --self-contained true `
    /p:PublishSingleFile=true `
    /p:IncludeNativeLibrariesForSelfExtract=true `
    -o $OutDir

if ($LASTEXITCODE -ne 0) {
    Write-Host "Publish FAILED with exit code $LASTEXITCODE" -ForegroundColor Red
    exit $LASTEXITCODE
}

$exe = Join-Path $OutDir "Swifter.exe"
if (Test-Path $exe) {
    $size = [math]::Round((Get-Item $exe).Length / 1MB, 2)
    Write-Host "OK -> $exe ($size MB)" -ForegroundColor Green
} else {
    Write-Host "Expected output $exe was not produced." -ForegroundColor Red
    exit 1
}
