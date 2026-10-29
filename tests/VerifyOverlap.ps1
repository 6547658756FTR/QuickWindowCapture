$ErrorActionPreference = "Stop"

Add-Type -AssemblyName PresentationCore
Add-Type -AssemblyName WindowsBase

$filePath = (Resolve-Path -LiteralPath $args[0]).Path
$script:opened = 0
$script:failed = 0
$openFrame = New-Object System.Windows.Threading.DispatcherFrame
$openTimer = New-Object System.Windows.Threading.DispatcherTimer
$openTimer.Interval = [TimeSpan]::FromSeconds(5)
$openTimer.add_Tick({
    $openTimer.Stop()
    $openFrame.Continue = $false
})

$players = @()
for ($index = 0; $index -lt 3; $index++) {
    $player = New-Object System.Windows.Media.MediaPlayer
    $player.Volume = 0
    $player.add_MediaOpened({
        $script:opened++
        if ($script:opened -eq 3) {
            $openFrame.Continue = $false
        }
    })
    $player.add_MediaFailed({
        $script:failed++
        $openFrame.Continue = $false
    })
    $players += $player
}

foreach ($player in $players) {
    $player.Open([Uri]$filePath)
}
$openTimer.Start()
[System.Windows.Threading.Dispatcher]::PushFrame($openFrame)

if ($script:opened -eq 3 -and $script:failed -eq 0) {
    foreach ($player in $players) {
        $player.Play()
    }

    $playFrame = New-Object System.Windows.Threading.DispatcherFrame
    $playTimer = New-Object System.Windows.Threading.DispatcherTimer
    $playTimer.Interval = [TimeSpan]::FromMilliseconds(120)
    $playTimer.add_Tick({
        $playTimer.Stop()
        $playFrame.Continue = $false
    })
    $playTimer.Start()
    [System.Windows.Threading.Dispatcher]::PushFrame($playFrame)
}

$positions = @($players | ForEach-Object { $_.Position.TotalMilliseconds })
foreach ($player in $players) {
    $player.Stop()
    $player.Close()
}

[PSCustomObject]@{
    OpenedPlayers = $script:opened
    FailedPlayers = $script:failed
    MinimumPositionMs = if ($positions.Count) { ($positions | Measure-Object -Minimum).Minimum } else { 0 }
} | Format-List

if ($script:opened -ne 3 -or $script:failed -ne 0) {
    exit 1
}
