param(
    [Parameter(Mandatory = $true)]
    [string]$CurrentPackageDirectory,

    [Parameter(Mandatory = $true)]
    [string]$BaselinePackageDirectory,

    [Parameter(Mandatory = $true)]
    [string]$ApiCompatPath
)

$ErrorActionPreference = "Stop"
if (Get-Variable PSNativeCommandUseErrorActionPreference -ErrorAction SilentlyContinue) {
    $PSNativeCommandUseErrorActionPreference = $false
}

function Get-PackageIdentity {
    param([Parameter(Mandatory = $true)][System.IO.FileInfo]$File)

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead($File.FullName)
    try {
        $entry = $archive.Entries | Where-Object { $_.FullName -like "*.nuspec" } | Select-Object -First 1
        if ($null -eq $entry) {
            throw "Package '$($File.FullName)' has no .nuspec metadata."
        }

        $reader = [System.IO.StreamReader]::new($entry.Open())
        try {
            [xml]$xml = $reader.ReadToEnd()
        }
        finally {
            $reader.Dispose()
        }

        $idNode = $xml.SelectSingleNode("//*[local-name()='metadata']/*[local-name()='id']")
        $versionNode = $xml.SelectSingleNode("//*[local-name()='metadata']/*[local-name()='version']")
        if ($null -eq $idNode -or $null -eq $versionNode) {
            throw "Package '$($File.FullName)' is missing id/version metadata."
        }

        [pscustomobject]@{
            Id = $idNode.InnerText
            Version = $versionNode.InnerText
            Path = $File.FullName
        }
    }
    finally {
        $archive.Dispose()
    }
}

function Get-ChangedFiles {
    if ([string]::IsNullOrWhiteSpace($env:GITHUB_BASE_SHA)) {
        throw "GITHUB_BASE_SHA is required so approvals must be changed in the current PR."
    }

    git cat-file -e "$($env:GITHUB_BASE_SHA)^{commit}" 2>$null
    if ($LASTEXITCODE -ne 0) {
        throw "Base SHA $($env:GITHUB_BASE_SHA) is not available in the checkout. CI must use fetch-depth: 0."
    }

    $files = @(git diff --name-only $env:GITHUB_BASE_SHA HEAD)
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to calculate changed files against base SHA $($env:GITHUB_BASE_SHA)."
    }

    return @($files | ForEach-Object { $_.Replace("\", "/") })
}

function Require-Approval {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string[]]$RequiredFields,
        [Parameter(Mandatory = $true)][string[]]$ChangedFiles
    )

    $normalized = $Path.Replace("\", "/")
    if ($normalized -notin $ChangedFiles) {
        throw "Compatibility approval '$normalized' must be added or modified in this PR."
    }
    if (-not (Test-Path $Path)) {
        throw "Compatibility approval '$normalized' does not exist."
    }

    $content = Get-Content -Raw $Path
    foreach ($field in $RequiredFields) {
        if ($content -notmatch "(?im)^\s*$([regex]::Escape($field))\s*:\s*\S+") {
            throw "Compatibility approval '$normalized' must contain a non-empty '${field}:' field."
        }
    }
}

function Invoke-ApiCompat {
    param(
        [Parameter(Mandatory = $true)][string]$Current,
        [Parameter(Mandatory = $true)][string]$Baseline,
        [switch]$Strict
    )

    $arguments = @("package", $Current, "--baseline-package", $Baseline)
    if ($Strict) {
        $arguments += "--enable-strict-mode-for-baseline-validation"
    }

    & $ApiCompatPath @arguments
    return $LASTEXITCODE
}

if (-not (Test-Path $ApiCompatPath)) {
    throw "ApiCompat executable not found: $ApiCompatPath"
}

$current = @{}
Get-ChildItem -Path $CurrentPackageDirectory -Filter *.nupkg -File -Recurse | ForEach-Object {
    $identity = Get-PackageIdentity $_
    if ($current.ContainsKey($identity.Id)) {
        throw "Duplicate current package id '$($identity.Id)'."
    }
    $current[$identity.Id] = $identity
}

$baseline = @{}
Get-ChildItem -Path $BaselinePackageDirectory -Filter *.nupkg -File -Recurse | ForEach-Object {
    $identity = Get-PackageIdentity $_
    if ($baseline.ContainsKey($identity.Id)) {
        throw "Duplicate baseline package id '$($identity.Id)'."
    }
    $baseline[$identity.Id] = $identity
}

if ($current.Count -eq 0 -or $baseline.Count -eq 0) {
    throw "Current and baseline package directories must both contain packages."
}

$changedFiles = Get-ChangedFiles
$failures = New-Object System.Collections.Generic.List[string]

foreach ($id in ($baseline.Keys | Sort-Object)) {
    if ($current.ContainsKey($id)) {
        continue
    }

    try {
        $approval = "eng/compatibility/breaking/$id.md"
        Require-Approval -Path $approval -RequiredFields @("Issue", "Reason", "Migration", "Version") -ChangedFiles $changedFiles
        if ("Directory.Build.props" -notin $changedFiles) {
            throw "Removing package '$id' requires an explicit version change in Directory.Build.props."
        }
        Write-Warning "Approved breaking package removal: $id"
    }
    catch {
        $failures.Add("Package '$id' disappeared from current artifacts: $($_.Exception.Message)")
    }
}

foreach ($id in ($current.Keys | Sort-Object)) {
    $right = $current[$id]

    if (-not $baseline.ContainsKey($id)) {
        try {
            $approval = "eng/compatibility/api-additions/$id.md"
            Require-Approval -Path $approval -RequiredFields @("Issue", "Reason", "Surface") -ChangedFiles $changedFiles
            Write-Warning "Approved new public package: $id $($right.Version)"
        }
        catch {
            $failures.Add("New package '$id' has no same-PR public-surface approval: $($_.Exception.Message)")
        }
        continue
    }

    $left = $baseline[$id]
    Write-Host "ApiCompat: $id baseline=$($left.Version) current=$($right.Version)"

    $normalExit = Invoke-ApiCompat -Current $right.Path -Baseline $left.Path
    if ($normalExit -ne 0) {
        try {
            $approval = "eng/compatibility/breaking/$id.md"
            Require-Approval -Path $approval -RequiredFields @("Issue", "Reason", "Migration", "Version") -ChangedFiles $changedFiles
            if ($right.Version -eq $left.Version) {
                throw "Breaking change requires a package version change; both packages are $($right.Version)."
            }
            Write-Warning "Approved breaking API change: $id $($left.Version) -> $($right.Version)"
        }
        catch {
            $failures.Add("Breaking API change in '$id': $($_.Exception.Message)")
        }
        continue
    }

    $strictExit = Invoke-ApiCompat -Current $right.Path -Baseline $left.Path -Strict
    if ($strictExit -ne 0) {
        try {
            $approval = "eng/compatibility/api-additions/$id.md"
            Require-Approval -Path $approval -RequiredFields @("Issue", "Reason", "Surface") -ChangedFiles $changedFiles
            Write-Warning "Approved additive public API change: $id"
        }
        catch {
            $failures.Add("Additive public API drift in '$id': $($_.Exception.Message)")
        }
    }
}

if ($failures.Count -gt 0) {
    Write-Error ("Compatibility gate failed:" + [Environment]::NewLine + " - " + ($failures -join ([Environment]::NewLine + " - ")))
    exit 1
}

Write-Host "Runtime package compatibility gate passed for $($current.Count) current packages against exact base."
