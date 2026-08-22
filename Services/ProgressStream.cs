using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace IptvPlayer.Services
{
    /// <summary>
    /// Обёртка над Stream для измерения скорости загрузки в реальном времени.
    /// Подсчитывает количество прочитанных байт и вызывает событие SpeedChanged
    /// каждую секунду с текущей скоростью в байтах в секунду.
    /// </summary>
    public class ProgressStream : Stream
    {
        private readonly Stream _baseStream;
        private long _bytesReadSinceLastCheck;
        private readonly Stopwatch _stopwatch;
        private readonly object _lock = new();

        /// <summary>
        /// Событие вызывается каждую секунду со скоростью в байтах в секунду.
        /// </summary>
        public event Action<double>? SpeedChanged;

        /// <summary>
        /// Текущая скорость загрузки в байтах в секунду.
        /// </summary>
        public double CurrentSpeedBytesPerSecond { get; private set; }

        /// <summary>
        /// Всего прочитано байт с момента создания потока.
        /// </summary>
        public long TotalBytesRead { get; private set; }

        public ProgressStream(Stream baseStream)
        {
            _baseStream = baseStream ?? throw new ArgumentNullException(nameof(baseStream));
            _stopwatch = Stopwatch.StartNew();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var bytesRead = _baseStream.Read(buffer, offset, count);
            OnBytesRead(bytesRead);
            return bytesRead;
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            var bytesRead = await _baseStream.ReadAsync(buffer, offset, count, cancellationToken);
            OnBytesRead(bytesRead);
            return bytesRead;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var bytesRead = await _baseStream.ReadAsync(buffer, cancellationToken);
            OnBytesRead(bytesRead);
            return bytesRead;
        }

        private void OnBytesRead(int bytesRead)
        {
            if (bytesRead <= 0) return;

            lock (_lock)
            {
                TotalBytesRead += bytesRead;
                _bytesReadSinceLastCheck += bytesRead;

                // Каждую секунду вычисляем скорость
                if (_stopwatch.ElapsedMilliseconds >= 1000)
                {
                    var elapsedSeconds = _stopwatch.Elapsed.TotalSeconds;
                    CurrentSpeedBytesPerSecond = _bytesReadSinceLastCheck / elapsedSeconds;

                    // Вызываем событие (подписчик должен сам маршалировать в UI-поток если нужно)
                    SpeedChanged?.Invoke(CurrentSpeedBytesPerSecond);

                    // Сброс счётчиков
                    _bytesReadSinceLastCheck = 0;
                    _stopwatch.Restart();
                }
            }
        }

        // Делегируем остальные методы базовому потоку
        public override bool CanRead => _baseStream.CanRead;
        public override bool CanSeek => _baseStream.CanSeek;
        public override bool CanWrite => _baseStream.CanWrite;
        public override bool CanTimeout => _baseStream.CanTimeout;
        public override long Length => _baseStream.Length;
        public override long Position
        {
            get => _baseStream.Position;
            set => _baseStream.Position = value;
        }

        public override void Flush() => _baseStream.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => _baseStream.FlushAsync(cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => _baseStream.Seek(offset, origin);
        public override void SetLength(long value) => _baseStream.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => _baseStream.Write(buffer, offset, count);
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => _baseStream.WriteAsync(buffer, offset, count, cancellationToken);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _baseStream.Dispose();
            }
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await _baseStream.DisposeAsync();
            await base.DisposeAsync();
        }
    }
}
