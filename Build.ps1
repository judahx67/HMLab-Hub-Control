param([switch]$Force, [string]$OutputPath)
$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
$sources = @('HubProtocol.cs','ReceivingLink.cs','Wallpaper.cs','MediaLibrary.cs','CropSession.cs','LiveHid.cs','Playlist.cs','FullResolutionPlaylist.cs','LivePlaylistPanel.cs','Program.cs','SelfCheck.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
$output = if ($OutputPath) { [IO.Path]::GetFullPath($OutputPath) } else { Join-Path $PSScriptRoot 'HMLab-Hub-Control.exe' }
if (-not (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'tools\Gif70.ps1'))) { throw 'Missing tools\Gif70.ps1; keep the cropper with the application sources.' }
$needsBuild = $Force -or -not (Test-Path -LiteralPath $output)
if (-not $needsBuild) { $needsBuild = @($sources | Where-Object { (Get-Item -LiteralPath $_).LastWriteTimeUtc -gt (Get-Item -LiteralPath $output).LastWriteTimeUtc }).Count -gt 0 }
if ($needsBuild) {
    & $compiler /nologo /target:winexe /optimize+ "/out:$output" /r:System.Windows.Forms.dll /r:System.Drawing.dll $sources
    if ($LASTEXITCODE -ne 0) { throw 'HMLab Hub Control compilation failed.' }
}
Write-Output $output
