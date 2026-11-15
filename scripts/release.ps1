param([switch] $SkipTests)
. "$PSScriptRoot/common.ps1"
& "$PSScriptRoot/publish.ps1" -SkipTests:$SkipTests
$versionSpec = [xml](Get-Content -Raw -LiteralPath (Join-Path $RepositoryRoot 'Directory.Build.props'))
$version = $versionSpec.Project.PropertyGroup.Version
$release = Join-Path $RepositoryRoot "artifacts\release\WinKit-$version-win-x64"
New-Item -ItemType Directory -Force -Path $release | Out-Null
$sourceExe = Join-Path $RepositoryRoot 'artifacts\publish\win-x64\WinKit.exe'
Copy-Item -LiteralPath $sourceExe -Destination (Join-Path $release 'WinKit.exe') -Force
Copy-Item -LiteralPath (Join-Path $RepositoryRoot 'docs\使用说明.txt') -Destination $release -Force
$hash = (Get-FileHash -LiteralPath (Join-Path $release 'WinKit.exe') -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  WinKit.exe" | Set-Content -Encoding utf8 -LiteralPath (Join-Path $release 'SHA256.txt')
$zip = "$release.zip"
Compress-Archive -Path "$release\*" -DestinationPath $zip -Force
Get-Item -LiteralPath $zip | Select-Object FullName,Length
