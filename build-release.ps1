#!/usr/bin/env pwsh
<#
    Publishes FileViewer.App as a self-contained, single-file, ReadyToRun win-x64 build and zips
    the output into release/FileViewer-win-x64.zip. Run from the repo root:

        pwsh ./build-release.ps1
#>
$ErrorActionPreference = "Stop"

$repoRoot = $PSScriptRoot
$publishDir = Join-Path $repoRoot "publish/FileViewer-win-x64"
$releaseDir = Join-Path $repoRoot "release"
$zipPath = Join-Path $releaseDir "FileViewer-win-x64.zip"

if (Test-Path $publishDir) {
    Remove-Item -Recurse -Force $publishDir
}
New-Item -ItemType Directory -Force -Path $releaseDir | Out-Null

Write-Host "Publishing FileViewer.App (Release, win-x64, self-contained, single-file, ReadyToRun)..."
dotnet publish (Join-Path $repoRoot "src/FileViewer.App") -c Release -r win-x64 -o $publishDir
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

if (Test-Path $zipPath) {
    Remove-Item -Force $zipPath
}

Write-Host "Zipping publish output to $zipPath..."
Compress-Archive -Path (Join-Path $publishDir "*") -DestinationPath $zipPath

Write-Host "Done: $zipPath"
