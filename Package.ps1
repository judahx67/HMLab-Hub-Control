# SPDX-License-Identifier: AGPL-3.0-or-later
param(
    [Parameter(Mandatory=$true)][ValidatePattern('^\d+\.\d+\.\d+(?:-[A-Za-z0-9.-]+)?$')][string]$Version,
    [string]$OutputDirectory,
    [string]$CacheDirectory
)
$ErrorActionPreference = 'Stop'

function Assert-Hash([string]$Path, [string]$Expected) {
    if ((Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -ne $Expected) {
        throw "SHA-256 mismatch: $Path"
    }
}

function Get-PinnedDownload($Asset, [string]$Cache) {
    $destination = Join-Path $Cache $Asset.Name
    if (-not (Test-Path -LiteralPath $destination -PathType Leaf)) {
        $temporary = $destination + '.' + [guid]::NewGuid().ToString('N') + '.tmp'
        try {
            Invoke-WebRequest -Uri $Asset.Url -OutFile $temporary -UseBasicParsing
            Assert-Hash $temporary $Asset.Sha256
            Move-Item -LiteralPath $temporary -Destination $destination
        } finally {
            if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary }
        }
    }
    Assert-Hash $destination $Asset.Sha256
    return $destination
}

Push-Location $PSScriptRoot
try {
    $dirty = @(git status --porcelain --untracked-files=no)
    if ($LASTEXITCODE -ne 0) { throw 'Cannot inspect the source checkout.' }
    if ($dirty.Count) { throw 'Commit tracked source changes before packaging.' }
    $revision = git rev-parse HEAD
    if ($LASTEXITCODE -ne 0) { throw 'Cannot identify the source commit.' }
    $pin = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'packaging\ffmpeg.json') -Raw | ConvertFrom-Json
    $output = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } else { Join-Path $PSScriptRoot ".checks\packages\v$Version" }
    $cache = if ($CacheDirectory) { [IO.Path]::GetFullPath($CacheDirectory) } else { Join-Path $PSScriptRoot '.checks\ffmpeg-downloads' }
    if (Test-Path -LiteralPath $output) { throw "Output directory already exists; choose a new directory: $output" }
    [void][IO.Directory]::CreateDirectory($cache)
    $binaryArchive = Get-PinnedDownload $pin.Binary $cache
    $sourceArchive = Get-PinnedDownload $pin.Source $cache
    [void][IO.Directory]::CreateDirectory($output)
    $work = Join-Path $output '.work'
    [void][IO.Directory]::CreateDirectory($work)
    $sourceZip = Join-Path $work 'controller-source.zip'
    git archive --format=zip --prefix=HMLab-Hub-Control/ "--output=$sourceZip" HEAD
    if ($LASTEXITCODE -ne 0) { throw 'Cannot archive the controller source.' }
    Expand-Archive -LiteralPath $sourceZip -DestinationPath $work
    $package = Join-Path $work 'HMLab-Hub-Control'
    $upstream = Join-Path $work 'ffmpeg'
    Expand-Archive -LiteralPath $binaryArchive -DestinationPath $upstream
    $vendor = Join-Path $upstream $pin.ArchiveRoot
    $manifest = Get-Content -LiteralPath (Join-Path $vendor 'manifest.json') -Raw | ConvertFrom-Json
    if ($manifest.av_version_info -ne $pin.Version -or $manifest.licensing.spdx -ne $pin.License -or $manifest.licensing.nonfree) {
        throw 'FFmpeg build identity or license differs from the pinned build.'
    }
    foreach ($name in $pin.RuntimeFiles) {
        $relative = 'bin/' + $name
        $expected = $manifest.artifacts_sha256.PSObject.Properties[$relative].Value
        if (-not $expected) { throw "Missing upstream runtime checksum: $relative" }
        $target = Join-Path $package ('tools\' + $name)
        Copy-Item -LiteralPath (Join-Path $vendor $relative) -Destination $target
        Assert-Hash $target $expected
    }
    $licenses = Join-Path $package 'licenses\ffmpeg'
    [void][IO.Directory]::CreateDirectory((Split-Path $licenses -Parent))
    Copy-Item -LiteralPath (Join-Path $vendor 'licenses') -Destination $licenses -Recurse
    Copy-Item -LiteralPath (Join-Path $vendor 'manifest.json') -Destination $licenses
    Copy-Item -LiteralPath (Join-Path $vendor 'share\tas-ffmpeg') -Destination (Join-Path $licenses 'build') -Recurse
    foreach ($file in Get-ChildItem -LiteralPath (Join-Path $vendor 'licenses') -Recurse -File) {
        $relative = $file.FullName.Substring((Join-Path $vendor 'licenses').Length + 1)
        Assert-Hash (Join-Path $licenses $relative) (Get-FileHash -LiteralPath $file.FullName).Hash
    }
    $sourceUrl = "https://github.com/judahx67/HMLab-Hub-Control/releases/download/v$Version/" + $pin.Source.Name
    $notice = @"
# FFmpeg credits and corresponding source

FFmpeg $($pin.Version), copyright (c) 2000-2026 the FFmpeg developers.
Project: https://ffmpeg.org/
Windows build: $($pin.UpstreamRelease)
Build license: $($pin.License); dependencies retain their own licenses.

These executables and shared libraries are unmodified. They run as separate
command-line tools used by Gif70. All upstream license notices are preserved
in this directory; build configuration is under build/ and identity and
runtime checksums are in manifest.json. Thanks to the FFmpeg contributors
and the TAS-FFMPEG maintainer.

The exact FFmpeg, dependency sources and build scripts are available without
charge alongside this application's Windows ZIP:
$sourceUrl

Archive: $($pin.Source.Name)
SHA-256: $($pin.Source.Sha256)
Upstream source: $($pin.Source.Url)
The source archive is not needed to run the application.
"@
    [IO.File]::WriteAllText((Join-Path $licenses 'SOURCE.md'),$notice,[Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText((Join-Path $package 'RELEASE.txt'),"HMLab Hub Control v$Version`r`nSource commit: $revision`r`nFFmpeg: $($pin.Version)`r`n",[Text.UTF8Encoding]::new($false))
    $executable = & (Join-Path $package 'Build.ps1') -Force
    $checks = Join-Path $work 'checks'
    $shell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $savedPath = $env:PATH
    try {
        # Prove the release runs with only the bundled FFmpeg tools available.
        $env:PATH = (Join-Path $env:WINDIR 'System32') + ';' + (Split-Path $shell -Parent)
        $versionText = & (Join-Path $package 'tools\ffmpeg.exe') -version
        if ($LASTEXITCODE -ne 0 -or $versionText[0] -notlike "ffmpeg version $($pin.Version) *") { throw 'Bundled FFmpeg did not start.' }
        & (Join-Path $package 'tools\ffprobe.exe') -version | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Bundled ffprobe did not start.' }
        $check = Start-Process -FilePath $executable -ArgumentList @('--self-check',('"{0}"' -f $checks)) -WindowStyle Hidden -PassThru
        if (-not $check.WaitForExit(60000)) {
            $check.Kill()
            throw 'Packaged controller checks timed out; stopped only the test process.'
        }
        if ($check.ExitCode -ne 0) { throw 'Packaged controller checks failed.' }
        & $shell -NoProfile -STA -ExecutionPolicy Bypass -File (Join-Path $package 'tools\Gif70.ps1') -SelfCheck -CheckDirectory (Join-Path $checks 'cropper')
        if ($LASTEXITCODE -ne 0) { throw 'Packaged cropper checks failed.' }
    } finally { $env:PATH = $savedPath }
    $archive = Join-Path $output "HMLab-Hub-Control-v$Version-windows-x64.zip"
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    Add-Type -AssemblyName System.IO.Compression
    $zip = [IO.Compression.ZipFile]::Open($archive,[IO.Compression.ZipArchiveMode]::Create)
    try {
        # .NET Framework's CreateFromDirectory writes legacy backslash entry names.
        foreach ($file in Get-ChildItem -LiteralPath $package -Recurse -File) {
            $entry = 'HMLab-Hub-Control/' + $file.FullName.Substring($package.Length + 1).Replace('\','/')
            [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip,$file.FullName,$entry,[IO.Compression.CompressionLevel]::Optimal)
        }
    } finally { $zip.Dispose() }
    $sourceOutput = Join-Path $output $pin.Source.Name
    Copy-Item -LiteralPath $sourceArchive -Destination $sourceOutput
    Assert-Hash $sourceOutput $pin.Source.Sha256
    $sums = foreach ($file in @($archive,$sourceOutput)) {
        (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + [IO.Path]::GetFileName($file)
    }
    [IO.File]::WriteAllText((Join-Path $output 'SHA256SUMS.txt'),(($sums -join "`n") + "`n"),[Text.UTF8Encoding]::new($false))
    Write-Output "Packaged and checked: $output"
    Write-Output 'Publish the Windows ZIP, corresponding-source archive and SHA256SUMS.txt together.'
} finally { Pop-Location }
