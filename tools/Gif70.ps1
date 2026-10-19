# SPDX-License-Identifier: AGPL-3.0-or-later
<#
.SYNOPSIS
  Rotate, trim and crop video/GIF into exactly 70 frames in landscape or portrait.
.DESCRIPTION
  Drag the crop to move it; drag its bottom-right handle to resize it.
  Drag the two timeline ends to trim; click inside to preview a time.
  Start/End here and numeric seconds provide precise adjustments.
  Output can be scaled up or down with linked dimensions and Smaller/Larger buttons.
  Output uses multiples of 17 x 9 (425 x 225) or 9 x 17 (225 x 425).
  Rotate left/right turns source pixels; changing layout resets the crop.
  Play/Pause loops the selected 70-frame interval; Cropped preview shows the result.
  The selected interval is sampled evenly, rather than taking its first 70 frames.
  Very short selections play over at least 1.4 seconds for reliable GIF timing.
  Cancel (or Esc) stops processing while busy and closes the window while idle.
  ffmpeg.exe and ffprobe.exe must be on PATH or beside this script.
.EXAMPLE
  .\Gif70.ps1
.EXAMPLE
  .\Gif70.ps1 'C:\Videos\clip.mp4'
.EXAMPLE
  .\Gif70.ps1 -SelfCheck
#>
[CmdletBinding()]
param([string]$Path, [switch]$SelfCheck, [string]$CheckDirectory,
    [string]$ExportDirectory, [string]$ResultDirectory, [switch]$Portrait)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
[Windows.Forms.Application]::EnableVisualStyles()
$script:app = @{ UI = $false; Busy = $false; Cancel = $false; Closing = $false
    Media = $null; Image = $null; Crop = $null; Updating = $false; Drag = $null
    Units = 25; Seek = 0.0; RangeDrag = $null; ActiveEnd = 'Start'; LastOutput = $null }
