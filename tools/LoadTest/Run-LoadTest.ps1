<#
.SYNOPSIS
    Convenience wrapper for the Diva Agent API load tester (tools/LoadTest).

.DESCRIPTION
    Builds the dotnet run arguments for the scenario you choose and streams the tool's own
    in-progress status lines and end-of-run summary straight to this console. Never pass the raw
    API key as a literal to this script — set it as an environment variable first
    ($env:DIVA_LOAD_TEST_KEY = "...") and pass that variable name via -ApiKeyEnvVar (default already
    matches it), so the key never has to appear in your shell history or in this script.

.EXAMPLE
    $env:DIVA_LOAD_TEST_KEY = "tei_..."
    ./Run-LoadTest.ps1 -AgentId 568cfc5b-18eb-4c5d-ac7c-17fa87422dad -Scenario burst -Users 5

.EXAMPLE
    ./Run-LoadTest.ps1 -AgentId 568cfc5b-18eb-4c5d-ac7c-17fa87422dad -Scenario ramp `
        -RampMaxUsers 100 -RampStepUsers 10 -RampStepSeconds 20 -Duration 300 -OutputCsv ramp.csv

.EXAMPLE
    ./Run-LoadTest.ps1 -AgentId 568cfc5b-18eb-4c5d-ac7c-17fa87422dad -Scenario soak `
        -Mode stream -Duration 120 -Query "Check availability at all 5 courses and summarize."
#>
param(
    [string]$BaseUrl = "http://localhost:6032",
    [string]$ApiKeyEnvVar = "DIVA_LOAD_TEST_KEY",

    [Parameter(Mandatory = $true)]
    [string]$AgentId,

    [ValidateSet("burst", "soak", "ramp")]
    [string]$Scenario = "burst",

    [ValidateSet("invoke", "stream")]
    [string]$Mode = "invoke",

    [int]$Users = 3,
    [int]$RequestsPerUser = 5,
    [int]$Duration = 60,

    [int]$RampMaxUsers = 200,
    [int]$RampStepUsers = 10,
    [int]$RampStepSeconds = 15,

    [string]$Query = "Give me a one-sentence status update.",
    [string]$OutputCsv,
    [switch]$SharedSession,
    [switch]$NoWarmup,
    [int]$RequestTimeout = 180
)

$ErrorActionPreference = "Stop"
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path

$apiKey = [System.Environment]::GetEnvironmentVariable($ApiKeyEnvVar)
if (-not $apiKey)
{
    Write-Host "No value found in `$env:$ApiKeyEnvVar - proceeding without an API key (fine only if the target has OAuth disabled)." -ForegroundColor Yellow
}

$dotnetArgs = @(
    "run", "--project", $scriptDir, "-c", "Release", "--",
    "--base-url", $BaseUrl,
    "--agent-id", $AgentId,
    "--scenario", $Scenario,
    "--mode", $Mode,
    "--query", $Query,
    "--request-timeout", $RequestTimeout
)
if ($apiKey) { $dotnetArgs += @("--api-key", $apiKey) }

switch ($Scenario)
{
    "burst" { $dotnetArgs += @("--users", $Users, "--requests-per-user", $RequestsPerUser) }
    "soak"  { $dotnetArgs += @("--users", $Users, "--duration", $Duration) }
    "ramp"  { $dotnetArgs += @("--ramp-max-users", $RampMaxUsers, "--ramp-step-users", $RampStepUsers, "--ramp-step-seconds", $RampStepSeconds, "--duration", $Duration) }
}
if ($SharedSession) { $dotnetArgs += "--shared-session" }
if ($NoWarmup) { $dotnetArgs += "--no-warmup" }
if ($OutputCsv) { $dotnetArgs += @("--output", $OutputCsv) }

$loggedArgs = $dotnetArgs -replace [regex]::Escape($apiKey), "***"
Write-Host "Running: dotnet $($loggedArgs -join ' ')" -ForegroundColor Cyan
Write-Host ""

& dotnet @dotnetArgs
exit $LASTEXITCODE
