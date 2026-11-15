# 仅在仓库 .tools 目录安装 SDK，不改变系统安装或 PATH。
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$spec = Get-Content -Raw -LiteralPath (Join-Path $root 'global.json') | ConvertFrom-Json
$version = $spec.sdk.version
$destination = Join-Path $root '.tools\dotnet'
$executable = Join-Path $destination 'dotnet.exe'
if (Test-Path -LiteralPath $executable) {
    & $executable --version
    if ($LASTEXITCODE -ne 0) { throw '已有 SDK 无法运行。' }
    Write-Host '已有仓库本地 SDK。'
    return
}
$metadata = Invoke-RestMethod 'https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json'
$sdk = @($metadata.releases | ForEach-Object { $_.sdks } | Where-Object { $_.version -eq $version }) | Select-Object -First 1
if (-not $sdk) { throw "官方元数据中未找到 SDK $version。" }
$file = $sdk.files | Where-Object { $_.rid -eq 'win-x64' -and $_.name -like '*.zip' } | Select-Object -First 1
if (-not $file) { throw '未找到 Windows x64 SDK 压缩包。' }
New-Item -ItemType Directory -Force -Path (Join-Path $root '.tools') | Out-Null
$zip = Join-Path $root '.tools\dotnet-sdk.zip'
Write-Host "下载 .NET SDK $version …"
Invoke-WebRequest -Uri $file.url -OutFile $zip
if ((Get-FileHash -LiteralPath $zip -Algorithm SHA512).Hash -ne $file.hash) { throw 'SDK SHA512 校验失败。' }
Expand-Archive -LiteralPath $zip -DestinationPath $destination -Force
& $executable --info
if ($LASTEXITCODE -ne 0) { throw 'SDK 安装检查失败。' }