$script:ratioW = 17; $script:ratioH = 9
$script:playback = @{ Frames = $null; Playing = $false; Index = 0; Position = 0.0; Clock = $null; Duration = 0.0; Start = 0.0; Length = 0.0 }
if ($Portrait) { $script:ratioW = 9; $script:ratioH = 17 }
$workRoot = [IO.Path]::GetTempPath()
if ($SelfCheck) { $workRoot = $PSScriptRoot }
$script:work = Join-Path $workRoot ('Gif70-' + [guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($script:work)

function Find-Tool([string]$Name) {
    $local = Join-Path $PSScriptRoot ($Name + '.exe')
    if (Test-Path -LiteralPath $local -PathType Leaf) { return $local }
    $command = Get-Command ($Name + '.exe') -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($command) { return $command.Source }
    throw "Missing $Name.exe. Install FFmpeg (which includes ffprobe) or place both executables beside this script."
}

# Windows command-line quoting, including embedded quotes and trailing backslashes.
# No shell evaluates filenames or filter arguments.
function Quote-Argument([string]$Value) {
    '"' + [regex]::Replace([regex]::Replace($Value, '(\\*)"', '$1$1\"'), '(\\+)$', '$1$1') + '"'
}

function Invoke-Tool([string]$Exe, [string[]]$Arguments) {
    $info = New-Object Diagnostics.ProcessStartInfo
    $info.FileName = $Exe
    $info.Arguments = (($Arguments | ForEach-Object { Quote-Argument $_ }) -join ' ')
    $info.UseShellExecute = $false; $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true; $info.RedirectStandardError = $true
    $process = New-Object Diagnostics.Process
    $process.StartInfo = $info
    try {
        [void]$process.Start()
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        while (-not $process.WaitForExit(40)) {
            if ($script:app.UI) { [Windows.Forms.Application]::DoEvents() }
            if ($script:app.Cancel) {
                if (-not $process.HasExited) { $process.Kill() }
                $process.WaitForExit()
                throw [OperationCanceledException]::new('Cancelled.')
            }
        }
        $out = $stdout.GetAwaiter().GetResult()
        $err = $stderr.GetAwaiter().GetResult()
        if ($script:app.Cancel) { throw [OperationCanceledException]::new('Cancelled.') }
        if ($process.ExitCode -ne 0) { throw "$([IO.Path]::GetFileName($Exe)) failed:`n$err" }
        return $out
    } finally { $process.Dispose() }
}

function Number([double]$Value) { $Value.ToString('0.#########', [Globalization.CultureInfo]::InvariantCulture) }

function Get-Media([string]$InputPath) {
    $resolved = (Resolve-Path -LiteralPath $InputPath).Path
    $json = Invoke-Tool $script:ffprobe @('-v','error','-show_streams','-show_format','-of','json',$resolved)
    $probe = $json | ConvertFrom-Json
    if ($probe.format.format_name -eq 'gif') {
        $json = Invoke-Tool $script:ffprobe @('-v','error','-ignore_loop','1','-min_delay','0',
            '-show_streams','-show_format','-of','json',$resolved)
        $probe = $json | ConvertFrom-Json
    }
    $video = $probe.streams | Where-Object { $_.codec_type -eq 'video' -and -not $_.disposition.attached_pic } | Select-Object -First 1
    if (-not $video) { throw 'The file has no video or animation stream.' }
    $duration = 0.0
    foreach ($candidate in @($video.duration, $probe.format.duration)) {
        $parsed = 0.0
        if ([double]::TryParse([string]$candidate, [Globalization.NumberStyles]::Float,
                [Globalization.CultureInfo]::InvariantCulture, [ref]$parsed) -and $parsed -gt 0) {
            $duration = $parsed; break
        }
    }
    if ($duration -le 0) { throw 'Could not determine the animation duration. Try remuxing the input to MP4 first.' }
    [pscustomobject]@{ Path = $resolved; Duration = $duration; Index = [int]$video.index
        IsGif = ($probe.format.format_name -eq 'gif'); Width = 0; Height = 0; Rotation = 0 }
}

function Input-Arguments($Media, [double]$Start) {
    $items = @('-hide_banner','-loglevel','error','-nostdin')
    if ($Media.IsGif) { $items += @('-ignore_loop','1','-min_delay','0') }
    # Preserve the frame held across the trim boundary. GIF seeks cannot reliably
    # recover that frame, so decode GIFs from the beginning; videos keep keyframe preroll.
    $seek = $Start
    if ($Media.IsGif) { $seek = 0 }
    $items += @('-noaccurate_seek','-ss',(Number $seek))
    $items += @('-i',$Media.Path)
    return $items
}

# FFmpeg autorotation precedes this filter; square pixels make preview and export
# use the same coordinate system, including anamorphic and phone footage.
$script:normalize = "scale=w='max(1,round(iw*sar))':h=ih,setsar=1"
function Get-NormalizeFilter($Media) {
    switch ([int]$Media.Rotation) {
        90 { return $script:normalize + ',transpose=clock' }
        180 { return $script:normalize + ',hflip,vflip' }
        270 { return $script:normalize + ',transpose=cclock' }
        default { return $script:normalize }
    }
}
function Get-Preview($Media, [double]$Time) {
    $file = Join-Path $script:work ('preview-' + [guid]::NewGuid().ToString('N') + '.png')
    try {
        $sampleStart = 0.0
        if ($Media.IsGif) { $sampleStart = $Time }
        $previewFilter = 'setpts=PTS-{0}/TB,tpad=stop_mode=clone:stop=-1,fps=fps=1000:start_time=0,trim=end_frame=1,{1}' -f
            (Number $sampleStart),(Get-NormalizeFilter $Media)
        $argsList = (Input-Arguments $Media $Time) + @('-map',"0:$($Media.Index)",'-an',
            '-vf',$previewFilter,'-frames:v','1','-update','1','-y',$file)
        [void](Invoke-Tool $script:ffmpeg $argsList)
        if (-not (Test-Path -LiteralPath $file)) { throw 'No frame at this time. Choose an earlier preview time.' }
        $stream = [IO.MemoryStream]::new([IO.File]::ReadAllBytes($file))
        try {
            $loaded = [Drawing.Image]::FromStream($stream)
            try { return [Drawing.Bitmap]::new($loaded) } finally { $loaded.Dispose() }
        } finally { $stream.Dispose() }
    } finally { if (Test-Path -LiteralPath $file) { [IO.File]::Delete($file) } }
}

function New-Crop([int]$Width, [int]$Height) {
    if ($Width -lt 1 -or $Height -lt 1) { throw 'The source has no usable pixels.' }
    $w = [int][Math]::Max(1.0,[Math]::Floor([Math]::Min([double]$Width,$Height*[double]$script:ratioW/$script:ratioH)))
    $h = [int][Math]::Min($Height,[Math]::Max(1,[Math]::Round($w*[double]$script:ratioH/$script:ratioW)))
    [pscustomobject]@{ X = [int][Math]::Floor(($Width-$w)/2.0)
        Y = [int][Math]::Floor(($Height-$h)/2.0); W = $w; H = $h }
}

function Set-CropSize([double]$Units, [double]$Left = [double]::NaN, [double]$Top = [double]::NaN) {
    $crop = $script:app.Crop; $media = $script:app.Media
    $maxWidth = [Math]::Floor([Math]::Min([double]$media.Width,$media.Height*[double]$script:ratioW/$script:ratioH))
    $width = [int][Math]::Max(1,[Math]::Min($maxWidth,[Math]::Round($Units*$script:ratioW)))
    $height = [int][Math]::Min($media.Height,[Math]::Max(1,[Math]::Round($width*[double]$script:ratioH/$script:ratioW)))
    if ([double]::IsNaN($Left)) { $Left = $crop.X+($crop.W-$width)/2.0 }
    if ([double]::IsNaN($Top)) { $Top = $crop.Y+($crop.H-$height)/2.0 }
    # Reposition at the bounds instead of treating the old top-left as a size limit.
    $crop.X = [int][Math]::Round([Math]::Max(0,[Math]::Min($media.Width-$width,$Left)))
    $crop.Y = [int][Math]::Round([Math]::Max(0,[Math]::Min($media.Height-$height,$Top)))
    $crop.W = $width; $crop.H = $height
    Sync-Crop
}

function Rotate-Source([int]$Degrees) {
    if ($script:app.Busy -or -not $script:app.Media) { return }
    Stop-Playback -Release
    $media = $script:app.Media
    $width = $media.Width; $height = $media.Height
    $media.Rotation = ($media.Rotation+$Degrees+360)%360
    if ($Degrees -eq 90) {
        $script:app.Image.RotateFlip([Drawing.RotateFlipType]::Rotate90FlipNone)
    } else {
        $script:app.Image.RotateFlip([Drawing.RotateFlipType]::Rotate270FlipNone)
    }
    $media.Width = $height; $media.Height = $width
    $script:app.Crop = New-Crop $media.Width $media.Height
    $script:app.Updating = $true
    try {
        $cropWidth.Value = 1; $cropHeight.Value = 1
        $cropWidth.Maximum = $media.Width; $cropHeight.Maximum = $media.Height
    } finally { $script:app.Updating = $false }
    Sync-Crop
    $status.Text = 'Source rotated; crop reset to the chosen output layout.'
    $sourceLabel.Text = '{0} | {1} x {2} | source rotated {3} degrees' -f [IO.Path]::GetFileName($media.Path),$media.Width,$media.Height,$media.Rotation
}

function Get-RangeGeometry {
    [pscustomobject]@{ Left = 14.0; Span = [Math]::Max(1.0,$timeline.ClientSize.Width-28.0); Y = 19.0 }
}

function Set-TrimHandle([string]$End, [double]$Time) {
    if (-not $script:app.Media) { return }
    $duration = [double]$script:app.Media.Duration
    $gap = [Math]::Min(0.001,$duration)
    if ($End -eq 'Start') {
        $startBox.Value = [decimal][Math]::Max(0.0,[Math]::Min([double]$endBox.Value-$gap,$Time))
        $seekBox.Value = $startBox.Value
    } else {
        $endBox.Value = [decimal][Math]::Min($duration,[Math]::Max([double]$startBox.Value+$gap,$Time))
        $seekBox.Value = $endBox.Value
    }
    $timeline.Invalidate()
}

function Sync-Trim {
    if ($script:app.Updating) { return }
    Stop-Playback -Release
    $timeline.Invalidate(); Update-Summary
}

function Get-SamplingFilter($Media, [double]$Start, [double]$Duration, [double]$PlayDuration) {
    $sampleStart = 0.0
    if ($Media.IsGif) { $sampleStart = $Start }
    return ('setpts=PTS-{1}/TB,tpad=stop_mode=clone:stop=-1,fps=fps=70/{0}:start_time=0,' -f (Number $Duration),(Number $sampleStart)) +
        ('trim=end_frame=70,settb=1/100,setpts=round(N*{0}*100/70)' -f (Number $PlayDuration))
}

function Stop-Playback([switch]$Release) {
    if ($script:playback.Playing) {
        $script:playback.Position += $script:playback.Clock.Elapsed.TotalMilliseconds
        $script:playback.Clock.Stop(); $script:playback.Playing = $false
    }
    if (Get-Variable playbackTimer -Scope Script -ErrorAction SilentlyContinue) { $playbackTimer.Stop() }
    if (Get-Variable playButton -Scope Script -ErrorAction SilentlyContinue) { $playButton.Text = 'Play' }
    if ($Release) {
        if ($script:playback.Frames) { foreach ($frame in $script:playback.Frames) { $frame.Dispose() } }
        $script:playback.Frames = $null; $script:playback.Index = 0; $script:playback.Position = 0.0; $script:playback.Clock = $null
    }
    if ($script:app.UI) { $canvas.Invalidate() }
}

function Update-Playback {
    if (-not $script:playback.Playing) { return }
    $elapsed = ($script:playback.Position+$script:playback.Clock.Elapsed.TotalMilliseconds)%$script:playback.Duration
    $index = 0
    for ($i = 1; $i -lt 70; $i++) {
        $at = [Math]::Round($i*[Math]::Max(1.4,$script:playback.Length)*100/70,[MidpointRounding]::AwayFromZero)*10
        if ($at -gt $elapsed) { break }; $index = $i
    }
    $script:playback.Index = $index
    $script:app.Seek = $script:playback.Start+$index*$script:playback.Length/70
    $script:app.Updating = $true
    try { $seekBox.Value = [decimal]$script:app.Seek } finally { $script:app.Updating = $false }
    $canvas.Invalidate(); $timeline.Invalidate()
}

function Start-Playback {
    if ($script:app.Busy -or -not $script:app.Media) { return }
    if ($script:playback.Playing) { Stop-Playback; $status.Text = 'Paused. Adjust the crop or press Play to continue.'; return }
    $start = [double]$startBox.Value; $length = [double]$endBox.Value-$start
    if ($length -le 0) { $status.Text = 'End must be after start.'; return }
    if (-not $script:playback.Frames) {
        $script:app.Cancel = $false; Set-Busy $true 'Preparing 70-frame playback preview...'
        $prefix = 'play-' + [guid]::NewGuid().ToString('N')
        $frames = [Collections.Generic.List[Drawing.Bitmap]]::new()
        try {
            # Decode once on demand, at bounded preview resolution. Crop edits reuse
            # these full-source frames; the export still uses full-resolution pixels.
            $filter = (Get-NormalizeFilter $script:app.Media) + ",scale=w='min(640,iw)':h='min(640,ih)':force_original_aspect_ratio=decrease," +
                (Get-SamplingFilter $script:app.Media $start $length ([Math]::Max(1.4,$length))) + ',format=rgb24'
            $pattern = Join-Path $script:work ($prefix + '-%03d.png')
            [void](Invoke-Tool $script:ffmpeg ((Input-Arguments $script:app.Media $start) + @('-map',"0:$($script:app.Media.Index)",'-an',
                '-vf',$filter,'-frames:v','70','-fps_mode','passthrough','-y',$pattern)))
            for ($i = 1; $i -le 70; $i++) {
                if ($script:app.Cancel) { throw [OperationCanceledException]::new('Cancelled.') }
                $file = Join-Path $script:work ($prefix + ('-{0:000}.png' -f $i))
                $loaded = [Drawing.Image]::FromFile($file)
                try { $frames.Add([Drawing.Bitmap]::new($loaded)) } finally { $loaded.Dispose() }
            }
            $script:playback.Frames = $frames.ToArray(); $frames.Clear()
            $script:playback.Start = $start; $script:playback.Length = $length
            $script:playback.Duration = [Math]::Round([Math]::Max(1.4,$length)*100)*10
        } catch { Show-Failure $_; return }
        finally {
            foreach ($frame in $frames) { $frame.Dispose() }
            foreach ($file in [IO.Directory]::GetFiles($script:work,($prefix + '-*.png'))) { [IO.File]::Delete($file) }
            Set-Busy $false
        }
    }
    if ($script:app.Closing) { Stop-Playback -Release; return }
    $script:playback.Clock = [Diagnostics.Stopwatch]::StartNew(); $script:playback.Playing = $true
    $playButton.Text = 'Pause'; $playbackTimer.Start(); Update-Playback
    $status.Text = 'Playing selected 70 frames in a loop. Crop edits update the preview; no audio.'
}

function Export-Gif($Media, $Crop, [double]$Start, [double]$End, [int]$Units, [string]$OutputPath) {
    if ($Start -lt 0 -or $End -le $Start -or $End -gt $Media.Duration + 0.000001) { throw 'Choose a valid start/end range inside the source.' }
    if ($Units -lt 1 -or $Units -gt 3855) { throw 'Output size is outside GIF dimensions.' }
    if ($Crop.X -lt 0 -or $Crop.Y -lt 0 -or $Crop.W -lt 1 -or $Crop.H -lt 1 -or
        $Crop.X+$Crop.W -gt $Media.Width -or $Crop.Y+$Crop.H -gt $Media.Height) { throw 'Crop lies outside the source.' }
    $destination = [IO.Path]::GetFullPath($OutputPath)
    if ([string]::Equals($destination, $Media.Path, [StringComparison]::OrdinalIgnoreCase)) { throw 'Choose an output filename different from the source.' }
    $duration = $End-$Start
    $playDuration = [Math]::Max(1.4, $duration)
    if ($playDuration -gt 45874.5) { throw 'Select a shorter interval: GIF frame delays cannot represent this duration.' }
    $width = $script:ratioW*$Units; $height = $script:ratioH*$Units
    $lastDelay = [int]([Math]::Round($playDuration*100) - [Math]::Round(69*$playDuration*100/70))
    # Pad before sampling so sparse/static sources still yield 70 encoded frames.
    # Limit before palette generation so the palette branch always reaches EOF.
    $filter = '[0:{0}]{1},crop={2}:{3}:{4}:{5}:exact=1,scale={6}:{7}:flags=lanczos,setsar=1,' -f
        $Media.Index,(Get-NormalizeFilter $Media),$Crop.W,$Crop.H,$Crop.X,$Crop.Y,$width,$height
    $filter += (Get-SamplingFilter $Media $Start $duration $playDuration) + ',split[a][b];'
    $filter += '[a]palettegen=reserve_transparent=1[p];[b][p]paletteuse=dither=sierra2_4a[out]'
    # Stage in the destination directory; failed/cancelled encodes never replace a file.
    $staged = Join-Path ([IO.Path]::GetDirectoryName($destination)) ('.gif70-' + [guid]::NewGuid().ToString('N') + '.gif')
    try {
        $argsList = (Input-Arguments $Media $Start) + @('-filter_complex',$filter,
            '-map','[out]','-an','-frames:v','70','-fps_mode','passthrough','-enc_time_base','1:100',
            '-loop','0','-final_delay',[string]$lastDelay,'-y',$staged)
        [void](Invoke-Tool $script:ffmpeg $argsList)
        $verified = Test-Gif $staged $width $height
        if ($script:app.Cancel) { throw [OperationCanceledException]::new('Cancelled.') }
        if ([IO.File]::Exists($destination)) { [IO.File]::Replace($staged, $destination, [NullString]::Value) }
        else { [IO.File]::Move($staged, $destination) }
        return [pscustomobject]@{ Path = $destination; Width = $width; Height = $height
            Frames = 70; Duration = $verified.Duration; Bytes = (Get-Item -LiteralPath $destination).Length }
    } finally { if (Test-Path -LiteralPath $staged) { [IO.File]::Delete($staged) } }
}

function Test-Gif([string]$File, [int]$Width, [int]$Height) {
    $json = Invoke-Tool $script:ffprobe @('-v','error','-ignore_loop','1','-min_delay','0',
        '-count_frames','-select_streams','v:0','-show_entries','stream=width,height,nb_read_frames:format=duration','-of','json',$File)
    $probe = $json | ConvertFrom-Json
    $video = $probe.streams[0]
    if ([int]$video.nb_read_frames -ne 70 -or [int]$video.width -ne $Width -or [int]$video.height -ne $Height) {
        throw "Output verification failed: got $($video.width) x $($video.height), $($video.nb_read_frames) frames."
    }
    [pscustomobject]@{ Duration = [double]::Parse([string]$probe.format.duration, [Globalization.CultureInfo]::InvariantCulture) }
}

# Optional hub-app integration. Publish only after Export-Gif has verified and
# committed the output; rename the receipt so the parent never reads half a path.
function Publish-Export([string]$File) {
    if (-not $ResultDirectory) { return }
    [void][IO.Directory]::CreateDirectory($ResultDirectory)
    $receipt = Join-Path $ResultDirectory ([guid]::NewGuid().ToString('N'))
    [IO.File]::WriteAllText(($receipt + '.tmp'), [IO.Path]::GetFullPath($File), [Text.UTF8Encoding]::new($false))
    [IO.File]::Move(($receipt + '.tmp'), ($receipt + '.result'))
}

function Set-Busy([bool]$Value, [string]$Message = '') {
    if ($Value) { Stop-Playback }
    $script:app.Busy = $Value
    if (-not $script:app.UI) { return }
    $openButton.Enabled = -not $Value
    $orientationRow.Enabled = -not $Value
    $rotateLeft.Enabled = $rotateRight.Enabled = (-not $Value -and $null -ne $script:app.Media)
    $playButton.Enabled = $stopButton.Enabled = $croppedPreview.Enabled = (-not $Value -and $null -ne $script:app.Media)
    foreach ($control in @($seekRow,$trimRow,$cropRow,$outputRow,$canvas,$exportButton)) {
        $control.Enabled = (-not $Value -and $null -ne $script:app.Media)
    }
    $cancelButton.Enabled = -not $script:app.Closing
    $progress.Visible = $Value
    if ($Message) { $status.Text = $Message }
    [Windows.Forms.Application]::DoEvents()
}

function Show-Failure($Failure) {
    if ($script:app.Closing) { return }
    if ($Failure.Exception -is [OperationCanceledException]) { $status.Text = 'Cancelled.'; return }
    $status.Text = 'Could not complete the operation.'
    [void][Windows.Forms.MessageBox]::Show($form, $Failure.Exception.Message, 'Gif70', 'OK', 'Error')
}

function Update-Summary {
    if (-not $script:app.Media) { return }
    $length = [double]$endBox.Value - [double]$startBox.Value
    if ($length -le 0) { $summary.Text = 'End must be after start.'; return }
    $play = [Math]::Max(1.4,$length)
    $summary.Text = '{0} x {1}  |  70 frames  |  {2:0.###} s selected  |  ~{3:0.##} fps' -f
        ($script:ratioW*$script:app.Units),($script:ratioH*$script:app.Units),$length,(70/$play)
    if ($length -lt 1.4) { $summary.Text += '  |  plays over 1.4 s' }
}

function Sync-Crop {
    $script:app.Updating = $true
    try {
        $cropWidth.Value = $script:app.Crop.W; $cropHeight.Value = $script:app.Crop.H
        $cropPosition.Text = 'x {0}, y {1}' -f $script:app.Crop.X,$script:app.Crop.Y
    } finally { $script:app.Updating = $false }
    $canvas.Invalidate()
}

function Show-Preview([double]$Time) {
    Stop-Playback -Release
    $bitmap = Get-Preview $script:app.Media $Time
    if ($bitmap.Width -ne $script:app.Media.Width -or $bitmap.Height -ne $script:app.Media.Height) {
        $bitmap.Dispose(); throw 'The source changes dimensions during playback; convert it to a fixed-size video first.'
    }
    if ($script:app.Image) { $script:app.Image.Dispose() }
    $script:app.Image = $bitmap; $script:app.Seek = $Time
    $script:app.Updating = $true
    try {
        $seekBox.Value = [decimal]$Time
        $timeline.Invalidate()
    } finally { $script:app.Updating = $false }
    $canvas.Invalidate()
}

function Open-Media([string]$File) {
    if (-not $File) {
        $dialog = New-Object Windows.Forms.OpenFileDialog
        $dialog.Filter = 'Video and GIF|*.mp4;*.mkv;*.mov;*.webm;*.avi;*.m4v;*.gif;*.wmv;*.mpeg;*.mpg;*.ts|All files|*.*'
        try { if ($dialog.ShowDialog($form) -ne 'OK') { return }; $File = $dialog.FileName }
        finally { $dialog.Dispose() }
    }
    Stop-Playback -Release
    $script:app.Cancel = $false
    Set-Busy $true 'Reading source and loading preview...'
    $image = $null
    try {
        $media = Get-Media $File
        $image = Get-Preview $media 0
        $media.Width = $image.Width; $media.Height = $image.Height
        $crop = New-Crop $media.Width $media.Height
        if ($script:app.Image) { $script:app.Image.Dispose() }
        $script:app.Media = $media; $script:app.Image = $image; $image = $null
        $script:app.Crop = $crop; $script:app.Seek = 0.0
        $script:app.Updating = $true
        try {
            $startBox.Value = 0; $endBox.Value = 0; $seekBox.Value = 0
            $maxTime = [decimal]$media.Duration
            $startBox.Maximum = $maxTime; $endBox.Maximum = $maxTime; $seekBox.Maximum = $maxTime
            $endBox.Value = $maxTime
            $cropWidth.Maximum = $media.Width; $cropHeight.Maximum = $media.Height
            $timeline.Invalidate()
        } finally { $script:app.Updating = $false }
        $sourceLabel.Text = '{0}  |  {1} x {2}  |  {3:0.###} s' -f [IO.Path]::GetFileName($media.Path),$media.Width,$media.Height,$media.Duration
        $tooltip.SetToolTip($sourceLabel,$media.Path)
        Sync-Crop; Update-Summary
        $status.Text = 'Choose a time range and crop, then export.'
    } catch { Show-Failure $_ }
    finally { if ($image) { $image.Dispose() }; Set-Busy $false }
}

function Get-CanvasGeometry {
    if (-not $script:app.Image) { return $null }
    $scale = [Math]::Min(($canvas.ClientSize.Width-24)/[double]$script:app.Media.Width,
        ($canvas.ClientSize.Height-24)/[double]$script:app.Media.Height)
    $width = $script:app.Media.Width*$scale; $height = $script:app.Media.Height*$scale
    [pscustomobject]@{ Scale = $scale; X = ($canvas.ClientSize.Width-$width)/2
        Y = ($canvas.ClientSize.Height-$height)/2; W = $width; H = $height }
}

function Start-UI {
    $script:app.UI = $true
    $script:form = New-Object Windows.Forms.Form
    $form.Text = 'Gif70 - trim, crop, resize'
    $form.Font = New-Object Drawing.Font 'Segoe UI', 10
    $area = [Windows.Forms.Screen]::PrimaryScreen.WorkingArea
    $form.ClientSize = New-Object Drawing.Size ([Math]::Min(1000,$area.Width-40)),([Math]::Min(800,$area.Height-60))
    $form.MinimumSize = New-Object Drawing.Size 930,640
    $form.StartPosition = 'CenterScreen'
    $form.BackColor = [Drawing.Color]::FromArgb(245,247,250)
    $form.AutoScaleMode = 'Dpi'
    $layout = New-Object Windows.Forms.TableLayoutPanel
    $layout.Dock = 'Fill'; $layout.Padding = New-Object Windows.Forms.Padding 12
    $layout.ColumnCount = 1; $layout.RowCount = 8
    foreach ($height in @(46,42,0,52,48,48,54,78)) {
        $style = New-Object Windows.Forms.RowStyle
        if ($height -eq 0) { $style.SizeType = 'Percent'; $style.Height = 100 }
        else { $style.SizeType = 'Absolute'; $style.Height = $height }
        [void]$layout.RowStyles.Add($style)
    }
    $form.Controls.Add($layout)

    function New-Row {
        $row = New-Object Windows.Forms.FlowLayoutPanel
        $row.Dock = 'Fill'; $row.WrapContents = $false; $row.Margin = New-Object Windows.Forms.Padding 0
        return $row
    }
    function New-Label([string]$Text, [int]$Width = 0) {
        $label = New-Object Windows.Forms.Label
        $label.Text = $Text; $label.AutoSize = ($Width -eq 0)
        if ($Width) { $label.Width = $Width; $label.Height = 32 }
        $label.Margin = New-Object Windows.Forms.Padding 4,9,6,0
        return $label
    }
    function New-Button([string]$Text, [int]$Width = 100) {
        $button = New-Object Windows.Forms.Button
        $button.Text = $Text; $button.Size = New-Object Drawing.Size $Width,32
        $button.FlatStyle = 'Flat'; $button.BackColor = [Drawing.Color]::White
        return $button
    }
    function New-Number([decimal]$Min, [decimal]$Max, [decimal]$Value, [int]$Decimals = 0, [decimal]$Step = 1) {
        $box = New-Object Windows.Forms.NumericUpDown
        $box.Minimum = $Min; $box.Maximum = $Max; $box.DecimalPlaces = $Decimals
        $box.Increment = $Step; $box.Value = $Value; $box.Width = 96
        $box.Margin = New-Object Windows.Forms.Padding 3,6,6,0
        return $box
    }

    $header = New-Row
    $script:openButton = New-Button 'Open video / GIF' 150
    $script:sourceLabel = New-Label 'Choose a video or animated GIF.' 760
    $sourceLabel.AutoEllipsis = $true
    $header.Controls.AddRange(@($openButton,$sourceLabel)); $layout.Controls.Add($header,0,0)
    $script:orientationRow = New-Row
    $script:outputLayout = New-Object Windows.Forms.ComboBox
    $outputLayout.DropDownStyle = 'DropDownList'; $outputLayout.Width = 205
    [void]$outputLayout.Items.Add('Landscape (17:9)'); [void]$outputLayout.Items.Add('Portrait (9:17)')
    $outputLayout.SelectedIndex = [int][bool]$Portrait
    $script:rotateLeft = New-Button 'Rotate left' 110; $script:rotateRight = New-Button 'Rotate right' 110
    $orientationRow.Controls.AddRange(@((New-Label 'Output layout'),$outputLayout,(New-Label 'Source'),$rotateLeft,$rotateRight))
    $script:playButton = New-Button 'Play' 85; $script:stopButton = New-Button 'Stop' 75
    $orientationRow.Controls.AddRange(@($playButton,$stopButton))
    $layout.Controls.Add($orientationRow,0,1)
    # PictureBox provides double-buffered painting without compiling UI helpers.
    $script:canvas = New-Object Windows.Forms.PictureBox
    $canvas.Dock = 'Fill'; $canvas.BackColor = [Drawing.Color]::FromArgb(27,31,39)
    $canvas.Margin = New-Object Windows.Forms.Padding 0,0,0,6
    $layout.Controls.Add($canvas,0,2)
    $script:seekRow = New-Row
    $seekRow.Controls.Add((New-Label 'Trim / seek'))
    $script:timeline = New-Object Windows.Forms.PictureBox
    # PictureBox normally skips keyboard focus; enable it for precise handle adjustments.
    $styleMethod = [Windows.Forms.Control].GetMethod('SetStyle',([Reflection.BindingFlags]::Instance -bor [Reflection.BindingFlags]::NonPublic))
    [void]$styleMethod.Invoke($timeline,@([Windows.Forms.ControlStyles]::Selectable,$true))
    $timeline.TabStop = $true
    $timeline.AccessibleName = 'Trim range: start and end handles; click the track to preview'
    $timeline.Width = 460; $timeline.Height = 44
    $script:seekBox = New-Number 0 1 0 3 0.1
    $previewButton = New-Button 'Show frame' 108
    $seekRow.Controls.AddRange(@($timeline,$seekBox,(New-Label 's'),$previewButton))
    $layout.Controls.Add($seekRow,0,3)
    $script:trimRow = New-Row
    $script:startBox = New-Number 0 1 0 3 0.1; $script:endBox = New-Number 0 1 1 3 0.1
    $script:setStart = New-Button 'Start here' 100; $script:setEnd = New-Button 'End here' 100
    $trimRow.Controls.AddRange(@((New-Label 'Trim   Start (s)'),$startBox,$setStart,(New-Label 'End (s)'),$endBox,$setEnd))
    $script:croppedPreview = New-Object Windows.Forms.CheckBox
    $croppedPreview.Text = 'Cropped preview'; $croppedPreview.Appearance = 'Button'
    $croppedPreview.Size = New-Object Drawing.Size 155,32; $croppedPreview.TextAlign = 'MiddleCenter'
    $trimRow.Controls.Add($croppedPreview)
    $layout.Controls.Add($trimRow,0,4)
    $script:cropRow = New-Row
    $script:cropWidth = New-Number 1 65535 425; $script:cropHeight = New-Number 1 65535 225
    $resetCrop = New-Button 'Reset crop' 105
    $script:cropPosition = New-Label 'x 0, y 0' 160
    $cropRow.Controls.AddRange(@((New-Label 'Crop   W'),$cropWidth,(New-Label 'H'),$cropHeight,$resetCrop,$cropPosition,
        (New-Label 'Drag box / bottom-right grip.')))
    $layout.Controls.Add($cropRow,0,5)
    $script:outputRow = New-Row
    $script:outWidth = New-Number 1 65535 ($script:ratioW*25) 0 $script:ratioW
    $script:outHeight = New-Number 1 65535 ($script:ratioH*25) 0 $script:ratioH
    $script:smallerButton = New-Button 'Smaller' 82; $script:largerButton = New-Button 'Larger' 82
    $script:outputScaleLabel = New-Label '17:9 | 100%' 190
    $outputRow.Controls.AddRange(@((New-Label 'Output   W'),$outWidth,(New-Label 'H'),$outHeight,$smallerButton,$largerButton,$outputScaleLabel))
    $layout.Controls.Add($outputRow,0,6)
    $footer = New-Object Windows.Forms.TableLayoutPanel
    $footer.Dock = 'Fill'; $footer.ColumnCount = 2; $footer.RowCount = 2
    [void]$footer.ColumnStyles.Add((New-Object Windows.Forms.ColumnStyle 'Percent',100))
    [void]$footer.ColumnStyles.Add((New-Object Windows.Forms.ColumnStyle 'Absolute',280))
    $script:summary = New-Label '70 frames total | 17:9 | looping GIF' 650
    $script:status = New-Label 'Open a source to begin.' 650
    $actions = New-Row
    $script:exportButton = New-Button 'Export GIF...' 148
    $exportButton.BackColor = [Drawing.Color]::FromArgb(38,110,210); $exportButton.ForeColor = [Drawing.Color]::White
    $script:cancelButton = New-Button 'Cancel' 100
    $form.CancelButton = $cancelButton
    # Esc should click the button; only the idle handler may close the modal form.
    $cancelButton.DialogResult = 'None'
    $actions.Controls.AddRange(@($exportButton,$cancelButton))
    $script:progress = New-Object Windows.Forms.ProgressBar
    $progress.Style = 'Marquee'; $progress.Dock = 'Fill'; $progress.Visible = $false
    $footer.Controls.Add($summary,0,0); $footer.Controls.Add($status,0,1)
    $footer.Controls.Add($actions,1,0); $footer.Controls.Add($progress,1,1)
    $layout.Controls.Add($footer,0,7)
    $script:tooltip = New-Object Windows.Forms.ToolTip
    $tooltip.SetToolTip($canvas,'Move the crop by dragging inside it. Resize with its bottom-right grip; the chosen output ratio stays locked.')
    $tooltip.SetToolTip($endBox,'End is exclusive. The 70 frames are sampled across the start/end interval.')
    $tooltip.SetToolTip($timeline,'Drag S/E to trim. Click or drag the track to seek. Arrow keys adjust the selected end; Space switches ends.')
    $tooltip.SetToolTip($outputScaleLabel,'100% is 425 x 225 landscape or 225 x 425 portrait. Both dimensions scale together.')
    $tooltip.SetToolTip($rotateRight,'Rotate the source 90 degrees clockwise and reset the crop; click twice for upside down.')
    $tooltip.SetToolTip($rotateLeft,'Rotate the source 90 degrees counterclockwise and reset the crop.')
    $tooltip.SetToolTip($playButton,'Play/Pause the selected interval at the same 70-frame sampling and timing as export. First Play prepares a small preview; no audio.')
    $tooltip.SetToolTip($stopButton,'Stop playback, release its cached frames, and return to the trim start.')
    $tooltip.SetToolTip($croppedPreview,'Show only the crop at the output aspect ratio. Turn this off to drag the crop box; numeric crop controls still work.')

    $script:onPaint = {
        param($sender,$event)
        $g = $event.Graphics
        $geometry = Get-CanvasGeometry
        if (-not $geometry) {
            $brush = New-Object Drawing.SolidBrush ([Drawing.Color]::LightGray)
            try { $g.DrawString('Open a video or GIF to choose a crop.', $form.Font, $brush, 24,24) } finally { $brush.Dispose() }
            return
        }
        $image = $script:app.Image
        if ($script:playback.Frames) { $image = $script:playback.Frames[$script:playback.Index] }
        $g.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBilinear
        if ($croppedPreview.Checked) {
            $crop = $script:app.Crop
            $factor = [Math]::Min(($canvas.ClientSize.Width-24)/[double]$script:ratioW,($canvas.ClientSize.Height-24)/[double]$script:ratioH)
            $w = $script:ratioW*$factor; $h = $script:ratioH*$factor
            $dest = [Drawing.RectangleF]::new(($canvas.ClientSize.Width-$w)/2,($canvas.ClientSize.Height-$h)/2,$w,$h)
            $sx = $image.Width/[double]$script:app.Media.Width; $sy = $image.Height/[double]$script:app.Media.Height
            $rect = [Drawing.RectangleF]::new(($crop.X*$sx),($crop.Y*$sy),($crop.W*$sx),($crop.H*$sy))
            $g.DrawImage($image,$dest,$rect,[Drawing.GraphicsUnit]::Pixel)
            return
        }
        $imageRect = [Drawing.RectangleF]::new($geometry.X,$geometry.Y,$geometry.W,$geometry.H)
        $g.DrawImage($image,$imageRect)
        $crop = $script:app.Crop
        $rect = [Drawing.RectangleF]::new(($geometry.X+$crop.X*$geometry.Scale),($geometry.Y+$crop.Y*$geometry.Scale),
            ($crop.W*$geometry.Scale),($crop.H*$geometry.Scale))
        $shade = New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(155,0,0,0))
        $region = [Drawing.Region]::new($imageRect); $region.Exclude($rect)
        $pen = New-Object Drawing.Pen ([Drawing.Color]::FromArgb(255,195,72)),2
        $gripBrush = New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(255,195,72))
        try {
            $g.FillRegion($shade,$region)
            $g.DrawRectangle($pen,$rect.X,$rect.Y,$rect.Width,$rect.Height)
            $g.FillRectangle($gripBrush,($rect.Right-7),($rect.Bottom-7),14,14)
        } finally { $shade.Dispose(); $region.Dispose(); $pen.Dispose(); $gripBrush.Dispose() }
    }
    $script:onDown = {
        param($sender,$event)
        if ($script:app.Busy -or -not $script:app.Media -or $croppedPreview.Checked -or $event.Button -ne 'Left') { return }
        $geo = Get-CanvasGeometry; $crop = $script:app.Crop
        $x = ($event.X-$geo.X)/$geo.Scale; $y = ($event.Y-$geo.Y)/$geo.Scale
        $hitGrip = ([Math]::Abs($x-($crop.X+$crop.W))*$geo.Scale -le 12 -and
            [Math]::Abs($y-($crop.Y+$crop.H))*$geo.Scale -le 12)
        if ($hitGrip -or ($x -ge $crop.X -and $x -le $crop.X+$crop.W -and $y -ge $crop.Y -and $y -le $crop.Y+$crop.H)) {
            $mode = 'Move'; if ($hitGrip) { $mode = 'Size' }
            $script:app.Drag = @{ Mode = $mode; X = $x; Y = $y; Left = $crop.X; Top = $crop.Y; W = $crop.W; H = $crop.H }
            $canvas.Capture = $true
        }
    }
    $script:onMove = {
        param($sender,$event)
        if (-not $script:app.Drag) { return }
        $geo = Get-CanvasGeometry; $drag = $script:app.Drag; $crop = $script:app.Crop
        $dx = ($event.X-$geo.X)/$geo.Scale-$drag.X; $dy = ($event.Y-$geo.Y)/$geo.Scale-$drag.Y
        if ($drag.Mode -eq 'Move') {
            $crop.X = [int][Math]::Round([Math]::Max(0,[Math]::Min($script:app.Media.Width-$crop.W,$drag.Left+$dx)))
            $crop.Y = [int][Math]::Round([Math]::Max(0,[Math]::Min($script:app.Media.Height-$crop.H,$drag.Top+$dy)))
            Sync-Crop
        } else {
            # Project the pointer delta onto the output-ratio diagonal.
            $rw = $script:ratioW; $rh = $script:ratioH
            $deltaUnits = ($rw*$dx+$rh*$dy)/($rw*$rw+$rh*$rh)
            Set-CropSize ($drag.W/[double]$rw+$deltaUnits) $drag.Left $drag.Top
        }
    }
    $script:onUp = { $script:app.Drag = $null; $canvas.Capture = $false }
    $canvas.Add_Paint($onPaint); $canvas.Add_MouseDown($onDown); $canvas.Add_MouseMove($onMove); $canvas.Add_MouseUp($onUp)
    $canvas.Add_MouseCaptureChanged({ if (-not $canvas.Capture) { $script:app.Drag = $null } })
    $openButton.Add_Click({ Open-Media '' })
    $cancelButton.Add_Click({
        if ($script:app.Busy) {
            $script:app.Cancel = $true
            $cancelButton.Enabled = $false
            $status.Text = 'Cancelling...'
        } else { $form.Close() }
    })
    $script:onPreview = {
        if ($script:app.Busy -or -not $script:app.Media) { return }
        $time = [double]$seekBox.Value
        # The exclusive endpoint may not contain a decodable frame.
        $time = [Math]::Min($time,[Math]::Max(0.0,$script:app.Media.Duration-0.001))
        $script:app.Cancel = $false; Set-Busy $true 'Loading preview frame...'
        try { Show-Preview $time; $status.Text = 'Preview ready. Drag the crop or export.' }
        catch { Show-Failure $_ } finally { Set-Busy $false }
    }
    $previewButton.Add_Click($onPreview)
    $playButton.Add_Click({ Start-Playback })
    $stopButton.Add_Click({
        if ($script:app.Busy -or -not $script:app.Media) { return }
        Stop-Playback -Release; $seekBox.Value = $startBox.Value; & $onPreview
    })
    $croppedPreview.Add_CheckedChanged({ $script:app.Drag = $null; $canvas.Capture = $false; $canvas.Invalidate() })
    $seekBox.Add_KeyDown({ param($sender,$event) if ($event.KeyCode -eq 'Enter') { & $onPreview; $event.SuppressKeyPress = $true } })
    $script:onRangePaint = {
        param($sender,$event)
        if (-not $script:app.Media) { return }
        $geo = Get-RangeGeometry; $g = $event.Graphics; $duration = $script:app.Media.Duration
        $left = $geo.Left; $right = $left+$geo.Span
        $sx = $left+[double]$startBox.Value/$duration*$geo.Span
        $ex = $left+[double]$endBox.Value/$duration*$geo.Span
        $px = $left+[double]$seekBox.Value/$duration*$geo.Span
        $track = [Drawing.SolidBrush]::new([Drawing.Color]::FromArgb(212,220,230))
        $selected = [Drawing.SolidBrush]::new([Drawing.Color]::FromArgb(123,184,244))
        $startBrush = [Drawing.SolidBrush]::new([Drawing.Color]::FromArgb(38,110,210))
        $endBrush = [Drawing.SolidBrush]::new([Drawing.Color]::FromArgb(184,97,17))
        $playPen = [Drawing.Pen]::new([Drawing.Color]::FromArgb(30,35,45),2)
        $focusPen = [Drawing.Pen]::new([Drawing.Color]::Black,1)
        try {
            $g.FillRectangle($track,[single]$left,[single]($geo.Y-4),[single]$geo.Span,[single]8)
            $g.FillRectangle($selected,[single]$sx,[single]($geo.Y-4),[single][Math]::Max(0.0,$ex-$sx),[single]8)
            $g.DrawLine($playPen,[single]$px,[single]2,[single]$px,[single]36)
            $g.FillRectangle($startBrush,[single]($sx-7),[single]($geo.Y-13),[single]14,[single]26)
            $g.FillRectangle($endBrush,[single]($ex-7),[single]($geo.Y-13),[single]14,[single]26)
            $g.DrawString('S',$form.Font,[Drawing.Brushes]::White,[single]($sx-6),[single]($geo.Y-10))
            $g.DrawString('E',$form.Font,[Drawing.Brushes]::White,[single]($ex-6),[single]($geo.Y-10))
            if ($timeline.Focused) {
                $activeX = $sx; if ($script:app.ActiveEnd -eq 'End') { $activeX = $ex }
                $g.DrawRectangle($focusPen,[single]($activeX-9),[single]($geo.Y-15),[single]18,[single]30)
            }
        } finally { $track.Dispose(); $selected.Dispose(); $startBrush.Dispose(); $endBrush.Dispose(); $playPen.Dispose(); $focusPen.Dispose() }
    }
    $script:onRangeDown = {
        param($sender,$event)
        if ($script:app.Busy -or -not $script:app.Media -or $event.Button -ne 'Left') { return }
        Stop-Playback
        [void]$timeline.Focus()
        $geo = Get-RangeGeometry; $duration = $script:app.Media.Duration
        $sx = $geo.Left+[double]$startBox.Value/$duration*$geo.Span
        $ex = $geo.Left+[double]$endBox.Value/$duration*$geo.Span
        $ds = [Math]::Abs($event.X-$sx); $de = [Math]::Abs($event.X-$ex)
        $mode = 'Seek'
        if ([Math]::Min($ds,$de) -le 12) {
            $mode = 'Start'; if ($de -lt $ds -or ($de -eq $ds -and $event.X -gt ($sx+$ex)/2)) { $mode = 'End' }
            $script:app.ActiveEnd = $mode
        }
        $script:app.RangeDrag = $mode; $timeline.Capture = $true
        & $onRangeMove $sender $event
    }
    $script:onRangeMove = {
        param($sender,$event)
        if (-not $script:app.Media -or $script:app.Busy) { return }
        $geo = Get-RangeGeometry
        if (-not $script:app.RangeDrag) { $timeline.Cursor = [Windows.Forms.Cursors]::Hand; return }
        $time = [Math]::Max(0.0,[Math]::Min(1.0,($event.X-$geo.Left)/$geo.Span))*$script:app.Media.Duration
        if ($script:app.RangeDrag -eq 'Seek') { $seekBox.Value = [decimal]$time; $timeline.Invalidate() }
        else { Set-TrimHandle $script:app.RangeDrag $time }
    }
    $script:onRangeUp = {
        if (-not $script:app.RangeDrag) { return }
        $script:app.RangeDrag = $null; $timeline.Capture = $false
        & $onPreview
    }
    $timeline.Add_Paint($onRangePaint); $timeline.Add_MouseDown($onRangeDown); $timeline.Add_MouseMove($onRangeMove); $timeline.Add_MouseUp($onRangeUp)
    $timeline.Add_MouseCaptureChanged({ if (-not $timeline.Capture) { $script:app.RangeDrag = $null } })
    $timeline.Add_GotFocus({ $timeline.Invalidate() }); $timeline.Add_LostFocus({ $timeline.Invalidate() })
    $timeline.Add_PreviewKeyDown({ param($sender,$event) if ($event.KeyCode -in @('Left','Right','Home','End')) { $event.IsInputKey = $true } })
    $timeline.Add_KeyDown({
        param($sender,$event)
        if ($event.KeyCode -eq 'Space') {
            if ($script:app.ActiveEnd -eq 'Start') { $script:app.ActiveEnd = 'End' } else { $script:app.ActiveEnd = 'Start' }
            $timeline.Invalidate(); $event.SuppressKeyPress = $true; return
        }
        if ($event.KeyCode -notin @('Left','Right','Home','End')) { return }
        $time = [double]$startBox.Value; if ($script:app.ActiveEnd -eq 'End') { $time = [double]$endBox.Value }
        $step = 0.001; if ($event.Shift) { $step = 0.1 }
        switch ($event.KeyCode.ToString()) {
            'Left' { $time -= $step }; 'Right' { $time += $step }; 'Home' { $time = 0 }; 'End' { $time = $script:app.Media.Duration }
        }
        Set-TrimHandle $script:app.ActiveEnd $time
        $event.SuppressKeyPress = $true
    })
    $timeline.Add_KeyUp({ param($sender,$event) if ($event.KeyCode -in @('Left','Right','Home','End')) { & $onPreview } })
    $setStart.Add_Click({ $startBox.Value = [decimal]$script:app.Seek })
    $setEnd.Add_Click({ $endBox.Value = [decimal]$script:app.Seek })
    $startBox.Add_ValueChanged({ Sync-Trim })
    $endBox.Add_ValueChanged({ Sync-Trim })
    $seekBox.Add_ValueChanged({ if (-not $script:app.Updating) { Stop-Playback -Release; $timeline.Invalidate() } })
    $resetCrop.Add_Click({ $script:app.Crop = New-Crop $script:app.Media.Width $script:app.Media.Height; Sync-Crop })
    $cropWidth.Add_ValueChanged({ if (-not $script:app.Updating -and $script:app.Crop) { Set-CropSize ([double]$cropWidth.Value/$script:ratioW) } })
    $cropHeight.Add_ValueChanged({ if (-not $script:app.Updating -and $script:app.Crop) { Set-CropSize ([double]$cropHeight.Value/$script:ratioH) } })
    $script:onOutputSize = {
        param([double]$Units)
        if ($script:app.Updating) { return }
        $script:app.Updating = $true
        try {
            $script:app.Units = [int][Math]::Max(1,[Math]::Min(3855,[Math]::Round($Units)))
            $outWidth.Value = $script:ratioW*$script:app.Units; $outHeight.Value = $script:ratioH*$script:app.Units
            $outWidth.Increment = $script:ratioW; $outHeight.Increment = $script:ratioH
            $outputScaleLabel.Text = '{0}:{1} | {2:0.#}%' -f $script:ratioW,$script:ratioH,($script:app.Units/25.0*100)
        } finally { $script:app.Updating = $false }
        Update-Summary
    }
    $outWidth.Add_ValueChanged({ & $onOutputSize ([double]$outWidth.Value/$script:ratioW) })
    $outHeight.Add_ValueChanged({ & $onOutputSize ([double]$outHeight.Value/$script:ratioH) })
    $outputLayout.Add_SelectedIndexChanged({
        if ($script:app.Updating) { return }
        $script:ratioW = 17; $script:ratioH = 9
        if ($outputLayout.SelectedIndex -eq 1) { $script:ratioW = 9; $script:ratioH = 17 }
        & $onOutputSize $script:app.Units
        if ($script:app.Media) { $script:app.Crop = New-Crop $script:app.Media.Width $script:app.Media.Height; Sync-Crop }
    })
    $rotateLeft.Add_Click({ Rotate-Source -90 }); $rotateRight.Add_Click({ Rotate-Source 90 })
    & $onOutputSize $script:app.Units
    $smallerButton.Add_Click({ & $onOutputSize ([Math]::Min($script:app.Units-1,[Math]::Round($script:app.Units/1.25))) })
    $largerButton.Add_Click({ & $onOutputSize ([Math]::Max($script:app.Units+1,[Math]::Round($script:app.Units*1.25))) })
    $script:onExport = {
        if ($script:app.Busy -or -not $script:app.Media) { return }
        if ($endBox.Value -le $startBox.Value) { $status.Text = 'End must be after start.'; return }
        $dialog = New-Object Windows.Forms.SaveFileDialog
        $dialog.Filter = 'Animated GIF|*.gif'; $dialog.DefaultExt = 'gif'; $dialog.AddExtension = $true
        if ($ExportDirectory) {
            [void][IO.Directory]::CreateDirectory($ExportDirectory)
            $dialog.InitialDirectory = [IO.Path]::GetFullPath($ExportDirectory)
        } else { $dialog.InitialDirectory = [IO.Path]::GetDirectoryName($script:app.Media.Path) }
        $dialog.FileName = [IO.Path]::GetFileNameWithoutExtension($script:app.Media.Path) + '_70.gif'
        try { if ($dialog.ShowDialog($form) -ne 'OK') { return }; $output = $dialog.FileName }
        finally { $dialog.Dispose() }
        $script:app.Cancel = $false; Set-Busy $true 'Encoding and verifying 70 frames...'
        try {
            $result = Export-Gif $script:app.Media $script:app.Crop ([double]$startBox.Value) ([double]$endBox.Value) $script:app.Units $output
            $script:app.LastOutput = $result.Path
            Publish-Export $result.Path
            $status.Text = 'Saved: {0} | {1:0.#} KB | verified 70 frames' -f [IO.Path]::GetFileName($result.Path),($result.Bytes/1KB)
            $tooltip.SetToolTip($status,$result.Path)
        } catch { Show-Failure $_ } finally { Set-Busy $false }
    }
    $exportButton.Add_Click($onExport)
    $form.Add_FormClosing({
        param($sender,$event)
        Stop-Playback -Release
        if ($script:app.Busy) { $event.Cancel = $true; $script:app.Closing = $true; $script:app.Cancel = $true }
    })
    $form.Add_Resize({ $timeline.Width = [Math]::Max(280,$form.ClientSize.Width-540); $canvas.Invalidate(); $timeline.Invalidate() })
    # DoEvents can service a close request while FFmpeg runs; close after its stack unwinds.
    $script:closeTimer = New-Object Windows.Forms.Timer
    $closeTimer.Interval = 100
    $closeTimer.Add_Tick({ if ($script:app.Closing -and -not $script:app.Busy) { $closeTimer.Stop(); $form.Close() } })
    $closeTimer.Start()
    $script:playbackTimer = New-Object Windows.Forms.Timer
    $playbackTimer.Interval = 15; $playbackTimer.Add_Tick({ Update-Playback })
    Set-Busy $false
}

