<#
.SYNOPSIS
    Find the copy of the GAP tool that is actually answering /gap, and (with
    -Quarantine -Force) move it out of IIS so the deployed copy is the only one.

.DESCRIPTION
    The message "P2608760: API status=false | {...}" is printed by NO file in this
    repository - not by 19f586c, not by 8a22893, not by the current tree. Whoever
    answers /gap is running a different build, so editing this folder cannot
    change what the browser shows. This script locates that build instead of
    guessing:

      * reads every physical path IIS serves (appcmd list vdir)
      * scans them (plus -SearchRoot) for files containing the literal "API status"
      * prints the sha1 (first 12 hex chars, the same value /api/health reports as
        portal_api_sha) and the last write time of every portal_api.py it finds
      * prints the python processes running right now with their command line, so
        an out-of-IIS copy (service, scheduled task, console, second site) cannot
        hide either

    With -Quarantine it stops the app pool of every IIS application whose folder
    contains "API status", removes the IIS application and renames the folder to
    <name>.stale-<timestamp>. Nothing is deleted: the rename is undoable by hand,
    and the URL falls back to the SO-GEN MVC app (404) instead of serving code
    nobody can find in git. Run install-gap-iis.ps1 + deploy-gap.ps1 afterwards.

.EXAMPLE
    .\find-gap-copies.ps1
    # read-only report, exit 1 when a foreign copy exists

.EXAMPLE
    .\find-gap-copies.ps1 -Quarantine -Force
    # stop + detach the apps that run foreign code, then rename their folders
#>
[CmdletBinding()]
param(
    [string[]]$SearchRoot = @("C:\inetpub"),
    [string]$HealthUrl = "http://localhost/gap/api/health",
    [switch]$Quarantine,
    [switch]$Force
)

$ErrorActionPreference = "Stop"
function Fail($m) { Write-Host "FAILED: $m" -ForegroundColor Red; exit 2 }
function Step($m) { Write-Host "`n== $m" -ForegroundColor Cyan }

$appcmd = Join-Path $env:windir "system32\inetsrv\appcmd.exe"
if (-not (Test-Path $appcmd)) { Fail "appcmd.exe not found - run this on the web server" }

function Get-Sha12($path) {
    try { return (Get-FileHash -LiteralPath $path -Algorithm SHA1).Hash.ToLower().Substring(0, 12) }
    catch { return "unreadable" }
}

function Test-ContainsText($path, $needle) {
    try {
        $bytes = [System.IO.File]::ReadAllBytes($path)
        return [System.Text.Encoding]::ASCII.GetString($bytes).Contains($needle)
    }
    catch { return $false }
}

# --- what IIS serves ---------------------------------------------------------
Step "IIS applications and their physical paths"
$vdirs = @()
foreach ($line in (& $appcmd list vdir 2>$null)) {
    if ($line -match 'VDIR\s+"([^"]+)"\s+\(physicalPath:(.+?)\)\s*$') {
        $vdirs += [pscustomobject]@{ Name = $matches[1]; Path = $matches[2].Trim() }
    }
}
foreach ($v in $vdirs) { Write-Host ("  {0,-40} {1}" -f $v.Name, $v.Path) }
if (-not $vdirs) { Write-Host "  (none)" -ForegroundColor Yellow }

$roots = @()
foreach ($r in $SearchRoot) { if (Test-Path $r) { $roots += (Resolve-Path $r).Path } }
foreach ($v in $vdirs) { if (Test-Path $v.Path) { $roots += $v.Path } }
$roots = $roots | Sort-Object -Unique
if (-not $roots) { Fail "nothing to search (no IIS vdirs, no -SearchRoot exists)" }

# --- every portal_api.py on those paths, plus anything printing "API status" --
$FOREIGN = "API status"
$files = @()
foreach ($root in $roots) {
    $files += Get-ChildItem -LiteralPath $root -Recurse -File -ErrorAction SilentlyContinue |
              Where-Object { $_.Extension -in ".py", ".pyc" }
}
$files = $files | Sort-Object FullName -Unique

Step "portal_api.py copies ($($files.Count) .py/.pyc files scanned)"
$sources = $files | Where-Object { $_.Name -eq "portal_api.py" }
if ($sources) {
    foreach ($f in $sources) {
        Write-Host ("  {0}  {1}  {2}" -f (Get-Sha12 $f.FullName), $f.LastWriteTime.ToString("yyyy-MM-dd HH:mm"), $f.FullName)
    }
    Write-Host "`n  /api/health must report one of these sha1 values as portal_api_sha." -ForegroundColor Gray
}
else { Write-Host "  none found - the worker is running from outside these roots" -ForegroundColor Yellow }

Step "Files that can print '$FOREIGN' (= not this repository)"
$foreign = @()
foreach ($f in $files) {
    if (Test-ContainsText $f.FullName $FOREIGN) { $foreign += $f }
}
if ($foreign) {
    foreach ($f in $foreign) {
        Write-Host ("  {0}  {1}" -f $f.LastWriteTime.ToString("yyyy-MM-dd HH:mm"), $f.FullName) -ForegroundColor Red
        if ($f.FullName.StartsWith($PSScriptRoot, [StringComparison]::OrdinalIgnoreCase)) {
            Write-Host "           ^ inside the folder this script ships from: delete that file" -ForegroundColor Yellow
        }
    }
}
else {
    Write-Host "  none here - check the python processes below and other drives" -ForegroundColor Yellow
}

