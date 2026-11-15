param([string] $Exe)
. "$PSScriptRoot/common.ps1"
if (-not $Exe) { $Exe = Join-Path $RepositoryRoot 'artifacts\publish\win-x64\WinKit.exe' }
$Exe = (Resolve-Path -LiteralPath $Exe).Path
Push-Location $RepositoryRoot
try {
    Invoke-DotNet -Arguments @('run', '--project', 'tests/WinKit.Desktop.Tests/WinKit.Desktop.Tests.csproj', '-c', 'Release', '--', $Exe)
} finally { Pop-Location }
