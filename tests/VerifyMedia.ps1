$ErrorActionPreference = "Stop"

Add-Type -AssemblyName PresentationCore
Add-Type -AssemblyName WindowsBase

$failed = $false
$results = foreach ($inputPath in $args) {
    $script:mediaOpened = $false
    $script:mediaFailure = $null
    $player = New-Object System.Windows.Media.MediaPlayer
    $frame = New-Object System.Windows.Threading.DispatcherFrame
    $timer = New-Object System.Windows.Threading.DispatcherTimer
    $timer.Interval = [TimeSpan]::FromSeconds(5)
    $timer.add_Tick({
        $timer.Stop()
        $frame.Continue = $false
    })
    $player.add_MediaOpened({
        $script:mediaOpened = $true
        $frame.Continue = $false
    })
    $player.add_MediaFailed({
        param($sender, $eventArgs)
        $script:mediaFailure = $eventArgs.ErrorException.Message
        $frame.Continue = $false
    })

    $filePath = (Resolve-Path -LiteralPath $inputPath).Path
    $fileLock = New-Object System.IO.FileStream(
        $filePath,
        [System.IO.FileMode]::Open,
        [System.IO.FileAccess]::Read,
        [System.IO.FileShare]::Read)
    try {
        $player.Open([Uri]$filePath)
        $timer.Start()
        [System.Windows.Threading.Dispatcher]::PushFrame($frame)
        $player.Close()
    }
    finally {
        $fileLock.Dispose()
    }

    if (-not $script:mediaOpened) {
        $failed = $true
    }

    [PSCustomObject]@{
        File = Split-Path -Leaf $filePath
        MediaOpened = $script:mediaOpened
        MediaFailure = $script:mediaFailure
        TimedOut = -not $script:mediaOpened -and $null -eq $script:mediaFailure
    }
}

$results | Format-Table -AutoSize

if ($failed) {
    exit 1
}
