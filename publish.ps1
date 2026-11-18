# Builds artifacts\rr.exe: one file, needs the .NET 10 runtime that is already on this machine.
$ErrorActionPreference = 'Stop'
dotnet test $PSScriptRoot
if ($LASTEXITCODE -ne 0) { throw 'Tests failed; not publishing.' }
dotnet publish "$PSScriptRoot\src\RepoRunner.Cli" -c Release -r win-x64 `
    -p:SelfContained=false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none `
    -o "$PSScriptRoot\artifacts"
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
Write-Host "Built $PSScriptRoot\artifacts\rr.exe"
