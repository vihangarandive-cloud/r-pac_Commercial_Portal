<#
.SYNOPSIS
    Register GapIntegration as an IIS application under the SO-GEN site so that
    https://so-gen/gap/ runs the Python tool instead of 404.

.DESCRIPTION
    Publishing the folder (RpacCommercial.csproj now includes it) only puts the
    files on disk. IIS still has to serve that folder as an application with its
    own application pool, otherwise the SO-GEN MVC handlers claim /gap/* and the
    page either 404s or the API calls never reach Python.

    Idempotent: run it after every publish. It creates the application and the
    pool when missing and re-points them when they exist.

.EXAMPLE
    .\install-gap-iis.ps1 -Site "SO-GEN" -Alias "gap" -PhysicalPath "C:\inetpub\SO-GEN\GapIntegration"

.EXAMPLE
    # defaults: site SO-GEN, alias gap, folder <site physical path>\GapIntegration
    .\install-gap-iis.ps1
#>
[CmdletBinding()]
param(
    [string]$Site = "SO-GEN",
    [string]$Alias = "gap",
    [string]$PhysicalPath = "",
    [string]$AppPool = "SO-GEN Gap"
)

$ErrorActionPreference = "Stop"
function Fail($m) { Write-Host "FAILED: $m" -ForegroundColor Red; exit 2 }
function Step($m) { Write-Host "`n== $m" -ForegroundColor Cyan }

$appcmd = Join-Path $env:windir "system32\inetsrv\appcmd.exe"
if (-not (Test-Path $appcmd)) { Fail "appcmd.exe not found - run this on the web server" }

$siteRoot = (& $appcmd list site /name:"$Site" /text:physicalPath 2>$null)
if (-not $siteRoot -or -not (Test-Path $siteRoot)) { Fail "IIS site '$Site' does not exist (appcmd list site)" }
Write-Host "site $Site -> $siteRoot"

if (-not $PhysicalPath) { $PhysicalPath = Join-Path $siteRoot "GapIntegration" }
if (-not (Test-Path (Join-Path $PhysicalPath "app.py"))) {
    Fail "no app.py in $PhysicalPath - publish or copy the folder first (deploy-gap.ps1 -Target '$PhysicalPath')"
}
Write-Host "application /$Alias -> $PhysicalPath"

if ($Alias -ne "gap") {
    Write-Host "WARNING: Web.config sets GAP_BASE_PATH=gap, but the alias is '$Alias'." -ForegroundColor Yellow
    Write-Host "         Change GAP_BASE_PATH to '$Alias' or the links in the page will 404." -ForegroundColor Yellow
}

# --- application pool -------------------------------------------------------
Step "Application pool '$AppPool'"
$existing = (& $appcmd list apppool /name:"$AppPool" /text:name 2>$null)
if ($existing -ne $AppPool) {
    & $appcmd add apppool /name:"$AppPool" | Out-Null
    if ($LASTEXITCODE -ne 0) { Fail "could not create app pool '$AppPool'" }
    Write-Host "created"
}
& $appcmd set apppool /apppool.name:"$AppPool" /managedRuntimeVersion:"" | Out-Null   # No Managed Code
& $appcmd set apppool /apppool.name:"$AppPool" /managedPipelineMode:Integrated | Out-Null
& $appcmd set apppool /apppool.name:"$AppPool" /enable32BitAppOnWin64:false | Out-Null
& $appcmd set apppool /apppool.name:"$AppPool" /startMode:AlwaysRunning 2>$null | Out-Null
Write-Host "configured (No Managed Code, Integrated, 64-bit)"

# --- application ------------------------------------------------------------
Step "Application /$Alias"
$appName = "$Site/$Alias"
$app = (& $appcmd list app "$appName" 2>$null)
if (-not $app) {
    & $appcmd add app /site.name:"$Site" /path:"/$Alias" /physicalPath:"$PhysicalPath" | Out-Null
    if ($LASTEXITCODE -ne 0) { Fail "could not add application /$Alias to $Site" }
    Write-Host "created"
}
else {
    & $appcmd set app /app.name:"$appName" /physicalPath:"$PhysicalPath" | Out-Null
    Write-Host "re-pointed"
}
& $appcmd set app /app.name:"$appName" /applicationPool:"$AppPool" | Out-Null

# --- the worker writes its own log, the pool identity must be allowed to ----
Step "Permissions on $PhysicalPath\logs"
$logs = Join-Path $PhysicalPath "logs"
if (-not (Test-Path $logs)) { New-Item -ItemType Directory -Path $logs -Force | Out-Null }
& icacls $logs /grant "IIS AppPool\${AppPool}:(OI)(CI)M" /T | Out-Null
Write-Host "granted Modify to IIS AppPool\$AppPool"

# --- what IIS now answers ---------------------------------------------------
Step "Result"
& $appcmd list app "$appName"
& $appcmd list apppool "$AppPool"

if (-not (Test-Path (Join-Path $PhysicalPath "venv\Scripts\python.exe"))) {
    Write-Host "`nNEXT: Web.config runs .\venv\Scripts\python.exe inside that folder and it does not exist yet." -ForegroundColor Yellow
    Write-Host "      deploy-gap.ps1 -Target '$PhysicalPath' -AppPool '$AppPool' -HealthUrl https://<host>/$Alias/api/health" -ForegroundColor Yellow
    exit 3
}

Write-Host "`nNEXT: https://<host>/$Alias/api/health must report portal_api_sha = the sha printed by" -ForegroundColor Green
Write-Host "      '$PhysicalPath\venv\Scripts\python.exe -c \"import portal_api;print(portal_api.SOURCE_SHA)\"'" -ForegroundColor Green
exit 0
