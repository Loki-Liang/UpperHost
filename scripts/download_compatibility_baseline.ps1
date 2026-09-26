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

# ApiCompat needs an artifact produced from the exact base SHA by the authoritative
# build/test/pack job. Requiring the entire workflow run to be successful creates a
# repair deadlock when an unrelated job fails on main: a hotfix PR cannot compare
# against its exact base even though build-test-scaffold produced valid packages.
$raw = gh run list --repo $Repository --workflow CI --commit $BaseSha --event push --limit 20 --json databaseId,headSha,status,conclusion,createdAt
if ($LASTEXITCODE -ne 0) {
    throw "Unable to query CI runs for exact base SHA $BaseSha."
}

$candidates = @($raw | ConvertFrom-Json) |
    Where-Object { $_.headSha -eq $BaseSha } |
    Sort-Object createdAt -Descending

$run = $null
foreach ($candidate in $candidates) {
    $viewRaw = gh run view $candidate.databaseId --repo $Repository --json jobs
    if ($LASTEXITCODE -ne 0) {
        continue
    }

    $view = $viewRaw | ConvertFrom-Json
    $producer = @($view.jobs) |
        Where-Object { $_.name -eq "build-test-scaffold" -and $_.conclusion -eq "success" } |
        Select-Object -First 1

    if ($null -eq $producer) {
        continue
    }

    # Download into an isolated probe directory first so a candidate without the
    # expected artifact cannot contaminate the final baseline directory.
    $probeDirectory = Join-Path ([System.IO.Path]::GetTempPath()) ("opendevicestudio-baseline-" + [Guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Force $probeDirectory | Out-Null

    try {
        gh run download $candidate.databaseId --repo $Repository --name opendevicestudio-packages --dir $probeDirectory 2>$null
        if ($LASTEXITCODE -ne 0) {
            continue
        }

        $packages = @(Get-ChildItem -Path $probeDirectory -Filter *.nupkg -File -Recurse)
        if ($packages.Count -eq 0) {
            continue
        }

        foreach ($package in $packages) {
            Copy-Item -LiteralPath $package.FullName -Destination $OutputDirectory -Force
        }

        $run = $candidate
        break
    }
    finally {
        Remove-Item -LiteralPath $probeDirectory -Recurse -Force -ErrorAction SilentlyContinue
    }
}

if ($null -eq $run) {
    throw "No exact-base CI run with a successful build-test-scaffold package artifact exists for SHA $BaseSha. Refusing to use a stale baseline."
}

$packages = @(Get-ChildItem -Path $OutputDirectory -Filter *.nupkg -File -Recurse)
if ($packages.Count -eq 0) {
    throw "Exact-base run $($run.databaseId) did not provide any NuGet packages."
}

Write-Host "Downloaded $($packages.Count) exact-base packages from build-test-scaffold in run $($run.databaseId) for $BaseSha."
