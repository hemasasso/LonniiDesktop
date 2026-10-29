# Builds the Lonnii Desktop test installer: artifacts\installer\LonniiBusiness-Setup-<version>.exe
#
#   powershell -File deploy\windows\build-installer.ps1 [-Version 1.0.0]
#
# Publishes self-contained win-x64, so test machines need no .NET runtime.
#
# Configuration "DebugInstaller" rather than Release: SixLabors.ImageSharp 4.x fails any build whose
# configuration does not start with "Debug" until a licence key is added. Optimize is forced on, so
# the code is release-grade; only the licence check is dodged. Fine for testing, NOT for customers -
# add the ImageSharp licence (SixLaborsLicenseKey) and switch to Release before shipping.
param(
    [string]$Version = "1.0.0"
)

$ErrorActionPreference = "Stop"
$root = Resolve-Path "$PSScriptRoot\..\.."
$publish = Join-Path $root "artifacts\publish"
$config = "DebugInstaller"

if (Test-Path $publish) { Remove-Item -Recurse -Force $publish }

$common = @("-c", $config, "-r", "win-x64", "--self-contained", "true",
            "-p:Optimize=true", "-p:DebugType=none", "-p:Version=$Version")

dotnet publish "$root\src\Lonnii.Api\Lonnii.Api.csproj" @common -o "$publish\Api"
if ($LASTEXITCODE -ne 0) { throw "API publish failed" }

dotnet publish "$root\src\Lonnii.Client\Lonnii.Client.csproj" @common -o "$publish\Client"
if ($LASTEXITCODE -ne 0) { throw "Client publish failed" }

# A publish once produced an empty compiled-XAML resource (12 bytes) and the app died at startup;
# refuse to package that. Loaded from bytes so the file is not left locked.
$client = [Reflection.Assembly]::Load([IO.File]::ReadAllBytes("$publish\Client\Lonnii.Client.dll"))
$xaml = $client.GetManifestResourceStream("Lonnii.Client.g.resources")
if (-not $xaml -or $xaml.Length -lt 10000) { throw "Client XAML resources are missing or empty - rebuild" }

# Never ship development settings.
Remove-Item -Force -ErrorAction SilentlyContinue "$publish\Api\appsettings.Development.json"

$iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
          "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
          "$env:ProgramFiles\Inno Setup 6\ISCC.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup 6 not found: winget install JRSoftware.InnoSetup" }

& $iscc "/DAppVersion=$Version" "/DPublishDir=$publish" "$PSScriptRoot\lonnii.iss"
if ($LASTEXITCODE -ne 0) { throw "Installer compile failed" }

# The throwaway configuration's intermediate folders are not needed afterwards.
Get-ChildItem "$root\src" -Directory -Recurse -Filter $config | Where-Object { $_.Parent.Name -in "bin", "obj" } |
    Remove-Item -Recurse -Force

Write-Host "`nInstaller: $root\artifacts\installer\LonniiBusiness-Setup-$Version.exe"
