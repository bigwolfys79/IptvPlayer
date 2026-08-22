# Установка декодеров Dolby (AC-3 / E-AC-3 / AC-4) для Windows 11 24H2 и новее.
#
# С этих версий Microsoft убрала встроенный декодер AC-3 из состава системы,
# из-за чего потоки с дорожкой Dolby Digital (например, BCU TruMotion HD в
# IptvPlayer) не воспроизводятся. Скрипт ставит декодеры Dolby для OEM:
#   1) через winget/Microsoft Store (если регион аккаунта позволяет);
#   2) при отказе Store — скачивает официальные подписанные пакеты с зеркала
#      MajorGeeks и устанавливает их сайдлоадом (Add-AppxPackage).
# Источники решения:
#   https://github.com/Victor-Freeze/AC-3-for-Windows-11
#   https://www.majorgeeks.com/files/details/dolby_ac_3ac_4_installer.html
#
# Запуск: powershell -ExecutionPolicy Bypass -File Install-DolbyDecoders.ps1

$ErrorActionPreference = 'Continue'
$ProgressPreference = 'SilentlyContinue'
$pkgDd = 'DolbyLaboratories.DolbyDigitalPlusDecoderOEM'

function Test-Installed {
    return [bool](Get-AppxPackage -Name $pkgDd -ErrorAction SilentlyContinue)
}

if (Test-Installed) {
    Write-Host "Декодер Dolby уже установлен:" -ForegroundColor Cyan
    Get-AppxPackage *Dolby* | Select-Object Name, Version | Format-Table -AutoSize
    exit 0
}

Write-Host "== Установка декодеров Dolby для Windows 11 ==" -ForegroundColor Cyan

# --- Способ 1: Microsoft Store через winget ---------------------------------
$installed = $false
if (Get-Command winget -ErrorAction SilentlyContinue) {
    Write-Host "`n[1] Пробую Microsoft Store (winget)..." -ForegroundColor Green
    winget install --id 9nvjqjbdkn97 -e --accept-source-agreements --accept-package-agreements
    $installed = Test-Installed
}

# --- Способ 2: сайдлоад подписанных пакетов с зеркала MajorGeeks ------------
if (-not $installed) {
    Write-Host "`n[2] Store не сработал (регион/лицензия) — скачиваю пакеты с зеркала..." -ForegroundColor Green
    $tmp = Join-Path $env:TEMP 'dolby-decoders'
    New-Item -ItemType Directory -Force -Path $tmp | Out-Null
    try {
        # Страница-зеркало отдаёт HTML с одноразовой ссылкой на files*.majorgeeks.com
        $mirror = Invoke-WebRequest -UseBasicParsing -UserAgent 'Mozilla/5.0' `
            -Uri 'https://www.majorgeeks.com/mg/getmirror/dolby_ac_3ac_4_installer,1.html'
        $link = ($mirror.Links | Where-Object href -match '\.zip$' | Select-Object -First 1).href
        if (-not $link) { $link = [regex]::Match($mirror.Content, 'https?://[^"''<> ]*\.zip').Value }
        if (-not $link) { throw 'ссылка на zip не найдена на странице-зеркале' }

        $zip = Join-Path $tmp 'Dolby_AC4_AC3_Installer.zip'
        Invoke-WebRequest -UseBasicParsing -UserAgent 'Mozilla/5.0' -Uri $link -OutFile $zip
        Expand-Archive -Path $zip -DestinationPath $tmp -Force

        foreach ($bundle in (Get-ChildItem "$tmp\*.AppxBundle")) {
            Write-Host "  установка $($bundle.Name)..." -ForegroundColor DarkGray
            Add-AppxPackage -Path $bundle.FullName
        }
        $installed = Test-Installed
    }
    catch {
        Write-Host "  не удалось: $($_.Exception.Message)" -ForegroundColor Red
    }
}

Write-Host "`n== Результат ==" -ForegroundColor Cyan
if ($installed) {
    Get-AppxPackage *Dolby* | Select-Object Name, Version | Format-Table -AutoSize
    Write-Host "Готово. Перезапустите IptvPlayer и включите канал с AC-3 (например, BCU TruMotion HD)." -ForegroundColor Cyan
} else {
    Write-Host "Декодер установить не удалось. Скачайте вручную:" -ForegroundColor Red
    Write-Host "  https://www.majorgeeks.com/files/details/dolby_ac_3ac_4_installer.html"
    Write-Host "и запустите оба .AppxBundle из архива двойным кликом."
}
