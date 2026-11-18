# Puts a "RepoRunner" shortcut on your Desktop that starts the app and opens it in your browser.
# Delete the shortcut to undo this; nothing else is installed.
$ErrorActionPreference = 'Stop'
$exe = Join-Path $PSScriptRoot 'artifacts\app\RepoRunner.exe'
if (-not (Test-Path $exe)) { throw "Build first: run .\publish.ps1 (missing $exe)" }
$link = Join-Path ([Environment]::GetFolderPath('Desktop')) 'RepoRunner.lnk'
$shortcut = (New-Object -ComObject WScript.Shell).CreateShortcut($link)
$shortcut.TargetPath = $exe
$shortcut.WorkingDirectory = Split-Path $exe
$shortcut.WindowStyle = 7   # start the small server window minimised; the page opens in the browser
$shortcut.Description = 'RepoRunner - run repos against an environment from the encrypted vault'
$shortcut.Save()
Write-Host "Created $link"
