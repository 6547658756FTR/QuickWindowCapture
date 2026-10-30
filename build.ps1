$ErrorActionPreference = "Stop"

$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$sourceFolder = Join-Path $projectRoot "src"
$outputFolder = Join-Path $projectRoot "dist"
$objectFolder = Join-Path $projectRoot "obj"
$compiler = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"

if (-not (Test-Path -LiteralPath $compiler)) {
    $compiler = "C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe"
}

if (-not (Test-Path -LiteralPath $compiler)) {
    throw ".NET Framework C# 컴파일러를 찾지 못했습니다."
}

New-Item -ItemType Directory -Path $outputFolder -Force | Out-Null
New-Item -ItemType Directory -Path $objectFolder -Force | Out-Null

$sources = Get-ChildItem -LiteralPath $sourceFolder -Filter "*.cs" | Select-Object -ExpandProperty FullName
$outputFile = Join-Path $outputFolder "QuickWindowCapture-v1.6.4.exe"
$manifest = Join-Path $projectRoot "app.manifest"
$iconFile = Join-Path $objectFolder "QuickWindowCapture.ico"
$frameworkFolder = Split-Path -Parent $compiler
$presentationCore = Join-Path $frameworkFolder "WPF\PresentationCore.dll"
$windowsBase = Join-Path $frameworkFolder "WPF\WindowsBase.dll"

# 실행 파일과 트레이에서 같은 카메라 아이콘을 사용합니다.
Add-Type -AssemblyName System.Drawing
$bitmap = New-Object System.Drawing.Bitmap(32, 32)
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$graphics.Clear([System.Drawing.Color]::Transparent)
$background = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(37, 99, 235))
$windowPen = New-Object System.Drawing.Pen([System.Drawing.Color]::White, 2.2)
$shutter = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(250, 204, 21))
$shutterOutline = New-Object System.Drawing.Pen([System.Drawing.Color]::White, 1.4)

try {
    $graphics.FillEllipse($background, 1, 1, 30, 30)
    $graphics.DrawRectangle($windowPen, 7, 8, 18, 14)
    $graphics.DrawLine($windowPen, 8, 12, 24, 12)
    $graphics.FillEllipse($shutter, 12, 14, 8, 8)
    $graphics.DrawEllipse($shutterOutline, 12, 14, 8, 8)
    $icon = [System.Drawing.Icon]::FromHandle($bitmap.GetHicon())
    $stream = [IO.File]::Create($iconFile)
    try {
        $icon.Save($stream)
    }
    finally {
        $stream.Dispose()
        $icon.Dispose()
    }
}
finally {
    $shutterOutline.Dispose()
    $shutter.Dispose()
    $windowPen.Dispose()
    $background.Dispose()
    $graphics.Dispose()
    $bitmap.Dispose()
}

$compilerArguments = @(
    "/nologo"
    "/target:winexe"
    "/optimize+"
    "/platform:anycpu"
    "/win32manifest:$manifest"
    "/win32icon:$iconFile"
    "/out:$outputFile"
    "/reference:System.dll"
    "/reference:System.Core.dll"
    "/reference:System.Drawing.dll"
    "/reference:System.Windows.Forms.dll"
    "/reference:$windowsBase"
    "/reference:$presentationCore"
)

$soundFolder = Join-Path $projectRoot "assets\sounds\trimmed"
$soundResources = Get-ChildItem -LiteralPath $soundFolder -Filter "*.wav" | ForEach-Object {
    "/resource:$($_.FullName),QuickWindowCapture.Sounds.$($_.Name)"
}

$compilerArguments += $soundResources
$compilerArguments += $sources

& $compiler $compilerArguments
if ($LASTEXITCODE -ne 0) {
    throw "빌드에 실패했습니다."
}

Write-Host "완료: $outputFile"
