<#
.SYNOPSIS
Records one of the website's looping videos on Windows, the counterpart of eng/showcase/record.sh.

.DESCRIPTION
Starts Codalus with its own user data, puts its window at 0,0 sized 1280x800, plays the story (eng/showcase/<name>.ps1)
with the pointer and the keyboard while ffmpeg records that region of the screen, then cuts the recording between the
story's start and end marks, joins its end to its start with a short crossfade so it loops, and encodes it for the web
(H.264, no sound, a few hundred KB).

The fixture and the user data are made in C:\codalus-showcase\<name>, never under the user's profile, so no path in
the picture names the login; the fixture is trusted in the user data, and the folder is removed afterwards unless -Keep.
Nothing else may be inside the recorded region (the top left 1280x800 of the primary screen) meanwhile, and the
pointer and the keyboard are the story's: leave them alone until it ends.

A story is a PowerShell file that defines:
  Prepare   makes the fixture in "$Work\src" and sets $script:Open (codalus's arguments) and, optionally,
            $script:Settings (extra settings.json lines) and $script:Load (seconds the solution takes to load, default 30)
  Setup     optional: gets the window ready before the recording starts (a tool window open, a first build)
  Teardown  optional: ends what Prepare or Setup started
  Story     plays it: Mark start ... Mark end around what the video shows, with the helpers in showcase.ps1
It runs in this script's scope, so it can read $Root and $Work.

.EXAMPLE
pwsh eng/showcase/record.ps1 wpf
#>
[CmdletBinding()]
param(
    # A story: eng/showcase/<name>.ps1.
    [Parameter(Mandatory)] [string] $Name,
    # The shell to record (default: the Release build).
    [string] $Codalus,
    # Where the video goes (default: website/wwwroot/assets/screenshots).
    [string] $Out,
    # Keeps the work folder (the raw recording, the app's log and the story's marks).
    [switch] $Keep,
    # Starts the shell and runs Setup, then leaves it running without recording: for writing a story with Shot.
    [switch] $Explore,
    # Only cuts and encodes again what a run with -Keep recorded.
    [switch] $Recut
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3

$Root = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
if (-not $Codalus) { $Codalus = Join-Path $Root 'src/Codalus.Shell/bin/Release/net11.0/codalus.exe' }
if (-not $Out) { $Out = Join-Path $Root 'website/wwwroot/assets/screenshots' }
$storyFile = Join-Path $PSScriptRoot "$Name.ps1"
if (-not (Test-Path $storyFile)) { throw "no story $storyFile" }
if (-not (Test-Path $Codalus)) { throw "no shell at $Codalus (dotnet build src/Codalus.Shell -c Release)" }
if (-not (Get-Command ffmpeg -ErrorAction SilentlyContinue)) { throw "ffmpeg isn't installed (winget install Gyan.FFmpeg)" }

$Work = "C:\codalus-showcase\$Name"
if (-not $Recut) {
    if (Test-Path $Work) { Remove-Item -Recurse -Force $Work }
    New-Item -ItemType Directory -Force "$Work\src", "$Work\ud\config", "$Work\ud\state" | Out-Null

    . (Join-Path $PSScriptRoot 'showcase.ps1')
    . $storyFile

    $script:Open = @()
    $script:Settings = ''
    $script:Load = 30
    Prepare

    $utf8 = New-Object System.Text.UTF8Encoding($false)
    $extra = if ($script:Settings) { ",`n" + $script:Settings } else { '' }
    [IO.File]::WriteAllText("$Work\ud\config\settings.json", @"
{
  "workbench.colorTheme": "dark",
  "workbench.setupCompleted": true,
  "editor.autoClosingBrackets": false,
  "welcome.showOnStartup": false$extra
}
"@, $utf8)
    $trusted = @(Get-ChildItem -Directory "$Work\src" | ForEach-Object { $_.FullName })
    [IO.File]::WriteAllText("$Work\ud\state\trusted-folders.json", (@{ folders = $trusted } | ConvertTo-Json -Compress), $utf8)

    $app = $null
    $recorder = $null
    try {
        $env:CODALUS_CREDENTIAL_STORE = 'memory'
        # Run from a Claude Code session, the recorder would pass on its markers to a CLI that AI Console starts.
        Get-ChildItem env: | Where-Object Name -Match '^(CLAUDECODE|CLAUDE_CODE_)' | ForEach-Object { Remove-Item "env:$($_.Name)" }
        $arguments = @('--user-data-dir', "$Work\ud", '--skip-setup') + $script:Open | ForEach-Object { '"' + $_ + '"' }
        New-Item -ItemType File "$Work\stdin.txt" | Out-Null   # nothing to read: the shell holds no handle of this console
        $app = Start-Process $Codalus -ArgumentList $arguments -WorkingDirectory "$Work\src" -PassThru `
            -RedirectStandardInput "$Work\stdin.txt" -RedirectStandardOutput "$Work\app.log" -RedirectStandardError "$Work\app.err.log"

        $deadline = (Get-Date).AddSeconds(60)
        while ($true) {
            $app.Refresh()
            if ($app.MainWindowHandle -ne [IntPtr]::Zero) { break }
            if ($app.HasExited -or (Get-Date) -gt $deadline) { throw "the shell didn't open its window (see $Work\app.log)" }
            Start-Sleep -Milliseconds 300
        }
        Window $app
        Wait $script:Load
        Window $app
        Click 1060 16   # the title bar's empty part: the window in front, with the keyboard
        Rest
        Wait 1
        if (Test-Path Function:\Setup) { Setup; Rest; Wait 1 }

        if ($Explore) {
            $app.Id | Set-Content "$Work\app.pid"
            Write-Output "exploring: the shell runs (pid $($app.Id)); dot-source eng/showcase/showcase.ps1 with `$Work = '$Work' to drive it"
            $app = $null
            return
        }

        $grab = New-Object System.Diagnostics.ProcessStartInfo 'ffmpeg'
        foreach ($argument in '-y', '-loglevel', 'error', '-f', 'gdigrab', '-framerate', '30', '-offset_x', '0', '-offset_y', '0',
            '-video_size', '1280x800', '-draw_mouse', '1', '-i', 'desktop', '-c:v', 'libx264', '-preset', 'ultrafast', '-qp', '0', "$Work\raw.mkv") {
            $grab.ArgumentList.Add($argument)
        }
        $grab.UseShellExecute = $false
        $grab.RedirectStandardInput = $true
        $recorder = [System.Diagnostics.Process]::Start($grab)
        Wait 1
        Mark recording
        Story
        Wait 0.5
        $recorder.StandardInput.Write('q')
        $recorder.StandardInput.Flush()
        if (-not $recorder.WaitForExit(30000)) { $recorder.Kill() }
        $recorder = $null
    }
    finally {
        if (Test-Path Function:\Teardown) { try { Teardown } catch { Write-Warning "teardown: $_" } }
        if ($recorder -and -not $recorder.HasExited) { $recorder.Kill() }
        if ($app -and -not $app.HasExited) {
            & taskkill /T /F /PID $app.Id 2>&1 | Out-Null
            $app.WaitForExit(10000) | Out-Null
        }
    }
}
elseif (-not (Test-Path "$Work\raw.mkv")) { throw "no recording in $Work (record with -Keep first)" }
if ($Explore) { return }

# --- The cut ----------------------------------------------------------------------------------------------------------

$marks = @{}
foreach ($line in Get-Content "$Work\marks.txt") { $at, $label = $line.Split(' ', 2); $marks[$label] = [double]::Parse($at, [Globalization.CultureInfo]::InvariantCulture) }
$inv = [Globalization.CultureInfo]::InvariantCulture
$startAt = ($marks['start'] - $marks['recording']).ToString('F3', $inv)
$length = $marks['end'] - $marks['start']
$fade = 0.8
$offset = ($length - 2 * $fade).ToString('F3', $inv)
$lengthText = $length.ToString('F3', $inv)

# The loop: the video starts $fade into the story and ends crossfading into the story's first $fade, which leads
# straight into the video's first frame. (Windows composites its popups, so there are no black flash frames to drop.)
$filter = "[0:v]fps=30,trim=start=${startAt}:duration=$lengthText,setpts=PTS-STARTPTS,split[a][b];" +
    "[a]trim=start=$fade,setpts=PTS-STARTPTS[body];" +
    "[b]trim=duration=$fade,setpts=PTS-STARTPTS[head];" +
    "[body][head]xfade=transition=fade:duration=${fade}:offset=$offset,format=yuv420p[v]"
New-Item -ItemType Directory -Force $Out | Out-Null
$video = Join-Path $Out "$Name.mp4"
& ffmpeg -y -loglevel error -i "$Work\raw.mkv" -filter_complex $filter -map '[v]' -an -c:v libx264 -preset veryslow -crf 30 -tune animation -movflags +faststart $video
if ($LASTEXITCODE -ne 0) { throw "ffmpeg couldn't encode $video" }
Write-Output ('{0}: {1} KB, {2} s' -f $video, [math]::Round((Get-Item $video).Length / 1KB), ($length - $fade).ToString('F1', $inv))

if ($Keep) { Write-Output "kept $Work" } else { Remove-Item -Recurse -Force $Work }
