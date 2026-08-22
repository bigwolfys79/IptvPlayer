# Генерация MSIX-логотипов Package.appxmanifest из Assets\AppIcon.ico.
# GDI+ Icon.ToBitmap() не читает PNG-сжатые кадры ICO, поэтому парсим
# каталог ICO вручную: берём самый крупный кадр (для 256x256 это обычно
# встроенный PNG) и уже его масштабируем.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$assets = 'F:\winplayWinUi\IptvPlayer\Assets'
$icoPath = Join-Path $assets 'AppIcon.ico'
$bytes = [System.IO.File]::ReadAllBytes($icoPath)

if ($bytes.Length -lt 6 -or $bytes[0] -ne 0 -or $bytes[1] -ne 0) {
    throw 'Не похоже на ICO-файл'
}
$count = [BitConverter]::ToUInt16($bytes, 4)

$best = $null
for ($i = 0; $i -lt $count; $i++) {
    $off = 6 + $i * 16
    $w = $bytes[$off]; if ($w -eq 0) { $w = 256 }
    $h = $bytes[$off + 1]; if ($h -eq 0) { $h = 256 }
    $size = [BitConverter]::ToUInt32($bytes, $off + 8)
    $dataOff = [BitConverter]::ToUInt32($bytes, $off + 12)
    $isPng = ($bytes[$dataOff] -eq 0x89 -and $bytes[$dataOff + 1] -eq 0x50)
    if ($best -eq $null -or $w -gt $best.W) {
        $best = @{ W = $w; H = $h; Size = $size; Off = $dataOff; Png = $isPng }
    }
}
Write-Host ("Лучший кадр: {0}x{1} ({2}), PNG={3}" -f $best.W, $best.H, $best.Size, $best.Png)

$framePath = Join-Path $env:TEMP 'iptv_icon_frame'
if ($best.Png) {
    $framePath = "$framePath.png"
    [System.IO.File]::WriteAllBytes($framePath, $bytes[$best.Off..($best.Off + $best.Size - 1)])
    $source = [System.Drawing.Bitmap]::FromFile($framePath)
} else {
    # BMP-кадр: Icon умеет читать маленькие классические кадры через
    # временный одно-кадровый ICO — собираем его заново.
    $framePath = "$framePath.ico"
    $ms = New-Object System.IO.MemoryStream
    $ms.Write([BitConverter]::GetBytes([UInt16]0), 0, 2)
    $ms.Write([BitConverter]::GetBytes([UInt16]1), 0, 2)
    $entry = New-Object byte[] 16
    $entry[0] = if ($best.W -ge 256) { 0 } else { $best.W }
    $entry[1] = if ($best.H -ge 256) { 0 } else { $best.H }
    [Array]::Copy([BitConverter]::GetBytes([UInt32]$best.Size), 0, $entry, 8, 4)
    [Array]::Copy([BitConverter]::GetBytes([UInt32]$best.Off), 0, $entry, 12, 4)
    $ms.Write($entry, 0, 16)
    $ms.Write($bytes, $best.Off, $best.Size)
    [System.IO.File]::WriteAllBytes($framePath, $ms.ToArray())
    $ms.Dispose()
    $ic = New-Object System.Drawing.Icon($framePath)
    $source = $ic.ToBitmap()
    $ic.Dispose()
}
Write-Host ("Загружено: {0}x{1}" -f $source.Width, $source.Height)

function Save-Resized([int]$w, [int]$h, [string]$name) {
    $bmp = New-Object System.Drawing.Bitmap($w, $h)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $scale = [Math]::Min($w / $source.Width, $h / $source.Height)
    $dw = [int]($source.Width * $scale)
    $dh = [int]($source.Height * $scale)
    $dx = [int](($w - $dw) / 2)
    $dy = [int](($h - $dh) / 2)
    $g.DrawImage($source, $dx, $dy, $dw, $dh)
    $g.Dispose()
    $bmp.Save((Join-Path $assets $name), [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Host ("{0}: {1}x{2}" -f $name, $w, $h)
}

Save-Resized 300 300 'Square150x150Logo.scale-200.png'
Save-Resized 88 88 'Square44x44Logo.scale-200.png'
Save-Resized 24 24 'Square44x44Logo.targetsize-24_altform-unplated.png'
Save-Resized 48 48 'Square44x44Logo.targetsize-48_altform-lightunplated.png'
Save-Resized 50 50 'StoreLogo.png'
Save-Resized 620 300 'Wide310x150Logo.scale-200.png'
Save-Resized 620 300 'SplashScreen.scale-200.png'
Save-Resized 200 200 'LockScreenLogo.scale-200.png'

$source.Dispose()
Remove-Item $framePath -ErrorAction SilentlyContinue
Write-Host 'Готово.'
