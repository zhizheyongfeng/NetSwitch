# Publish the Windows x64 single-file build.
param(
    [string]$Output = "$PSScriptRoot\..\artifacts\win-x64"
)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot '..\src\NetSwitch.App\NetSwitch.App.csproj'

dotnet publish $project `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -o $Output

Write-Host "Published to $Output"
