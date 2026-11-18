# Builds artifacts\rr.exe: one file, needs the .NET 10 runtime that is already on this machine.
$ErrorActionPreference = 'Stop'
dotnet test $PSScriptRoot
if ($LASTEXITCODE -ne 0) { throw 'Tests failed; not publishing.' }
dotnet publish "$PSScriptRoot\src\RepoRunner.Cli" -c Release -r win-x64 `
    -p:SelfContained=false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none `
    -o "$PSScriptRoot\artifacts"
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
Write-Host "Built $PSScriptRoot\artifacts\rr.exe"

# The window: a local web app served to your browser. Start it with artifacts\app\RepoRunner.exe
dotnet publish "$PSScriptRoot\src\RepoRunner.App" -c Release -p:DebugType=none -o "$PSScriptRoot\artifacts\app"
if ($LASTEXITCODE -ne 0) { throw 'Publishing the app failed.' }
Write-Host "Built $PSScriptRoot\artifacts\app\RepoRunner.exe"
