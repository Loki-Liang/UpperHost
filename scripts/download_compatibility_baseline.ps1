param(
    [Parameter(Mandatory = $true)]
    [string]$Repository,

    [Parameter(Mandatory = $true)]
    [string]$BaseSha,

    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory
)

$ErrorActionPreference = "Stop"

if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
    throw "GitHub CLI (gh) is required to download the exact-base compatibility artifact."
}

New-Item -ItemType Directory -Force $OutputDirectory | Out-Null

$raw = gh run list --repo $Repository --workflow CI --commit $BaseSha --event push --status success --limit 20 --json databaseId,headSha,status,conclusion,createdAt
if ($LASTEXITCODE -ne 0) {
    throw "Unable to query successful CI runs for exact base SHA $BaseSha."
}

$runs = @($raw | ConvertFrom-Json)
$run = $runs |
    Where-Object { $_.headSha -eq $BaseSha -and $_.conclusion -eq "success" } |
    Sort-Object createdAt -Descending |
    Select-Object -First 1

if ($null -eq $run) {
    throw "No successful main CI run with package artifacts exists for exact base SHA $BaseSha. Refusing to use a stale baseline."
}

gh run download $run.databaseId --repo $Repository --name upperhost-packages --dir $OutputDirectory
if ($LASTEXITCODE -ne 0) {
    throw "Unable to download upperhost-packages from exact-base run $($run.databaseId)."
}

$packages = @(Get-ChildItem -Path $OutputDirectory -Filter *.nupkg -File -Recurse)
if ($packages.Count -eq 0) {
    throw "Exact-base run $($run.databaseId) did not provide any NuGet packages."
}

Write-Host "Downloaded $($packages.Count) exact-base packages from run $($run.databaseId) for $BaseSha."