function Invoke-SelfCheck {
    $script:app.UI = $false
    $script:Portrait = $false; $script:ratioW = 17; $script:ratioH = 9
    $inputFile = Join-Path $script:work ("source with spaces & Unicode-$([char]0x6d4b)$([char]0x8bd5).mp4")
    [void](Invoke-Tool $script:ffmpeg @('-hide_banner','-loglevel','error','-nostdin','-f','lavfi','-i',
        'testsrc2=size=640x360:rate=24:duration=4','-c:v','libx264','-pix_fmt','yuv420p','-y',$inputFile))
    $media = Get-Media $inputFile
    $image = Get-Preview $media 0
    $media.Width = $image.Width; $media.Height = $image.Height; $image.Dispose()
    $crop = New-Crop $media.Width $media.Height
    $crop.X = 79; $crop.Y = 51; $crop.W = 340; $crop.H = 180
    $result = Export-Gif $media $crop 0.75 3.25 15 (Join-Path $script:work 'video output.gif')
    if ([Math]::Abs($result.Duration-2.5) -gt 0.03) { throw "Unexpected duration: $($result.Duration)" }
    Write-Output "PASS: trimmed video -> 255 x 135, 70 frames, $($result.Duration) seconds."
    $gifMedia = Get-Media $result.Path; $gifMedia.Width = 255; $gifMedia.Height = 135
    $gifCrop = New-Crop 255 135
    $gifResult = Export-Gif $gifMedia $gifCrop 0.3 1.8 10 (Join-Path $script:work 'gif output.gif')
    if ([Math]::Abs($gifResult.Duration-1.5) -gt 0.03) { throw 'GIF input duration did not survive conversion.' }
    Write-Output 'PASS: GIF input, nonzero trim -> 170 x 90, 70 frames, 1.5 seconds.'
    $short = Export-Gif $media $crop 1.01 1.11 5 (Join-Path $script:work 'short.gif')
    if ([Math]::Abs($short.Duration-1.4) -gt 0.03) { throw 'Short selection timing failed.' }
    Write-Output 'PASS: 0.1-second selection -> 85 x 45, 70 frames, 1.4-second playback.'
    $staticFile = Join-Path $script:work 'static.gif'
    [void](Invoke-Tool $script:ffmpeg @('-hide_banner','-loglevel','error','-f','lavfi','-i',
        'color=c=blue:size=340x180:rate=2:duration=2','-loop','0','-y',$staticFile))
    $staticMedia = Get-Media $staticFile; $staticMedia.Width = 340; $staticMedia.Height = 180
    [void](Export-Gif $staticMedia (New-Crop 340 180) 0 1.5 25 (Join-Path $script:work 'static output.gif'))
    Write-Output 'PASS: identical source frames -> exactly 70 encoded frames, 425 x 225.'
    # Independent content checks: known color changes and a border at the crop origin.
    $bands = Join-Path $script:work 'timing.mp4'
    $pattern = "color=c=red:size=640x360:rate=24:duration=4," +
        "drawbox=w=iw:h=ih:color=lime:t=fill:enable='gte(t,1)'," +
        "drawbox=w=iw:h=ih:color=blue:t=fill:enable='gte(t,2)'," +
        "drawbox=w=iw:h=ih:color=yellow:t=fill:enable='gte(t,3)'," +
        'drawbox=x=79:y=51:w=340:h=180:color=white:t=4'
    [void](Invoke-Tool $script:ffmpeg @('-hide_banner','-loglevel','error','-f','lavfi','-i',$pattern,
        '-c:v','libx264','-pix_fmt','yuv420p','-y',$bands))
    $bandMedia = Get-Media $bands; $bandMedia.Width = 640; $bandMedia.Height = 360
    $bandResult = Export-Gif $bandMedia $crop 0.75 3.25 15 (Join-Path $script:work 'timing output.gif')
    $bandGif = Get-Media $bandResult.Path
    foreach ($check in @(@(0,'Red'),@(0.5,'Lime'),@(1.5,'Blue'),@(2.4,'Yellow'))) {
        $frame = Get-Preview $bandGif $check[0]
        try {
            $actual = $frame.GetPixel(127,67); $expected = [Drawing.Color]::FromName($check[1])
            if ([Math]::Abs([int]$actual.R-$expected.R) -gt 35 -or
                [Math]::Abs([int]$actual.G-$expected.G) -gt 35 -or
                [Math]::Abs([int]$actual.B-$expected.B) -gt 35) { throw "Trim content mismatch at $($check[0]): $actual" }
            $edge = $frame.GetPixel(1,1)
            if ($edge.R -lt 180 -or $edge.G -lt 180 -or $edge.B -lt 180) { throw 'Crop origin did not match the selected border.' }
        } finally { $frame.Dispose() }
    }
    Write-Output 'PASS: decoded colors confirm trim timing, full interval coverage, and crop coordinates.'
    $heldResult = Export-Gif $staticMedia (New-Crop 340 180) 1.2 1.3 5 (Join-Path $script:work 'held frame.gif')
    $heldMedia = Get-Media $heldResult.Path
    $heldFrame = Get-Preview $heldMedia 1.3
    try { if ($heldFrame.GetPixel(30,20).B -lt 220) { throw 'Lost the GIF frame held across the selection.' } }
    finally { $heldFrame.Dispose() }
    Write-Output 'PASS: GIF selection between sparse source frames retains the displayed image.'
    # A second successful save replaces the destination, while cancellation preserves it.
    [void](Export-Gif $media $crop 0 2 10 $result.Path)
    $beforeCancel = [Convert]::ToBase64String([IO.File]::ReadAllBytes($result.Path))
    $script:app.Cancel = $true
    $cancelled = $false
    try { [void](Export-Gif $media $crop 0 2 5 $result.Path) }
    catch [OperationCanceledException] { $cancelled = $true }
    finally { $script:app.Cancel = $false }
    if (-not $cancelled -or [Convert]::ToBase64String([IO.File]::ReadAllBytes($result.Path)) -ne $beforeCancel) {
        throw 'Cancellation did not preserve the existing output.'
    }
    # Restore the documented sample dimensions after the replacement check.
    $result = Export-Gif $media $crop 0.75 3.25 15 $result.Path
    Write-Output 'PASS: overwrite succeeds; cancellation preserves the previous file.'
    foreach ($variant in @('rotated','anamorphic')) {
        $variantPath = Join-Path $script:work ($variant + '.mp4')
        $inputOptions = @()
        if ($variant -eq 'rotated') {
            $inputOptions = @('-display_rotation','90')
            $variantArgs = @('-c:v','copy')
            $expectedWidth = 360; $expectedHeight = 640
        } else {
            $variantArgs = @('-vf','setsar=2','-c:v','libx264')
            $expectedWidth = 1280; $expectedHeight = 360
        }
        [void](Invoke-Tool $script:ffmpeg (@('-hide_banner','-loglevel','error') + $inputOptions + @('-i',$inputFile) + $variantArgs + @('-y',$variantPath)))
        $variantMedia = Get-Media $variantPath
        $variantFrame = Get-Preview $variantMedia 0.5
        try {
            if ($variantFrame.Width -ne $expectedWidth -or $variantFrame.Height -ne $expectedHeight) { throw "$variant preview normalization failed." }
            $variantMedia.Width = $variantFrame.Width; $variantMedia.Height = $variantFrame.Height
        } finally { $variantFrame.Dispose() }
        [void](Export-Gif $variantMedia (New-Crop $expectedWidth $expectedHeight) 0.5 2 10 (Join-Path $script:work ($variant + '.gif')))
    }
    Write-Output 'PASS: rotated video and non-square source pixels share preview/export crop coordinates.'
    # Exercise the same crop interaction handlers used by the live window.
    Start-UI
    if ($script:playback.Frames -or $script:playback.Playing -or $playbackTimer.Enabled) { throw 'Cropper startup prepared playback before Play was requested.' }
    $form.ShowInTaskbar = $false; $form.Opacity = 0
    $form.Show(); [Windows.Forms.Application]::DoEvents()
    Open-Media $inputFile
    $geo = Get-CanvasGeometry
    $cropBefore = $script:app.Crop.W
    $right = $geo.X+($script:app.Crop.X+$script:app.Crop.W)*$geo.Scale
    $bottom = $geo.Y+($script:app.Crop.Y+$script:app.Crop.H)*$geo.Scale
    $mouse = { param($x,$y) [Windows.Forms.MouseEventArgs]::new([Windows.Forms.MouseButtons]::Left,1,[int]$x,[int]$y,0) }
    & $onDown $canvas (& $mouse $right $bottom)
    & $onMove $canvas (& $mouse ($right-100) ($bottom-50))
    & $onUp
    if ($script:app.Crop.W -ge $cropBefore -or [Math]::Abs($script:app.Crop.W*9.0/17-$script:app.Crop.H) -gt 0.51) { throw 'Crop resize handler failed.' }
    $leftBefore = $script:app.Crop.X
    $insideX = $geo.X+($script:app.Crop.X+20)*$geo.Scale; $insideY = $geo.Y+($script:app.Crop.Y+20)*$geo.Scale
    & $onDown $canvas (& $mouse $insideX $insideY)
    & $onMove $canvas (& $mouse ($insideX+30) $insideY)
    & $onUp
    if ($script:app.Crop.X -le $leftBefore) { throw 'Crop move handler failed.' }
    & $onOutputSize 10
    if ($outWidth.Value -ne 170 -or $outHeight.Value -ne 90) { throw 'Output dimension linking failed.' }
    $largerButton.PerformClick()
    if ($outWidth.Value -le 170 -or $outWidth.Value*9 -ne $outHeight.Value*17) { throw 'Output enlargement failed.' }
    $largerWidth = $outWidth.Value
    $smallerButton.PerformClick()
    if ($outWidth.Value -ge $largerWidth -or $outWidth.Value*9 -ne $outHeight.Value*17) { throw 'Output reduction failed.' }
    # The two actual timeline handles update independent endpoints and cannot cross.
    $rangeGeo = Get-RangeGeometry
    $sy = $rangeGeo.Y
    $sx = $rangeGeo.Left
    & $onRangeDown $timeline (& $mouse $sx $sy)
    & $onRangeMove $timeline (& $mouse ($rangeGeo.Left+$rangeGeo.Span*0.2) $sy)
    & $onRangeUp
    if ([Math]::Abs([double]$startBox.Value-0.8) -gt 0.02 -or $endBox.Value -ne 4) { throw "Start trim handle failed: start=$($startBox.Value), end=$($endBox.Value), span=$($rangeGeo.Span)." }
    & $onRangeDown $timeline (& $mouse ($rangeGeo.Left+$rangeGeo.Span) $sy)
    & $onRangeMove $timeline (& $mouse ($rangeGeo.Left+$rangeGeo.Span*0.8) $sy)
    & $onRangeUp
    if ([Math]::Abs([double]$endBox.Value-3.2) -gt 0.02) { throw 'End trim handle failed.' }
    Set-TrimHandle 'Start' 99
    if ($startBox.Value -ge $endBox.Value) { throw 'Trim handles crossed.' }
    Set-TrimHandle 'Start' 0.8
    # A track click changes the preview without modifying the trim range.
    & $onRangeDown $timeline (& $mouse ($rangeGeo.Left+$rangeGeo.Span*0.5) $sy)
    & $onRangeUp
    if ([Math]::Abs($script:app.Seek-2) -gt 0.02 -or [Math]::Abs([double]$startBox.Value-0.8) -gt 0.02) { throw 'Timeline seeking changed the trim range.' }
    $setStart.PerformClick()
    if ($startBox.Value -ne [decimal]$script:app.Seek) { throw 'Start here lost precision.' }
    $seekBox.Value = 2.75; & $onPreview
    if ([Math]::Abs($script:app.Seek-2.75) -gt 0.00001) { throw 'Preview rounded fractional seconds.' }
    $setEnd.PerformClick()
    if ($endBox.Value -ne [decimal]$script:app.Seek) { throw 'End here lost precision.' }
    $startBox.Value = 0.8
    Write-Output 'PASS: two timeline handles, crossing limits, track seeking, precise Start/End here, output scaling both ways.'
    # Reproduce the reported stuck-width case through the real resize function.
    $realMedia = $script:app.Media; $realCrop = $script:app.Crop
    try {
        $script:app.Media = @{ Width = 416; Height = 234 }
        $script:app.Crop = @{ X = 9; Y = 10; W = 391; H = 207 }
        Set-CropSize 24
        if ($script:app.Crop.W -ne 408) { throw 'Crop still stops at the old top-left boundary.' }
        Set-CropSize (416.0/17)
        if ($script:app.Crop.W -ne 416 -or $script:app.Crop.X -ne 0) { throw 'Crop still leaves horizontal edge pixels unused.' }
        Set-CropSize (415.0/17)
        if ($script:app.Crop.W -ne 415) { throw 'Crop still snaps in 17-pixel steps.' }
    } finally { $script:app.Media = $realMedia; $script:app.Crop = $realCrop; Sync-Crop }
    Write-Output 'PASS: crop can expand at an edge, reach full available width, and resize one pixel at a time.'
    $outWidth.Value = 731
    if ($script:app.Units -ne 43 -or $outHeight.Value -ne 387) { throw 'Large output dimensions did not link correctly.' }
    [void](Export-Gif $media (New-Crop $media.Width $media.Height) 0.8 2.75 $script:app.Units (Join-Path $script:work 'upscaled.gif'))
    & $onOutputSize 10
    Write-Output 'PASS: output upscales to 731 x 387 with exactly 70 frames.'
    $bitmap = New-Object Drawing.Bitmap $canvas.Width,$canvas.Height
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    try { & $onPaint $canvas ([Windows.Forms.PaintEventArgs]::new($graphics,$canvas.ClientRectangle)) }
    finally { $graphics.Dispose(); $bitmap.Dispose() }
    Write-Output 'PASS: live UI crop move/resize, linked output size, and preview paint handlers.'
    # Use asymmetric corners to catch reversed rotations and preview/export disagreement.
    $cornersPath = Join-Path $script:work 'orientation.mp4'
    $cornersFilter = 'color=c=red:size=340x180:rate=12:duration=2,' +
        'drawbox=x=170:y=0:w=170:h=90:color=lime:t=fill,' +
        'drawbox=x=0:y=90:w=170:h=90:color=blue:t=fill,' +
        'drawbox=x=170:y=90:w=170:h=90:color=yellow:t=fill'
    [void](Invoke-Tool $script:ffmpeg @('-hide_banner','-loglevel','error','-f','lavfi','-i',$cornersFilter,'-c:v','libx264','-pix_fmt','yuv420p','-y',$cornersPath))
    Open-Media $cornersPath
    $startBox.Value = 0.2; $endBox.Value = 1.7
    foreach ($turn in @(0,90,180,270)) {
        if ($turn -gt 0) { $rotateRight.PerformClick() }
        $outputLayout.SelectedIndex = [int]($turn -in @(90,270))
        & $onOutputSize 10
        $expectedW = 170; $expectedH = 90
        $expectedColors = @('Red','Lime','Blue','Yellow')
        switch ($turn) {
            90 { $expectedW = 90; $expectedH = 170; $expectedColors = @('Blue','Red','Yellow','Lime') }
            180 { $expectedColors = @('Yellow','Blue','Lime','Red') }
            270 { $expectedW = 90; $expectedH = 170; $expectedColors = @('Lime','Yellow','Red','Blue') }
        }
        if ($outWidth.Value -ne $expectedW -or $outHeight.Value -ne $expectedH) { throw 'Portrait output dimensions did not link.' }
        if ($startBox.Value -ne 0.2 -or $endBox.Value -ne 1.7) { throw 'Rotation/layout changed the trim range.' }
        Set-CropSize (10000)
        $crop = $script:app.Crop; $current = $script:app.Media
        if ($crop.X -lt 0 -or $crop.Y -lt 0 -or $crop.X+$crop.W -gt $current.Width -or $crop.Y+$crop.H -gt $current.Height) { throw 'Rotated crop escaped source bounds.' }
        $orientationResult = Export-Gif $current $crop 0.2 1.7 10 (Join-Path $script:work ("turn-$turn.gif"))
        Show-Preview 0.5
        $decoded = Get-Preview (Get-Media $orientationResult.Path) 0.5
        try {
            if ($decoded.Width -ne $expectedW -or $decoded.Height -ne $expectedH) { throw 'Rotated GIF dimensions are wrong.' }
            $points = @(@(0.25,0.25),@(0.75,0.25),@(0.25,0.75),@(0.75,0.75))
            for ($corner = 0; $corner -lt 4; $corner++) {
                $expected = [Drawing.Color]::FromName($expectedColors[$corner])
                foreach ($frame in @($decoded,$script:app.Image)) {
                    $actual = $frame.GetPixel([int]($frame.Width*$points[$corner][0]),[int]($frame.Height*$points[$corner][1]))
                    if ([Math]::Abs([int]$actual.R-$expected.R) -gt 35 -or [Math]::Abs([int]$actual.G-$expected.G) -gt 35 -or [Math]::Abs([int]$actual.B-$expected.B) -gt 35) { throw "Rotation $turn disagrees at corner $corner : $actual" }
                }
            }
        } finally { $decoded.Dispose() }
    }
    $smallerButton.PerformClick(); $largerButton.PerformClick()
    if ($outWidth.Value*17 -ne $outHeight.Value*9) { throw 'Portrait resizing lost its ratio.' }
    $tinyCrop = New-Crop 1 1
    if ($tinyCrop.W -ne 1 -or $tinyCrop.H -ne 1) { throw 'Tiny portrait crop escaped source bounds.' }
    $rotateLeft.PerformClick()
    if ($script:app.Media.Rotation -ne 180 -or $script:app.Media.Width -ne 340) { throw 'Rotate left did not reverse the previous right rotation.' }
    $outputLayout.SelectedIndex = 0
    Write-Output 'PASS: four source rotations agree between preview and 70-frame export; portrait crop bounds, linked scaling and reverse rotation work.'
    # Exercise the real playback buttons and timer handler with known source colors.
    Open-Media $bands
    $script:app.Crop = [pscustomobject]@{ X = 79; Y = 51; W = 340; H = 180 }; Sync-Crop
    $startBox.Value = 0.75; $endBox.Value = 3.25
    $playButton.PerformClick()
    if (-not $script:playback.Playing -or $script:playback.Frames.Count -ne 70 -or $playButton.Text -ne 'Pause') { throw 'Play did not prepare/start 70-frame playback.' }
    $watch = [Diagnostics.Stopwatch]::StartNew()
    while ($watch.ElapsedMilliseconds -lt 350) { [Windows.Forms.Application]::DoEvents(); [Threading.Thread]::Sleep(10) }
    if ($script:playback.Index -lt 3 -or $script:app.Seek -le 0.75 -or $script:app.Seek -ge 3.25) { throw 'Real playback timer did not advance the frame/playhead within the trim range.' }
    $cached = $script:playback.Frames
    $playButton.PerformClick()
    if ($script:playback.Playing -or $playbackTimer.Enabled -or $playButton.Text -ne 'Play') { throw 'Pause did not stop the playback clock/timer.' }
    $pausedIndex = $script:playback.Index
    $watch.Restart()
    while ($watch.ElapsedMilliseconds -lt 100) { [Windows.Forms.Application]::DoEvents(); [Threading.Thread]::Sleep(10) }
    if ($script:playback.Index -ne $pausedIndex) { throw 'Paused preview continued advancing.' }
    $playButton.PerformClick()
    if (-not [object]::ReferenceEquals($cached,$script:playback.Frames)) { throw 'Resume unnecessarily decoded the source again.' }
    $playbackTimer.Stop(); $script:playback.Clock.Stop()
    $croppedPreview.Checked = $true
    foreach ($check in @(@(0,'Red'),@(500,'Lime'),@(1500,'Blue'),@(2400,'Yellow'),@(2500,'Red'))) {
        $script:playback.Position = $check[0]; $script:playback.Clock.Reset(); Update-Playback
        $frame = [Drawing.Bitmap]::new($canvas.Width,$canvas.Height)
        $g = [Drawing.Graphics]::FromImage($frame)
        try {
            & $onPaint $canvas ([Windows.Forms.PaintEventArgs]::new($g,$canvas.ClientRectangle))
            $actual = $frame.GetPixel([int]($frame.Width/2),[int]($frame.Height/2)); $expected = [Drawing.Color]::FromName($check[1])
            if ([Math]::Abs([int]$actual.R-$expected.R) -gt 35 -or [Math]::Abs([int]$actual.G-$expected.G) -gt 35 -or [Math]::Abs([int]$actual.B-$expected.B) -gt 35) { throw "Playback trim/timing/loop mismatch at $($check[0]) ms: $actual" }
        } finally { $g.Dispose(); $frame.Dispose() }
    }
    $cropWidth.Value = 323
    if (-not $script:playback.Playing -or -not [object]::ReferenceEquals($cached,$script:playback.Frames) -or $script:app.Crop.W -ne 323) { throw 'Crop editing interrupted playback or ignored the edit.' }
    if ($startBox.Value -ne 0.75 -or $endBox.Value -ne 3.25) { throw 'Playback changed the trim range.' }
    $stopButton.PerformClick()
    if ($script:playback.Playing -or $script:playback.Frames -or $playbackTimer.Enabled -or [Math]::Abs($script:app.Seek-0.75) -gt 0.001) { throw 'Stop did not release preview frames and return to trim start.' }
    $playButton.PerformClick(); $startBox.Value = 1
    if ($script:playback.Frames -or $script:playback.Playing) { throw 'Trim change did not invalidate cached playback.' }
    Open-Media $staticFile
    $startBox.Value = 1.2; $endBox.Value = 1.3; $playButton.PerformClick()
    $heldSource = Get-Preview $script:app.Media 1.25
    try { $heldColor = $heldSource.GetPixel(20,20) } finally { $heldSource.Dispose() }
    if ($script:playback.Frames.Count -ne 70 -or $script:playback.Duration -ne 1400 -or $script:playback.Frames[35].GetPixel(20,20).ToArgb() -ne $heldColor.ToArgb()) {
        throw "Short/sparse GIF playback failed: frames=$($script:playback.Frames.Count), duration=$($script:playback.Duration), pixel=$($script:playback.Frames[35].GetPixel(20,20)), status=$($status.Text)."
    }
    Open-Media $cornersPath
    if ($script:playback.Frames -or $script:playback.Playing) { throw 'Opening another file retained old playback.' }
    $outputLayout.SelectedIndex = 1; $rotateRight.PerformClick(); $playButton.PerformClick()
    if ($script:playback.Frames[0].Width -ge $script:playback.Frames[0].Height -or $script:playback.Frames[0].GetPixel(40,80).B -lt 220) { throw 'Portrait playback did not apply source rotation.' }
    if ($CheckDirectory) {
        [void][IO.Directory]::CreateDirectory([IO.Path]::GetFullPath($CheckDirectory))
        $croppedPreview.Checked = $true; $form.PerformLayout()
        $capture = [Drawing.Bitmap]::new($form.Width,$form.Height)
        try { $form.DrawToBitmap($capture,[Drawing.Rectangle]::new(0,0,$form.Width,$form.Height)); $capture.Save((Join-Path $CheckDirectory 'Gif70-playback-ui.png')) }
        finally { $capture.Dispose() }
    }
    Stop-Playback -Release
    $croppedPreview.Checked = $false; $outputLayout.SelectedIndex = 0
    Write-Output 'PASS: Play/Pause/resume/Stop, crop-only painting, trim timing and looping, crop edits during playback, sparse GIFs, portrait rotation and cache cleanup.'
    if ($CheckDirectory) {
        $artifactDir = [IO.Path]::GetFullPath($CheckDirectory)
        [void][IO.Directory]::CreateDirectory($artifactDir)
        $form.PerformLayout()
        $layoutSize = $form.Size
        $screenshot = New-Object Drawing.Bitmap $layoutSize.Width,$layoutSize.Height
        try {
            $form.DrawToBitmap($screenshot, [Drawing.Rectangle]::new(0,0,$layoutSize.Width,$layoutSize.Height))
            $screenshot.Save((Join-Path $artifactDir 'Gif70-ui.png'),[Drawing.Imaging.ImageFormat]::Png)
        } finally { $screenshot.Dispose() }
        [IO.File]::Copy($result.Path,(Join-Path $artifactDir 'sample-255x135-70frames.gif'),$true)
        Write-Output "Check artifacts: $artifactDir"
    }
    $savedResultDirectory = $script:ResultDirectory
    try {
        $script:ResultDirectory = Join-Path $script:work 'hub export receipts'
        Publish-Export $result.Path
        $receipts = @(Get-ChildItem -LiteralPath $script:ResultDirectory -Filter '*.result')
        if ($receipts.Count -ne 1 -or [IO.File]::ReadAllText($receipts[0].FullName) -ne $result.Path) { throw 'Hub export receipt did not preserve the completed output path.' }
        if (@(Get-ChildItem -LiteralPath $script:ResultDirectory -Filter '*.tmp').Count) { throw 'Incomplete export receipt was left behind.' }
        Write-Output 'PASS: successful exports publish an atomic UTF-8 receipt for automatic hub import.'
    } finally {
        foreach ($receipt in [IO.Directory]::GetFiles($script:ResultDirectory)) { [IO.File]::Delete($receipt) }
        [IO.Directory]::Delete($script:ResultDirectory)
        $script:ResultDirectory = $savedResultDirectory
    }
    $playButton.PerformClick(); $form.Close()
    if ($script:playback.Frames -or $script:playback.Playing -or $playbackTimer.Enabled) { throw 'Closing the cropper retained playback frames or a running timer.' }
    $script:app.UI = $false
}

