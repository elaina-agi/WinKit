param([ValidateSet('Debug', 'Release')][string] $Configuration = 'Release')
. "$PSScriptRoot/common.ps1"
Push-Location $RepositoryRoot
try {
    Invoke-DotNet -Arguments @('build', 'WinKit.slnx', '-c', $Configuration, '--nologo')
} finally {
    Pop-Location
}