# --- what the URL actually answers ------------------------------------------
Step "What $HealthUrl answers"
try {
    $health = Invoke-RestMethod -Uri $HealthUrl -UseDefaultCredentials -TimeoutSec 20 -ErrorAction Stop
    Write-Host ($health | ConvertTo-Json -Depth 6 -Compress)
    Write-Host ("  portal_api_sha = {0}  (compare with the sha1 column above)" -f $health.portal_api_sha) -ForegroundColor Cyan
}
catch {
    Write-Host ("  {0}" -f $_.Exception.Message) -ForegroundColor Yellow
    Write-Host "  (a foreign build may not even have /api/health - that is itself the answer)" -ForegroundColor Gray
}

# --- anything running outside IIS -------------------------------------------
Step "python processes right now"
$procs = Get-CimInstance Win32_Process -Filter "Name LIKE '%python%'" -ErrorAction SilentlyContinue
if ($procs) {
    foreach ($p in $procs) {
        Write-Host ("  pid {0}: {1}" -f $p.ProcessId, $p.CommandLine)
        if ($p.ExecutablePath) { Write-Host ("         exe {0}" -f $p.ExecutablePath) -ForegroundColor Gray }
    }
}
else { Write-Host "  none (the worker is started by ANCM on demand: that is normal)" -ForegroundColor Gray }

# --- quarantine --------------------------------------------------------------
if ($Quarantine -and $foreign) {
    $targets = @()
    $self = $PSScriptRoot.TrimEnd("\")
    foreach ($f in $foreign) {
        $owner = $vdirs | Where-Object { $f.FullName.StartsWith($_.Path, [StringComparison]::OrdinalIgnoreCase) } |
                 Sort-Object { $_.Path.Length } -Descending | Select-Object -First 1
        if (-not $owner) { continue }
        # Never retire the folder this script runs from (or one containing it): a stray
        # nested copy must be deleted file by file, not by renaming a good application.
        $ownerPath = $owner.Path.TrimEnd("\")
        if ($ownerPath -eq $self -or $self.StartsWith($ownerPath, [StringComparison]::OrdinalIgnoreCase)) {
            Write-Host ("  KEEPING '{0}': the stale file {1} is inside this application - delete that file only" -f $owner.Name, $f.FullName) -ForegroundColor Yellow
            continue
        }
        $targets += [pscustomobject]@{ App = $owner.Name; Path = $owner.Path; File = $f.FullName }
    }
    $targets = $targets | Sort-Object Path -Unique

    Step "Plan"
    if (-not $targets) { Write-Host "  nothing can be retired automatically - see the KEEPING line(s) above" -ForegroundColor Yellow }
    foreach ($t in $targets) { Write-Host ("  stop pool of '{0}', remove the application, rename {1}" -f $t.App, $t.Path) }

    if (-not $targets) {
        Write-Host "`nNothing was changed: handle the file(s) listed above by hand, then re-run this report." -ForegroundColor Yellow
        exit 1
    }
    if (-not $Force) {
        Write-Host "`nNothing was changed. Re-run with -Quarantine -Force to apply the plan above." -ForegroundColor Yellow
        exit 1
    }

    foreach ($t in $targets) {
        Step "Quarantining $($t.App)"
        $appLine = (& $appcmd list app "$($t.App)" 2>$null)
        if ($appLine -match 'applicationPool:([^)]+)') {
            $pool = $matches[1].Trim()
            & $appcmd stop apppool /apppool.name:"$pool" | Out-Null
            Write-Host "  stopped pool '$pool'"
        }
        & $appcmd delete app "$($t.App)" | Out-Null
        Write-Host "  removed IIS application $($t.App)"

        $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
        $leaf = (Split-Path $t.Path -Leaf) + ".stale-$stamp"
        $moved = $false
        for ($i = 1; $i -le 5 -and -not $moved; $i++) {
            try {
                Rename-Item -LiteralPath $t.Path -NewName $leaf -ErrorAction Stop
                $moved = $true
            }
            catch {
                if ($i -eq 5) { Write-Host "  could not rename $($t.Path): $($_.Exception.Message)" -ForegroundColor Red }
                else { Start-Sleep -Seconds 2 }   # python.exe may still hold a __pycache__ file
            }
        }
        if ($moved) { Write-Host "  renamed $($t.Path) -> $leaf" -ForegroundColor Yellow }
    }

    Write-Host "`nNow: .\install-gap-iis.ps1 -Site ""SO-GEN"" -Alias ""gap"" then .\deploy-gap.ps1 ..." -ForegroundColor Green
    exit 0
}

if ($foreign) {
    Write-Host "`nRESULT: a foreign copy exists (red paths). /gap is NOT running this repository." -ForegroundColor Red
    Write-Host "Run with -Quarantine -Force to retire it, then install-gap-iis.ps1 + deploy-gap.ps1." -ForegroundColor Red
    exit 1
}

Write-Host "`nRESULT: nothing here can print 'API status'. If the page still shows it, search every drive:" -ForegroundColor Green
Write-Host '  Get-PSDrive -PSProvider FileSystem | ForEach-Object { Get-ChildItem "$($_.Root)" -Recurse -File -Include *.py,*.pyc -ErrorAction SilentlyContinue } | Select-String -Pattern "API status" -SimpleMatch -List' -ForegroundColor Gray
exit 0
