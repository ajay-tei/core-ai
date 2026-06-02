# backup-prod.ps1 — Backs up the prod deploy data folder via SSH before a new deployment.
# Called by the backup:prod CI job.
#
# Parameters:
#   -SshKeyFile   Path to the SSH private key on the runner
#   -SshTarget    user@host for the prod server
#   -DeployPath   Deploy path on the prod server  (e.g. C:\ai-app\tei-ai)
#   -CommitSha    Short commit SHA used to name the backup folder

param(
    [Parameter(Mandatory)][string] $SshKeyFile,
    [Parameter(Mandatory)][string] $SshTarget,
    [Parameter(Mandatory)][string] $DeployPath,
    [Parameter(Mandatory)][string] $CommitSha
)

Write-Host "=== Prod backup via SSH ==="
Write-Host "Target:     $SshTarget"
Write-Host "DeployPath: $DeployPath"

$timestamp  = Get-Date -Format 'yyyyMMdd-HHmmss'
$backupName = "data-$CommitSha-$timestamp"

$remoteScript = @"
`$dataDir   = Join-Path '$DeployPath' 'data'
`$backupDir = Join-Path '$DeployPath' 'backups'
`$dest      = Join-Path `$backupDir '$backupName'

if (-not (Test-Path `$dataDir)) {
    Write-Host 'No data directory found — skipping backup (first deploy).'
    exit 0
}
if (-not (Test-Path `$backupDir)) {
    `$null = New-Item -ItemType Directory -Path `$backupDir -Force
}
Copy-Item -Path `$dataDir -Destination `$dest -Recurse -Force
`$sizeMB = [math]::Round((Get-ChildItem `$dest -Recurse | Measure-Object Length -Sum).Sum / 1MB, 2)
Write-Host "Backup complete: `$dest (`$sizeMB MB)"

# Retention: keep the 5 most-recent backups
`$existing = Get-ChildItem `$backupDir -Directory | Sort-Object CreationTime -Descending
if (`$existing.Count -gt 5) {
    `$toRemove = `$existing | Select-Object -Skip 5
    foreach (`$dir in `$toRemove) {
        Write-Host "Pruning old backup: `$(`$dir.FullName)"
        Remove-Item `$dir.FullName -Recurse -Force
    }
}
"@

$encoded = [Convert]::ToBase64String([System.Text.Encoding]::Unicode.GetBytes($remoteScript))
ssh -i $SshKeyFile -o BatchMode=yes -o StrictHostKeyChecking=yes $SshTarget `
    "powershell -NoProfile -EncodedCommand $encoded"

if ($LASTEXITCODE -ne 0) {
    Write-Error "Prod backup failed (SSH exit $LASTEXITCODE)"
    exit $LASTEXITCODE
}
Write-Host "=== Prod backup complete ==="
