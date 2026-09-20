#!/usr/bin/env pwsh
<#
    Publishes FileViewer.App as a self-contained, single-file, ReadyToRun win-x64 build and zips it
    into release/FileViewer-win-x64-v<version>.zip, alongside the README and changelog. Run from the
    repo root:

        pwsh ./build-release.ps1

    The version is read from Directory.Build.props rather than passed in, so the file name, the
    assembly and the release tag can't drift apart.
#>
$ErrorActionPreference = "Stop"

$repoRoot = $PSScriptRoot

function Get-ProjectVersion {
    param([string]$PropsPath)

    $propsText = Get-Content -Raw -Path $PropsPath
    $match = [regex]::Match($propsText, '<Version>([^<]+)</Version>')
    if (-not $match.Success) {
        throw "No <Version> element found in $PropsPath. Set one before cutting a release."
    }
    return $match.Groups[1].Value.Trim()
}

$version = Get-ProjectVersion -PropsPath (Join-Path $repoRoot "Directory.Build.props")
$buildName = "FileViewer-win-x64-v$version"

$publishDir = Join-Path $repoRoot "publish/$buildName"
$releaseDir = Join-Path $repoRoot "release"
$zipPath = Join-Path $releaseDir "$buildName.zip"

if (Test-Path $publishDir) {
    Remove-Item -Recurse -Force $publishDir
}
New-Item -ItemType Directory -Force -Path $releaseDir | Out-Null

Write-Host "Publishing $buildName (Release, win-x64, self-contained, single-file, ReadyToRun)..."
dotnet publish (Join-Path $repoRoot "src/FileViewer.App") -c Release -r win-x64 -o $publishDir
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

# Shipped next to the exe so a downloaded build can say what it is and what changed, without
# needing the repository.
foreach ($doc in @("README.md", "CHANGELOG.md")) {
    $source = Join-Path $repoRoot $doc
    if (Test-Path $source) {
        Copy-Item -Path $source -Destination $publishDir -Force
    }
}

if (Test-Path $zipPath) {
    Remove-Item -Force $zipPath
}

Write-Host "Zipping publish output to $zipPath..."
Compress-Archive -Path (Join-Path $publishDir "*") -DestinationPath $zipPath

Write-Host "Done: $zipPath"
