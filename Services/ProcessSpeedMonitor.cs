using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;

namespace IptvPlayer.Services;

/// <summary>
/// Измерение фактической скорости загрузки по счётчикам ввода-вывода
/// процесса: ОС суммирует байты чтения и из файлов, и из сокетов — в том
/// числе тех, которыми FFmpeg качает поток. Ни MediaPlaybackSession, ни
/// FFmpegInteropX счётчиков байт наружу не отдают (для HLS — в принципе,
/// см. StreamService.UpdateDownloadSpeed), а здесь измерение получается
/// «бесплатно»: тракт воспроизведения не меняется и лишнего трафика нет.
/// Managed-класс Process эти счётчики не отдаёт, поэтому Win32
/// GetProcessIoCounters для собственного процесса (прав не требует).
///
/// Цена вопроса — счётчик процесс-wide: чужие большие загрузки приложения
/// (EPG-фиды, плейлисты, дисковый кэш) на время PauseScope исключаются из
/// замера (показывается последнее значение), а разовая мелочь вроде иконок
/// каналов гасится медианой по окну в несколько секунд.
/// </summary>
public sealed class ProcessSpeedMonitor
{
    // Медиана по ~5 замеров: единичный пик (иконка, мелкий файл) не
    // пролезает в показание.
    private const int WindowSamples = 5;

    // Перерыв между Sample() больше этого — окно устарело, копим заново.
    private const double GapResetSeconds = 4;

    private readonly object _gate = new();
    private readonly Queue<double> _window = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private ulong _lastBytes;
    private int _pauseDepth;

    /// <summary>
    /// Снимает очередной замер; вызывать примерно раз в секунду (тик
    /// оверлея статистики). Возвращает сглаженную скорость в бит/с или
    /// null, пока замеров ещё нет.
    /// </summary>
    public double? Sample()
    {
        lock (_gate)
        {
            var elapsed = _clock.Elapsed.TotalSeconds;
            _clock.Restart();

            if (!GetProcessIoCounters(GetCurrentProcess(), out var io))
            {
                // Счётчики собственного процесса недоступны разве что при
                // гибели процесса — измерять больше нечего.
                return Median();
            }

            // В Windows сокетные чтения (включая HLS-сегменты, которые
            // FFmpeg качает для воспроизведения) идут в OtherTransferCount,
            // а не в ReadTransferCount (там — только файловые ReadFile).
            // Подтверждено экспериментально: скачивание 1 МБ по HTTP дало
            // Read=0, Other=755K, Write=1MB.
            var bytes = io.OtherTransferCount;

            // Дельта достоверна, только если между тиками не было чужой
            // загрузки (PauseScope) и большой паузы; счётчик назад не
            // ходит, но на всякий случай страхуемся.
            var reliable = _pauseDepth == 0 && elapsed <= GapResetSeconds && bytes >= _lastBytes;

            if (reliable && elapsed > 0)
            {
                _window.Enqueue((bytes - _lastBytes) * 8 / elapsed);
                while (_window.Count > WindowSamples)
                {
                    _window.Dequeue();
                }
            }
            else if (elapsed > GapResetSeconds)
            {
                _window.Clear();
            }
            // При открытом PauseScope окно не чистим: последнее значение
            // остаётся на показ, а базу пересчитываем, чтобы после чужой
            // загрузки её байты не попали в следующий замер.

            _lastBytes = bytes;

            return Median();
        }
    }

    private double? Median()
    {
        if (_window.Count == 0)
        {
            return null;
        }

        var sorted = _window.OrderBy(x => x).ToArray();
        return sorted[sorted.Length / 2];
    }

    /// <summary>
    /// Пока scope открыт, новые замеры не набираются (последнее значение
    /// остаётся). Обязателен вокруг загрузок, чьи байты не должны попадать
    /// в скорость потока: EPG-фиды, плейлисты, дисковый кэш. Вкладывать
    /// scopes друг в друга можно.
    /// </summary>
    public IDisposable PauseScope()
    {
        lock (_gate)
        {
            _pauseDepth++;
        }

        return new PauseToken(this);
    }

    private void ReleasePause()
    {
        lock (_gate)
        {
            if (_pauseDepth > 0)
            {
                _pauseDepth--;
            }
        }
    }

    private sealed class PauseToken : IDisposable
    {
        private readonly ProcessSpeedMonitor _owner;

        public PauseToken(ProcessSpeedMonitor owner)
        {
            _owner = owner;
        }

        public void Dispose() => _owner.ReleasePause();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [DllImport("kernel32.dll")]
    private static extern bool GetProcessIoCounters(IntPtr hProcess, out IO_COUNTERS ioCounters);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();
}
