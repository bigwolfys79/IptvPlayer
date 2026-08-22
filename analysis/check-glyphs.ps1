Add-Type -AssemblyName PresentationCore
foreach ($f in @('segfluenticons.ttf', 'segmdl2.ttf')) {
    $p = Join-Path 'C:\Windows\Fonts' $f
    if (-not (Test-Path $p)) { Write-Host "$f NOT FOUND"; continue }
    $gt = New-Object System.Windows.Media.GlyphTypeface($p)
    Write-Host "=== $f ==="
    foreach ($cp in @(0xE7C8, 0xE71A, 0xE785, 0xE787, 0xE767, 0xE713, 0xE7ED)) {
        Write-Host ('0x{0:X}: {1}' -f $cp, $gt.CharacterToGlyphMap.ContainsKey($cp))
    }
}
