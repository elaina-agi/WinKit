param([Parameter(Mandatory)][string] $FixtureDirectory, [string] $OutputDirectory, [string] $Exe)
. "$PSScriptRoot/common.ps1"
if (-not $Exe) { $Exe = Join-Path $RepositoryRoot 'artifacts\publish\win-x64\WinKit.exe' }
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $RepositoryRoot 'artifacts\ui-smoke' }
$Exe = (Resolve-Path -LiteralPath $Exe).Path
$FixtureDirectory = (Resolve-Path -LiteralPath $FixtureDirectory).Path
$OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
# GUI executables must be explicitly waited for; a bare '& WinKit.exe' can return
# before the smoke work completes and leave LASTEXITCODE unset/stale.
$argumentLine = '--ui-smoke "{0}" "{1}"' -f $FixtureDirectory, $OutputDirectory
$process = Start-Process -FilePath $Exe -ArgumentList $argumentLine -PassThru -Wait
if ($process.ExitCode -ne 0) { throw "UI smoke 失败，退出码 $($process.ExitCode)。检查 $OutputDirectory 中的状态文件。" }
$state = Get-Content -Raw -LiteralPath (Join-Path $OutputDirectory 'smoke-state.json') | ConvertFrom-Json
if (-not $state.Passed) { throw 'UI smoke 状态未通过。' }
$state | Select-Object Passed,FileCount,DefaultSelected,SelectedAfterSelectAll,SelectedAfterClear,BindingErrors
