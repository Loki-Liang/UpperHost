param(
    [Parameter(Mandatory = $true)]
    [string]$ApiCompatPath
)

$ErrorActionPreference = "Stop"
if (Get-Variable PSNativeCommandUseErrorActionPreference -ErrorAction SilentlyContinue) {
    $PSNativeCommandUseErrorActionPreference = $false
}

if (-not (Test-Path $ApiCompatPath)) {
    throw "ApiCompat executable not found: $ApiCompatPath"
}

$root = Join-Path ([System.IO.Path]::GetTempPath()) ("opendevicestudio-apicompat-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force $root | Out-Null

function Build-Probe {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$Source
    )

    $dir = Join-Path $root $Name
    New-Item -ItemType Directory -Force $dir | Out-Null

    @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <AssemblyName>CompatibilityProbe</AssemblyName>
    <Nullable>enable</Nullable>
  </PropertyGroup>
</Project>
"@ | Set-Content -Encoding UTF8 (Join-Path $dir "CompatibilityProbe.csproj")

    $Source | Set-Content -Encoding UTF8 (Join-Path $dir "PublicApi.cs")
    dotnet build (Join-Path $dir "CompatibilityProbe.csproj") -c Release --nologo | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to build compatibility probe '$Name'."
    }

    return Join-Path $dir "bin/Release/net10.0/CompatibilityProbe.dll"
}

try {
    $breakingBaseline = Build-Probe "breaking-baseline" @"
namespace CompatibilityProbe;
public sealed class PublicApi
{
    public void Stable() { }
    public void Removed() { }
}
"@
    $breakingCurrent = Build-Probe "breaking-current" @"
namespace CompatibilityProbe;
public sealed class PublicApi
{
    public void Stable() { }
}
"@

    & $ApiCompatPath -l $breakingBaseline -r $breakingCurrent
    if ($LASTEXITCODE -eq 0) {
        throw "ApiCompat regression: a removed public member was not detected."
    }
    Write-Host "Verified: normal ApiCompat rejects removed public API."

    $additiveBaseline = Build-Probe "additive-baseline" @"
namespace CompatibilityProbe;
public sealed class PublicApi
{
    public void Stable() { }
}
"@
    $additiveCurrent = Build-Probe "additive-current" @"
namespace CompatibilityProbe;
public sealed class PublicApi
{
    public void Stable() { }
    public void Added() { }
}
"@

    & $ApiCompatPath -l $additiveBaseline -r $additiveCurrent
    if ($LASTEXITCODE -ne 0) {
        throw "ApiCompat regression: additive API should be compatible in normal mode."
    }

    & $ApiCompatPath -l $additiveBaseline -r $additiveCurrent --strict-mode
    if ($LASTEXITCODE -eq 0) {
        throw "ApiCompat regression: strict mode did not detect additive public API drift."
    }
    Write-Host "Verified: strict ApiCompat identifies additive public API drift."
}
finally {
    Remove-Item -Recurse -Force $root -ErrorAction SilentlyContinue
}

# Expected ApiCompat failures above are asserted intentionally. Reset the process
# exit code so a successful deliberate-break test cannot fail the GitHub Actions step.
exit 0
