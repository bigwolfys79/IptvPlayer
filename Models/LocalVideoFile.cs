using System;

namespace IptvPlayer.Models;

/// <summary>
/// Локальный видеофайл, выбранный на хабе (карточка «Видео»): параметр
/// навигации в MainPage. Path — абсолютный путь на диске, Title — имя
/// файла без расширения (заголовок воспроизведения и ключ позиции
/// досмотра в VodResumeStore).
/// </summary>
public sealed record LocalVideoFile(string Path, string Title);
