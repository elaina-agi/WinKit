param([ValidateSet('Debug', 'Release')][string] $Configuration = 'Release')
. "$PSScriptRoot/common.ps1"
Push-Location $RepositoryRoot
try {
    Invoke-DotNet -Arguments @('run', '--project', 'tests/WinKit.Core.Tests/WinKit.Core.Tests.csproj', '-c', $Configuration)
} finally {
    Pop-Location
}