try {
    $script:ffmpeg = Find-Tool 'ffmpeg'; $script:ffprobe = Find-Tool 'ffprobe'
    if ($SelfCheck) { Invoke-SelfCheck; Write-Output 'All Gif70 self-checks passed.' }
    else {
        Start-UI
        $form.Add_Shown({ Open-Media $Path })
        [void]$form.ShowDialog()
    }
} catch {
    if ($SelfCheck) { throw }
    [void][Windows.Forms.MessageBox]::Show($_.Exception.Message, 'Gif70', 'OK', 'Error')
} finally {
    Stop-Playback -Release
    if (Get-Variable playbackTimer -Scope Script -ErrorAction SilentlyContinue) { $playbackTimer.Dispose() }
    if ($script:app.Image) { $script:app.Image.Dispose() }
    if (Get-Variable closeTimer -Scope Script -ErrorAction SilentlyContinue) { $closeTimer.Dispose() }
    if (Get-Variable tooltip -Scope Script -ErrorAction SilentlyContinue) { $tooltip.Dispose() }
    if (Get-Variable form -Scope Script -ErrorAction SilentlyContinue) { $form.Dispose() }
    # Delete only files in the unique directory created by this run.
    foreach ($file in [IO.Directory]::GetFiles($script:work)) { [IO.File]::Delete($file) }
    [IO.Directory]::Delete($script:work)
}
