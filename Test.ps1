param([switch]$Cropper)
$ErrorActionPreference = 'Stop'

$checks = Join-Path $PSScriptRoot '.checks'
$build = Join-Path $checks 'bin'
New-Item -ItemType Directory -Force -Path $build | Out-Null
$executable = & (Join-Path $PSScriptRoot 'Build.ps1') -OutputPath (Join-Path $build 'HMLab-Hub-Control.Tests.exe')
$report = Join-Path $checks 'controller'
$process = Start-Process -FilePath $executable -ArgumentList @('--self-check',('"{0}"' -f $report)) -WindowStyle Hidden -PassThru
if (-not $process.WaitForExit(60000)) {
    $process.Kill()
    throw 'Controller checks timed out after 60 seconds; stopped only the test process.'
}
if ($process.ExitCode -ne 0) {
    $failure = Join-Path $build 'last-error.txt'
    if (Test-Path -LiteralPath $failure) { Get-Content -LiteralPath $failure | Write-Output }
    throw "Controller checks failed (exit $($process.ExitCode))."
}
Get-Content -LiteralPath (Join-Path $report 'self-check.txt')

if ($Cropper) {
    & powershell.exe -NoProfile -STA -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'tools\Gif70.ps1') -SelfCheck -CheckDirectory (Join-Path $checks 'cropper')
    if ($LASTEXITCODE -ne 0) { throw "Cropper checks failed (exit $LASTEXITCODE)." }
}
