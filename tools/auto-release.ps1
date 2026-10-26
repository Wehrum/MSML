<#
.SYNOPSIS
    Publishes a new MSML release whenever main has changes since the last release. Runs on this PC (releases need
    files generated from your copy of the game, so they can't be built on GitHub).

.DESCRIPTION
    Checks origin/main for commits since the newest v* tag that touch the loader, installer or tools (src/, installer/,
    shared/, tools/, compatibility.json, Directory.Build.props). If there are any, it fast-forwards main, picks the
    next version, and runs tools\publish-release.ps1, which builds, tags, pushes and uploads the GitHub release.

    Version bump from the new commits' messages: "[major]" -> x+1.0.0, "[minor]" -> x.y+1.0, otherwise x.y.z+1.

    Triggers (both stay on this PC; nothing is committed):
      -Install    adds a git post-merge hook: after you pull or merge into main, a release builds in the background.
      -Schedule   also registers a Windows scheduled task that checks every 15 minutes, so PRs merged on GitHub get
                  released even if you don't pull.
      -Remove     removes the hook and the scheduled task.
    Without a switch it runs one check now. Log: .tools\auto-release.log

.PARAMETER DryRun
    Report what would be released without building or publishing.
#>
[CmdletBinding()]
param(
    [switch]$Install,
    [switch]$Schedule,
    [switch]$Remove,
    [switch]$DryRun
)

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path -Parent $PSScriptRoot
$Branch = "main"
$TaskName = "MSML auto-release"
$LogFile = Join-Path $RepoRoot ".tools\auto-release.log"
$LockFile = Join-Path $RepoRoot ".tools\auto-release.lock"
$Watched = @("src", "installer", "shared", "tools", "compatibility.json", "Directory.Build.props")
$HookPath = Join-Path $RepoRoot ".git\hooks\post-merge"
$HookMarker = "# MSML auto-release"

function Log([string]$msg, [string]$color = "Gray") {
    $line = "[{0:yyyy-MM-dd HH:mm:ss}] {1}" -f (Get-Date), $msg
    Write-Host $line -ForegroundColor $color
    New-Item -ItemType Directory -Force (Split-Path $LogFile) | Out-Null
    Add-Content $LogFile $line
}

# Runs git and returns its output; git writes progress to stderr, which mustn't abort under Windows PowerShell.
function Invoke-Git([string[]]$a) {
    $out = & { $ErrorActionPreference = "Continue"; & git.exe -C $RepoRoot @a 2>&1 }
    if ($LASTEXITCODE -ne 0) { throw "git $($a -join ' ') failed: $($out -join ' ')" }
    return @($out | ForEach-Object { "$_" })
}

# ---- install / remove triggers ----------------------------------------------------------------------
if ($Install -or $Schedule) {
    $hook = @"
#!/bin/sh
${HookMarker}: after a pull/merge into $Branch, publish a release in the background if anything changed.
[ "`$(git rev-parse --abbrev-ref HEAD)" = "$Branch" ] || exit 0
powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "tools/auto-release.ps1" >/dev/null 2>&1 &
exit 0
"@
    [IO.File]::WriteAllText($HookPath, $hook.Replace("`r`n", "`n"))
    Log "Installed git post-merge hook ($HookPath)." Green
    if ($Schedule) {
        $action = New-ScheduledTaskAction -Execute "powershell.exe" -WorkingDirectory $RepoRoot `
            -Argument "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$PSCommandPath`""
        $trigger = New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(1) -RepetitionInterval (New-TimeSpan -Minutes 15)
        $settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -DontStopIfGoingOnBatteries -AllowStartIfOnBatteries -MultipleInstances IgnoreNew
        Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Settings $settings -Force | Out-Null
        Log "Registered scheduled task '$TaskName' (every 15 minutes)." Green
    }
    exit 0
}
if ($Remove) {
    if ((Test-Path $HookPath) -and ((Get-Content $HookPath -Raw) -match [regex]::Escape($HookMarker))) { Remove-Item $HookPath; Log "Removed git hook." }
    if (Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue) { Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false; Log "Removed scheduled task." }
    exit 0
}

# ---- one check ---------------------------------------------------------------------------------------
if (Test-Path $LockFile) {
    $age = (Get-Date) - (Get-Item $LockFile).LastWriteTime
    if ($age.TotalMinutes -lt 60) { Write-Host "Another auto-release is running (lock $LockFile)."; exit 0 }
    Remove-Item $LockFile -Force
}
Set-Content $LockFile $PID
try {
    Invoke-Git @("fetch", "-q", "origin", $Branch, "--tags") | Out-Null
    $tags = Invoke-Git @("tag", "--list", "v*") | Where-Object { $_ -match '^v\d+\.\d+\.\d+$' } |
        Sort-Object { [version]$_.TrimStart('v') }
    $last = $tags | Select-Object -Last 1
    $range = if ($last) { "$last..origin/$Branch" } else { "origin/$Branch" }
    $commits = Invoke-Git (@("log", "--format=%H %s", $range, "--") + $Watched) | Where-Object { $_ }
    if (-not $commits) { Log "No changes to release since $(if ($last) { $last } else { 'the start' })."; exit 0 }

    $v = if ($last) { [version]$last.TrimStart('v') } else { [version]"0.0.0" }
    $messages = Invoke-Git @("log", "--format=%B", $range)
    $next = if ($messages -match '\[major\]') { "$($v.Major + 1).0.0" }
            elseif ($messages -match '\[minor\]') { "$($v.Major).$($v.Minor + 1).0" }
            else { "$($v.Major).$($v.Minor).$($v.Build + 1)" }
    Log "$(@($commits).Count) change(s) since $last -> releasing v${next}:" Cyan
    $commits | Select-Object -First 15 | ForEach-Object { Log "  $($_.Substring(0, [Math]::Min(100, $_.Length)))" }
    if ($DryRun) { Log "Dry run: nothing published."; exit 0 }

    # Bring local main up to date (the release is built from it).
    if ((Invoke-Git @("status", "--porcelain")) | Where-Object { $_ }) { throw "Working tree has uncommitted changes; commit or stash them, then run tools\auto-release.ps1." }
    $current = (Invoke-Git @("rev-parse", "--abbrev-ref", "HEAD"))[0]
    if ($current -ne $Branch) { throw "Checked out branch is '$current', not '$Branch'. Switch to $Branch, then run tools\auto-release.ps1." }
    Invoke-Git @("merge", "-q", "--ff-only", "origin/$Branch") | Out-Null

    $out = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot "publish-release.ps1") -Version $next `
        -Notes "Changes since ${last}:`n$(($commits | ForEach-Object { '- ' + $_.Substring(41) }) -join "`n")" 2>&1
    $out | ForEach-Object { Add-Content $LogFile "    $_" }
    if ($LASTEXITCODE -ne 0) { throw "publish-release.ps1 failed (exit $LASTEXITCODE); see $LogFile." }
    Log "Published v${next}: https://github.com/Wehrum/MSML/releases/tag/v$next" Green
} catch {
    Log "Auto-release failed: $($_.Exception.Message)" Red
    exit 1
} finally {
    Remove-Item $LockFile -Force -ErrorAction SilentlyContinue
}
