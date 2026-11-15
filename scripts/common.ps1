$ErrorActionPreference = 'Stop'
$RepositoryRoot = Split-Path $PSScriptRoot -Parent
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$localSdk = Join-Path $RepositoryRoot '.tools\dotnet\dotnet.exe'
if (Test-Path -LiteralPath $localSdk) {
    $DotNet = $localSdk
    $env:DOTNET_ROOT = Split-Path $localSdk -Parent
} else {
    $systemSdk = Get-Command dotnet -ErrorAction SilentlyContinue
    if (-not $systemSdk) {
        throw '找不到 .NET SDK，请先运行 scripts/setup-sdk.ps1。'
    }
    $DotNet = $systemSdk.Source
}

function Invoke-DotNet {
    param([Parameter(Mandatory)][string[]] $Arguments)
    & $DotNet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet 命令失败，退出码 $LASTEXITCODE。"
    }
}
