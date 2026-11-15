param([switch] $SkipTests)
. "$PSScriptRoot/common.ps1"
Push-Location $RepositoryRoot
try {
    if (-not $SkipTests) { & "$PSScriptRoot/test.ps1" }
    $output = Join-Path $RepositoryRoot 'artifacts\publish\win-x64'
    Invoke-DotNet -Arguments @(
        'publish', 'src/WinKit.Desktop/WinKit.Desktop.csproj', '-c', 'Release',
        '-r', 'win-x64', '--self-contained', 'true',
        '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true',
        '-p:EnableCompressionInSingleFile=true', '-p:DebugType=None', '-p:DebugSymbols=false',
        '-o', $output
    )
    $exe = Join-Path $output 'WinKit.exe'
    if (-not (Test-Path -LiteralPath $exe)) { throw '发布输出缺少 WinKit.exe。' }
    Get-FileHash -LiteralPath $exe -Algorithm SHA256
    Write-Host "发布完成：$exe"
} finally {
    Pop-Location
}
