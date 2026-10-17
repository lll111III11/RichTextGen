# Sign a PE file with the local code signing certificate.
# Usage: sign.ps1 <fileToSign>
# Silently no-ops when the certificate is absent, so a fresh clone can still build.
# NOTE: keep this script ASCII-only (Windows PowerShell 5.1 reads .ps1 as ANSI without BOM).
param(
    [Parameter(Mandatory=$true)][string]$File
)

$ErrorActionPreference = "Stop"

$SignDir = Join-Path (Split-Path -Parent $PSScriptRoot) "signing"
$pfx     = Join-Path $SignDir "rtg-codesign.pfx"
$pwFile  = Join-Path $SignDir "pfx-password.txt"

if (-not (Test-Path -LiteralPath $File)) { throw "file not found: $File" }
if (-not (Test-Path -LiteralPath $pfx) -or -not (Test-Path -LiteralPath $pwFile)) {
    Write-Output "[sign] certificate not found in $SignDir, skipping: $File"
    exit 0
}

# locate signtool (x86 build matches our x86 binaries)
$signtool = $null
$kits = "C:\Program Files (x86)\Windows Kits\10\bin"
if (Test-Path $kits) {
    $signtool = Get-ChildItem $kits -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match '\\x86\\' } |
        Sort-Object FullName -Descending |
        Select-Object -First 1 -ExpandProperty FullName
}
if (-not $signtool) {
    $cmd = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($cmd) { $signtool = $cmd.Source }
}
if (-not $signtool) {
    Write-Output "[sign] signtool.exe not found, skipping: $File"
    exit 0
}

$pw = (Get-Content -LiteralPath $pwFile -Raw).Trim()

& $signtool sign /fd SHA256 /f $pfx /p $pw `
    /d "Rich text & multifunctional tool" `
    /du "https://github.com/lll111III11/RichTextGen" `
    $File

if ($LASTEXITCODE -ne 0) { throw "signtool failed with exit code $LASTEXITCODE for $File" }
Write-Output "[sign] signed: $File"
exit 0
