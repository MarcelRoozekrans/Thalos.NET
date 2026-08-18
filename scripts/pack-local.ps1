#Requires -Version 7
<#
.SYNOPSIS
  Packs every Thalos.NET package into the local folder feed used by Daedalus (Plan B) at version 0.3.0-<Suffix> (nine packages since 0.3.0).
.PARAMETER Suffix
  Pre-release suffix; defaults to local.<yyyyMMddHHmmss> so every run produces a new, monotonically increasing version.
.PARAMETER Feed
  Local feed folder; defaults to C:\Projects\Prive\.nuget-local.
#>
param(
    [string]$Suffix = ("local." + (Get-Date -Format "yyyyMMddHHmmss")),
    [string]$Feed = "C:\Projects\Prive\.nuget-local"
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot

New-Item -ItemType Directory -Force $Feed | Out-Null
dotnet pack "$repo\Thalos.NET.slnx" -c Release -o $Feed -p:VersionSuffix=$Suffix --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet pack failed ($LASTEXITCODE)" }

$version = "0.3.0-$Suffix"
Write-Host ""
Write-Host "Packed Thalos.NET $version to $Feed"
Get-ChildItem $Feed -Filter "Thalos.NET*.$version.*nupkg" | Sort-Object Name | ForEach-Object { Write-Host ("  {0,-60} {1,8:N0} KB" -f $_.Name, ($_.Length / 1KB)) }
Write-Host ""
Write-Host "Pin in Directory.Packages.props: <PackageVersion Include=""Thalos.NET"" Version=""$version"" /> (and the other Thalos.NET.* ids)"
