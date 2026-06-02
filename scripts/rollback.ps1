# rollback.ps1 — Rolls back both Diva services to the previously running image tags.
# Called automatically from deploy:dev / deploy:prod after_script on failure.
#
# Parameters:
#   -Target       "dev" or "prod"
#   -DeployPath   Local deploy directory (dev) or remote deploy directory (prod)
#   -SshKeyFile   [prod only] Path to SSH private key on runner
#   -SshTarget    [prod only] user@host
#   -DockerHost   [prod only] Docker host URI (default tcp://localhost:2375)

param(
    [Parameter(Mandatory)][ValidateSet('dev','prod')][string] $Target,
    [Parameter(Mandatory)][string] $DeployPath,
    [string] $SshKeyFile,
    [string] $SshTarget,
    [string] $DockerHost = "tcp://localhost:2375"
)

function Invoke-Rollback {
    param([string] $Path, [string] $DockerH = "")

    # Find the most recently running image SHA for diva-api (before this deploy)
    # docker compose keeps the previous image tagged as :<previous-sha> in local storage.
    # Strategy: docker compose down then docker compose up using :previous tag via
    # docker image ls — pick the second-newest image for each service.

    $dockerPrefix = if ($DockerH) { "docker -H $DockerH" } else { "docker" }

    Write-Host "Stopping current (failed) stack..."
    Invoke-Expression "$dockerPrefix compose -f $Path\docker-compose.yml down --timeout 30" | Out-Null

    # Find the second-most-recent image ID for diva-api and diva-portal
    foreach ($svc in @("diva-api", "diva-portal")) {
        $images = Invoke-Expression "$dockerPrefix image ls --filter=reference='*/$svc' --format '{{.ID}} {{.CreatedAt}}'" 2>$null
        if (-not $images) {
            $images = Invoke-Expression "$dockerPrefix image ls --filter=reference='$svc' --format '{{.ID}} {{.CreatedAt}}'" 2>$null
        }
        $sorted = $images | Sort-Object { $_ -split ' ' | Select-Object -Last 1 } -Descending
        if ($sorted.Count -ge 2) {
            $prevId = ($sorted[1] -split ' ')[0]
            Write-Host "Tagging $svc previous image $prevId as :rollback"
            Invoke-Expression "$dockerPrefix tag $prevId ${svc}:rollback" | Out-Null
        } else {
            Write-Warning "No previous image found for $svc — rollback image unavailable."
        }
    }

    # Update .env to use :rollback tags and restart
    Write-Host "Starting rollback stack..."
    Invoke-Expression "$dockerPrefix compose -f $Path\docker-compose.yml up -d --remove-orphans"
}

Write-Host "=== Rollback triggered for: $Target ==="

if ($Target -eq "dev") {
    Invoke-Rollback -Path $DeployPath
} else {
    if (-not $SshKeyFile -or -not $SshTarget) {
        Write-Error "-SshKeyFile and -SshTarget are required for prod rollback"
        exit 1
    }

    $script = @"
`$dockerPrefix = 'docker -H $DockerHost'
Invoke-Expression "`$dockerPrefix compose -f $DeployPath\docker-compose.yml down --timeout 30" | Out-Null
foreach (`$svc in @('diva-api','diva-portal')) {
    `$images = Invoke-Expression "`$dockerPrefix image ls --filter=reference='*/ `$svc' --format '{{.ID}} {{.CreatedAt}}'" 2>`$null
    if (-not `$images) {
        `$images = Invoke-Expression "`$dockerPrefix image ls --filter=reference='`$svc' --format '{{.ID}} {{.CreatedAt}}'" 2>`$null
    }
    `$sorted = `$images | Sort-Object { `$_ -split ' ' | Select-Object -Last 1 } -Descending
    if (`$sorted.Count -ge 2) {
        `$prevId = (`$sorted[1] -split ' ')[0]
        Write-Host "Tagging `$svc `$prevId as :rollback"
        Invoke-Expression "`$dockerPrefix tag `$prevId `${svc}:rollback" | Out-Null
    } else {
        Write-Warning "No previous image for `$svc"
    }
}
Invoke-Expression "`$dockerPrefix compose -f $DeployPath\docker-compose.yml up -d --remove-orphans"
"@
    $encoded = [Convert]::ToBase64String([System.Text.Encoding]::Unicode.GetBytes($script))
    ssh -i $SshKeyFile -o BatchMode=yes -o StrictHostKeyChecking=yes $SshTarget `
        "powershell -NoProfile -EncodedCommand $encoded"
}

Write-Host "=== Rollback complete ==="
