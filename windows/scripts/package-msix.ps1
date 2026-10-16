<#
.SYNOPSIS
  Builds and signs a sideloadable MSIX of Bridge.Windows (docs/development.md, Packaging).

.EXAMPLE
  # First time on a dev machine: make a self-signed "CN=Bridge Dev" certificate and package x64.
  .\windows\scripts\package-msix.ps1 -CreateDevCert

.EXAMPLE
  # With a real certificate (its subject must match Publisher in Package.appxmanifest):
  .\windows\scripts\package-msix.ps1 -Platform arm64 -CertThumbprint 0123ABCD...

.NOTES
  Needs Visual Studio 2022 (MSBuild with the Windows App SDK / MSIX tooling) on Windows.
  Installing a package signed with the dev certificate needs that certificate trusted once:
  the script exports it next to the package and prints the (admin) command to trust it.
#>
param(
    [ValidateSet('x64', 'arm64', 'x86')] [string] $Platform = 'x64',
    [string] $CertThumbprint,
    [switch] $CreateDevCert
)
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$project = Join-Path $root 'Bridge.Windows\Bridge.Windows.csproj'
$manifest = [xml](Get-Content (Join-Path $root 'Bridge.Windows\Package.appxmanifest'))
$publisher = $manifest.Package.Identity.Publisher

if ($CreateDevCert) {
    $existing = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -eq $publisher -and $_.NotAfter -gt (Get-Date) } | Select-Object -First 1
    if ($existing) { $CertThumbprint = $existing.Thumbprint; Write-Host "Using existing dev certificate $CertThumbprint" }
    else {
        $cert = New-SelfSignedCertificate -Type Custom -Subject $publisher -KeyUsage DigitalSignature `
            -FriendlyName 'Bridge dev package signing' -CertStoreLocation Cert:\CurrentUser\My `
            -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}') -NotAfter (Get-Date).AddYears(2)
        $CertThumbprint = $cert.Thumbprint
        Write-Host "Created dev certificate $CertThumbprint ($publisher)"
    }
}
if (-not $CertThumbprint) { throw 'Pass -CertThumbprint, or -CreateDevCert for a local test certificate.' }
$cert = Get-Item "Cert:\CurrentUser\My\$CertThumbprint"
if ($cert.Subject -ne $publisher) { throw "Certificate subject '$($cert.Subject)' must match the manifest Publisher '$publisher'." }

$msbuild = & "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" -latest -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
if (-not $msbuild) { throw 'MSBuild not found: install Visual Studio 2022 with the Windows App SDK workload.' }

& $msbuild $project /restore /m /v:minimal `
    /p:Configuration=Release /p:Platform=$Platform `
    /p:WindowsPackageType=MSIX /p:GenerateAppxPackageOnBuild=true `
    /p:AppxPackageSigningEnabled=true /p:PackageCertificateThumbprint=$CertThumbprint
if ($LASTEXITCODE -ne 0) { throw "MSBuild failed ($LASTEXITCODE)" }

$out = Join-Path $root 'AppPackages'
$package = Get-ChildItem $out -Recurse -Filter '*.msix' | Sort-Object LastWriteTime -Descending | Select-Object -First 1
$cer = Join-Path $package.DirectoryName 'Bridge-signing.cer'
Export-Certificate -Cert $cert -FilePath $cer | Out-Null
Write-Host ""
Write-Host "Package: $($package.FullName)"
Write-Host "To install on a test PC (once per certificate, in an elevated PowerShell):"
Write-Host "  Import-Certificate -FilePath '$cer' -CertStoreLocation Cert:\LocalMachine\TrustedPeople"
Write-Host "Then double-click the .msix, or: Add-AppxPackage '$($package.FullName)'"
