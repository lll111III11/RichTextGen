param(
    [string]$PrimaryName = "richtextgen-dl",
    [string]$BackupName = "richtextgen-dl2",
    [string]$KvTitle = "DOWNLOADS",
    [switch]$NoBackup
)

# Deploy the static download site to Cloudflare Pages using wrangler OAuth.
# Prerequisite: run `npx wrangler@3 login` once (browser authorization).

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$pub = Join-Path $root "public"
$wrangler = @("--yes", "wrangler@3")

function Run-Wrangler {
    param([string[]]$CliArgs, [string]$WorkDir = $root)
    Push-Location $WorkDir
    $prev = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        $out = & npx @wrangler @CliArgs 2>&1 | Out-String
        return @{ Code = $LASTEXITCODE; Out = $out }
    } finally {
        $ErrorActionPreference = $prev
        Pop-Location
    }
}

Write-Host "[1/6] check login"
$who = Run-Wrangler -CliArgs @("whoami")
if ($who.Code -ne 0 -or $who.Out -notmatch "Account ID") {
    throw "wrangler not logged in. Run: npx wrangler@3 login"
}
Write-Host "       ok"

Write-Host "[2/6] resolve KV namespace"
$kvId = $null
$cfgPath = Join-Path $root "wrangler.toml"
if (Test-Path -LiteralPath $cfgPath) {
    $cfg = Get-Content -LiteralPath $cfgPath -Raw
    if ($cfg -match 'id\s*=\s*"([0-9a-f]{32})"') { $kvId = $Matches[1] }
    $nameInCfg = if ($cfg -match 'name\s*=\s*"([^"]+)"') { $Matches[1] } else { $null }
    if ($nameInCfg -and $nameInCfg -ne $PrimaryName) { $kvId = $null }
}
if (-not $kvId) {
    $res = Run-Wrangler -CliArgs @("kv:namespace", "create", $KvTitle)
    if ($res.Out -match '"id"\s*:\s*"([0-9a-f]{32})"') {
        $kvId = $Matches[1]
    } else {
        throw "could not create KV namespace. output:`n$($res.Out)"
    }
    Write-Host "       created: $kvId"
} else {
    Write-Host "       existing: $kvId"
}

Write-Host "[3/6] ensure Pages projects"
foreach ($name in @($PrimaryName)) {
    $res = Run-Wrangler -CliArgs @("pages", "project", "create", $name, "--production-branch", "main")
    if ($res.Out -match "Successfully created") { Write-Host "       created: $name" }
    elseif ($res.Out -match "already exists") { Write-Host "       exists: $name" }
    else { Write-Host "       warn: $name -> $($res.Out.Trim())" }
}
if (-not $NoBackup) {
    $res = Run-Wrangler -CliArgs @("pages", "project", "create", $BackupName, "--production-branch", "main")
    if ($res.Out -match "Successfully created") { Write-Host "       created: $BackupName" }
    elseif ($res.Out -match "already exists") { Write-Host "       exists: $BackupName" }
    else { Write-Host "       warn: $BackupName -> $($res.Out.Trim())" }
}

Write-Host "[4/6] write config and mirrors.json"
$toml = @"
name = "$PrimaryName"
compatibility_date = "2025-07-18"
pages_build_output_dir = "public"

[[kv_namespaces]]
binding = "DOWNLOADS"
id = "$kvId"
"@
[System.IO.File]::WriteAllText($cfgPath, $toml, (New-Object System.Text.UTF8Encoding($false)))

$primaryBase = "https://$PrimaryName.pages.dev/"
$mirrors = @(@{ id = "cf"; label = "Cloudflare 主线路"; tag = "pages.dev"; base = $primaryBase })
if (-not $NoBackup) {
    $mirrors += @{ id = "cf2"; label = "Cloudflare 备用线路"; tag = "pages.dev"; base = "https://$BackupName.pages.dev/" }
}
$mirrorJson = @{
    probePath = "files/probe.txt"
    counter = "https://$PrimaryName.pages.dev/api/count"
    mirrors = $mirrors
} | ConvertTo-Json -Depth 6
[System.IO.File]::WriteAllText((Join-Path $pub "mirrors.json"), $mirrorJson, (New-Object System.Text.UTF8Encoding($false)))
Write-Host "       counter: https://$PrimaryName.pages.dev/api/count"

Write-Host "[5/6] deploy primary"
$res = Run-Wrangler -CliArgs @("pages", "deploy", "public", "--project-name", $PrimaryName, "--branch", "main", "--commit-dirty=true")
Write-Host $res.Out.Trim()
if ($res.Code -ne 0) { throw "primary deploy failed" }

if (-not $NoBackup) {
    Write-Host "[6/6] deploy backup"
    $bk = Join-Path $root "backup"
    if (Test-Path -LiteralPath $bk) { Remove-Item -LiteralPath $bk -Recurse -Force }
    New-Item -ItemType Directory -Path $bk | Out-Null
    Copy-Item -LiteralPath $pub -Destination (Join-Path $bk "public") -Recurse
    $bkToml = @"
name = "$BackupName"
compatibility_date = "2025-07-18"
pages_build_output_dir = "public"
"@
    [System.IO.File]::WriteAllText((Join-Path $bk "wrangler.toml"), $bkToml, (New-Object System.Text.UTF8Encoding($false)))
    $res = Run-Wrangler -CliArgs @("pages", "deploy", "public", "--project-name", $BackupName, "--branch", "main", "--commit-dirty=true") -WorkDir $bk
    Write-Host $res.Out.Trim()
    if ($res.Code -ne 0) { throw "backup deploy failed" }
}

Write-Host ""
Write-Host "done:"
Write-Host "  primary: https://$PrimaryName.pages.dev/"
if (-not $NoBackup) { Write-Host "  backup : https://$BackupName.pages.dev/" }
