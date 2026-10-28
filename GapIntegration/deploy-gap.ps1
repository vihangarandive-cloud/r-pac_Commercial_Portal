<#
.SYNOPSIS
    Deploy this folder into the SO-GEN site and PROVE the worker loaded it.

.DESCRIPTION
    Copying files is not a deployment: IIS keeps the old module in memory, a
    stray __pycache__ wins over the new source, and a venv built on another PC
    has no packages. This script does the whole sequence and then compares
    portal_api.SOURCE_SHA (computed with the interpreter IIS runs) with the
    portal_api_sha the running worker reports on /api/health. It exits 1 when
    the server is still answering with another copy - which is exactly the
    situation where "I deployed the fix and nothing changed" happens.

    .env is never copied over: the server keeps its own credentials. The script
    only checks that it exists.

.EXAMPLE
    .\deploy-gap.ps1 -Target C:\inetpub\<site>\GapIntegration -AppPool "SO-GEN Gap" `
                     -HealthUrl https://so-gen/gap/api/health

.EXAMPLE
    # copy only, no pip, no IIS restart (e.g. testing on a workstation)
    .\deploy-gap.ps1 -Target C:\gap-test -SkipPip
#>
[CmdletBinding()]
param(
    [string]$Source = $PSScriptRoot,
    [Parameter(Mandatory = $true)][string]$Target,
    [string]$AppPool = "",
    [string]$HealthUrl = "",
    [string]$Python = "",
    [int]$HealthTimeoutSec = 60,
    [switch]$SkipPip,
    [switch]$UseDefaultCredentials
)

$ErrorActionPreference = "Stop"

function Fail($msg) { Write-Host "FAILED: $msg" -ForegroundColor Red; exit 2 }
function Step($msg) { Write-Host "`n== $msg" -ForegroundColor Cyan }

# --- pre-flight -------------------------------------------------------------
$Source = (Resolve-Path $Source).Path
foreach ($f in @("app.py", "portal_api.py", "auth.py", "envcheck.py", "requirements.txt", "Web.config")) {
    if (-not (Test-Path (Join-Path $Source $f))) { Fail "$f is missing from $Source (run the script from this folder)" }
}
if (-not (Test-Path $Target)) { New-Item -ItemType Directory -Path $Target -Force | Out-Null }
$Target = (Resolve-Path $Target).Path
if ($Source -eq $Target) { Fail "Source and Target are the same folder ($Target)" }
if (-not $Python) { $Python = Join-Path $Target "venv\Scripts\python.exe" }
if (-not (Test-Path $Python)) {
    Write-Host "WARNING: $Python not found. Create the venv in $Target" -ForegroundColor Yellow
    Write-Host "         (python -m venv venv; .\venv\Scripts\pip install -r requirements.txt)" -ForegroundColor Yellow
    Write-Host "         or pass -Python <server python>. Web.config must name the same file." -ForegroundColor Yellow
}

$appcmd = Join-Path $env:windir "system32\inetsrv\appcmd.exe"

# --- stop the worker so no file is locked and no module is cached ----------
if ($AppPool) {
    Step "Stopping application pool '$AppPool'"
    if (Test-Path $appcmd) { & $appcmd stop apppool /apppool.name:"$AppPool" | Out-Null }
    else { Write-Host "WARNING: appcmd.exe not found - stop the pool in IIS Manager, then press Enter" -ForegroundColor Yellow; Read-Host | Out-Null }
    Start-Sleep -Seconds 2
}

# --- copy (mirror, but never touch .env / venv / logs / caches) ------------
Step "Mirroring $Source -> $Target"
$rc = Start-Process robocopy -Wait -PassThru -NoNewWindow -ArgumentList @(
    "`"$Source`"", "`"$Target`"", "/MIR", "/NFL", "/NDL", "/NJH", "/NJS",
    "/XD", "venv", "__pycache__", ".git", "logs",
    "/XF", "*.pyc", ".env"
)
if ($rc.ExitCode -ge 8) { Fail "robocopy returned $($rc.ExitCode)" }   # 0-7 = success

if (-not (Test-Path (Join-Path $Target "logs"))) { New-Item -ItemType Directory -Path (Join-Path $Target "logs") -Force | Out-Null }
if (-not (Test-Path (Join-Path $Target ".env"))) {
    Write-Host "WARNING: $Target\.env does not exist - /api/health will report gap_password_configured=false" -ForegroundColor Yellow
    Write-Host "         and every dropdown/to-job call fails. Create it from .env.example." -ForegroundColor Yellow
}

# --- packages into the interpreter IIS will actually start -----------------
if (-not $SkipPip -and (Test-Path $Python)) {
    Step "Installing requirements into $Python"
    & $Python -m pip install --disable-pip-version-check -q -r (Join-Path $Target "requirements.txt")
    if ($LASTEXITCODE -ne 0) { Fail "pip install failed - the worker would answer 500.30 (ModuleNotFoundError)" }
}

# --- what the file on disk says -------------------------------------------
Step "Hashing the deployed source"
Push-Location $Target
$expected = ((& $Python -c "import portal_api;print(portal_api.SOURCE_SHA)" 2>&1) -join "`n").Trim()
$importExit = $LASTEXITCODE
Pop-Location
if ($importExit -ne 0 -or $expected -notmatch '^[0-9a-f]{12}$') {
    Fail "could not import portal_api with $Python :`n$expected"
}
Write-Host "expected sha : $expected"

if ($AppPool) {
    Step "Starting application pool '$AppPool'"
    if (Test-Path $appcmd) { & $appcmd start apppool /apppool.name:"$AppPool" | Out-Null }
}

# --- prove the worker is running it ---------------------------------------
if ($HealthUrl) {
    Step "Waiting for $HealthUrl to report sha $expected"
    $deadline = (Get-Date).AddSeconds($HealthTimeoutSec)
    $iwr = @{ UseBasicParsing = $true; TimeoutSec = 15 }
    if ($UseDefaultCredentials) { $iwr["UseDefaultCredentials"] = $true }
    $last = $null
    while ((Get-Date) -lt $deadline) {
        try {
            $last = (Invoke-WebRequest -Uri $HealthUrl @iwr).Content | ConvertFrom-Json
            if ($last.portal_api_sha -eq $expected) {
                Write-Host "OK: the running worker reports portal_api_sha=$($last.portal_api_sha)" -ForegroundColor Green
                Write-Host ("username={0} password_configured={1} base_path={2} payload_variant={3}" -f `
                    $last.gap_username, $last.gap_password_configured, $last.base_path, $last.payload_variant)
                if ($last.runtime) {
                    Write-Host ("python={0} venv={1} site_packages={2}" -f `
                        $last.runtime.python, $last.runtime.in_venv, $last.runtime.site_packages)
                }
                exit 0
            }
            Write-Host "worker still reports '$($last.portal_api_sha)' - waiting..."
        }
        catch {
            Write-Host "health not answering yet: $($_.Exception.Message)"
        }
        Start-Sleep -Seconds 3
    }
    Write-Host "FAILED: the worker never reported $expected" -ForegroundColor Red
    if ($last) {
        Write-Host ("last answer: portal_api_sha={0} app_dir={1}" -f $last.portal_api_sha, $last.runtime.app_dir)
    }
    Write-Host "Another copy is being started. Check Web.config processPath, the IIS application's" -ForegroundColor Yellow
    Write-Host "physical path, a second gap folder under the site, and delete __pycache__ next to app.py." -ForegroundColor Yellow
    exit 1
}

Write-Host "`nCopied. Open /api/health and compare portal_api_sha with $expected" -ForegroundColor Green
exit 0
