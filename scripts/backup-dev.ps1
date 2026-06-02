# backup-dev.ps1 — Backs up the dev deploy data folder before a new deployment.
# Called by the backup:dev CI job.
#
# Parameters:
#   -DeployPath   Path to the dev deploy directory  (e.g. C:\ai-app\tei-ai-dev)
#   -CommitSha    Short commit SHA used to name the backup folder

param(
    [Parameter(Mandatory)][string] $DeployPath,
    [Parameter(Mandatory)][string] $CommitSha
)

$dataDir   = Join-Path $DeployPath "data"
$backupDir = Join-Path $DeployPath "backups"
$dest      = Join-Path $backupDir  "data-$CommitSha-$(Get-Date -Format 'yyyyMMdd-HHmmss')"

Write-Host "=== Dev backup ==="
Write-Host "Source : $dataDir"
Write-Host "Target : $dest"

if (-not (Test-Path $dataDir)) {
    Write-Host "No data directory found at '$dataDir' — skipping backup (first deploy)."
    exit 0
}

if (-not (Test-Path $backupDir)) {
    $null = New-Item -ItemType Directory -Path $backupDir -Force
}

Copy-Item -Path $dataDir -Destination $dest -Recurse -Force
$sizeMB = [math]::Round((Get-ChildItem $dest -Recurse | Measure-Object Length -Sum).Sum / 1MB, 2)
Write-Host "Backup complete: $dest ($sizeMB MB)"

# ── Retention: keep only the 5 most-recent backups ────────────────────────────
$existing = Get-ChildItem $backupDir -Directory | Sort-Object CreationTime -Descending
if ($existing.Count -gt 5) {
    $toRemove = $existing | Select-Object -Skip 5
    foreach ($dir in $toRemove) {
        Write-Host "Pruning old backup: $($dir.FullName)"
        Remove-Item $dir.FullName -Recurse -Force
    }
}
