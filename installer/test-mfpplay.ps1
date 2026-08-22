# Диагностика: декодирует ли системный Media Foundation видеодорожку потока.
# NaturalVideoWidth > 0 = видео декодируется (проблема была бы в приложении),
# 0 при играющем звуке = видеодорожка не декодируется (кодек/система).
param([string]$Url, [int]$Seconds = 20)

$mpType = [Windows.Media.Playback.MediaPlayer, Windows.Media.Playback, ContentType = WindowsRuntime]
$msType = [Windows.Media.Core.MediaSource, Windows.Media.Core, ContentType = WindowsRuntime]

$mp = New-Object $mpType
$mp.Source = $msType::CreateFromUri([Uri]$Url)
$mp.Volume = 0
$mp.Play()

for ($i = 0; $i -lt $Seconds; $i++) {
    Start-Sleep -Seconds 1
    $s = $mp.PlaybackSession
    "{0,2} c: state={1} video={2}x{3}" -f ($i + 1), $s.PlaybackState, $s.NaturalVideoWidth, $s.NaturalVideoHeight
}

$mp.Dispose()
