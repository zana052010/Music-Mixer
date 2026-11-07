// Плеер предпросмотра на ManagedBass: два потока и независимые громкости.
// Слой Infrastructure; реализует IAudioPlayerService.
// Используется MainViewModel (мэшап) и диалогами (прослушивание источника).
//
// Ключевая идея: инструментал — ведущие часы. Вокал не «подгоняется» на лету,
// а ставится в позицию timeline - startOffset. Если она отрицательна, вокал
// ждёт в паузе, пока монитор не запустит его в нужный момент.
using ManagedBass;
using MusicMixer.Application.Abstractions;
using MusicMixer.Domain.Exceptions;
using MusicMixer.Domain.Models;

namespace MusicMixer.Infrastructure.Playback;

/// <summary>
/// Синхронный двухканальный предпросмотр на BASS. Нативная библиотека BASS
/// отдельно поставляется для каждой платформы; см. README.
/// </summary>
public sealed class ManagedBassAudioPlayerService : IAudioPlayerService
{
    // Монитор следит за отложенным стартом вокала и концом инструментала.
    // 20 мс достаточно, чтобы рассинхрон не был слышен, и дёшево по CPU.
    private const int MonitorIntervalMilliseconds = 20;
    // Если позиция почти у конца, Play начинает трек заново, а не стоит на месте.
    private const double RestartThresholdSeconds = 0.05;

    // Один замок на всё состояние: BASS-хэндлы и флаги меняются из UI-потока
    // и фонового монитора одновременно.
    private readonly object _gate = new();

    // 0 — валидное «нет потока» для BASS.
    private int _instrumentalHandle;
    private int _vocalHandle;
    private int _sourcePreviewHandle;
    private bool _bassInitialized;
    private bool _disposed;
    private bool _playRequested;
    private bool _vocalStarted;
    private double _durationSeconds;
    private double _vocalDurationSeconds;
    private double _sourcePreviewDurationSeconds;
    private double _startOffsetSeconds;
    private double _appliedInstrumentalVolume = 0.88;
    private double _appliedVocalVolume = 0.88;
    private CancellationTokenSource? _monitorCancellation;

    public bool IsLoaded
    {
        get
        {
            lock (_gate)
            {
                return _instrumentalHandle != 0 && _vocalHandle != 0;
            }
        }
    }

    public bool IsPlaying
    {
        get
        {
            lock (_gate)
            {
                // _playRequested важен: BASS может ещё показывать Playing,
                // пока мы уже инициировали паузу/остановку.
                return _playRequested &&
                       _instrumentalHandle != 0 &&
                       Bass.ChannelIsActive(_instrumentalHandle) == PlaybackState.Playing;
            }
        }
    }

    public TimeSpan Position
    {
        get
        {
            lock (_gate)
            {
                return TimeSpan.FromSeconds(Math.Clamp(
                    GetPositionSecondsLocked(_instrumentalHandle),
                    0,
                    Math.Max(0, _durationSeconds)));
            }
        }
    }

    public TimeSpan Duration
    {
        get
        {
            lock (_gate)
            {
                return TimeSpan.FromSeconds(_durationSeconds);
            }
        }
    }

    public bool IsSourcePreviewPlaying
    {
        get
        {
            lock (_gate)
            {
                return _sourcePreviewHandle != 0 &&
                       Bass.ChannelIsActive(_sourcePreviewHandle) == PlaybackState.Playing;
            }
        }
    }

    public TimeSpan SourcePreviewPosition
    {
        get
        {
            lock (_gate)
            {
                return TimeSpan.FromSeconds(Math.Clamp(
                    GetPositionSecondsLocked(_sourcePreviewHandle),
                    0,
                    Math.Max(0, _sourcePreviewDurationSeconds)));
            }
        }
    }

    public TimeSpan SourcePreviewDuration
    {
        get
        {
            lock (_gate)
            {
                return TimeSpan.FromSeconds(_sourcePreviewDurationSeconds);
            }
        }
    }

    public Task<TimeSpan> LoadSourcePreviewAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        // Открытие файла и создание потока BASS — блокирующая операция,
        // поэтому уводим её с UI-потока.
        return Task.Run(
            () => LoadSourcePreviewCore(filePath, cancellationToken),
            cancellationToken);
    }

    public void PlaySourcePreview()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            EnsurePreviewLoaded();

            if (GetPositionSecondsLocked(_sourcePreviewHandle) >=
                _sourcePreviewDurationSeconds - RestartThresholdSeconds)
            {
                SetPositionSecondsLocked(_sourcePreviewHandle, 0);
            }

            if (!Bass.ChannelPlay(_sourcePreviewHandle, false))
            {
                throw CreateBassException("Starting source preview");
            }
        }
    }

    public void PauseSourcePreview()
    {
        lock (_gate)
        {
            if (_sourcePreviewHandle != 0)
            {
                _ = Bass.ChannelPause(_sourcePreviewHandle);
            }
        }
    }

    public void StopSourcePreview()
    {
        lock (_gate)
        {
            if (_sourcePreviewHandle == 0)
            {
                return;
            }

            _ = Bass.ChannelPause(_sourcePreviewHandle);
            SetPositionSecondsLocked(_sourcePreviewHandle, 0);
        }
    }

    public void SeekSourcePreview(TimeSpan position)
    {
        lock (_gate)
        {
            if (_sourcePreviewHandle == 0)
            {
                return;
            }

            SetPositionSecondsLocked(
                _sourcePreviewHandle,
                Math.Clamp(position.TotalSeconds, 0, _sourcePreviewDurationSeconds));
        }
    }

    public void UnloadSourcePreview()
    {
        lock (_gate)
        {
            FreePreviewStreamLocked();
        }
    }

    public Task LoadAsync(
        string instrumentalFilePath,
        string vocalFilePath,
        AlignmentParameters alignment,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instrumentalFilePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(vocalFilePath);
        ArgumentNullException.ThrowIfNull(alignment);

        return Task.Run(
            () => LoadCore(instrumentalFilePath, vocalFilePath, alignment, cancellationToken),
            cancellationToken);
    }

    public void Play()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            EnsureLoaded();

            if (_playRequested)
            {
                return;
            }

            // Повторный Play после конца трека начинает воспроизведение с нуля.
            if (GetPositionSecondsLocked(_instrumentalHandle) >=
                _durationSeconds - RestartThresholdSeconds)
            {
                SetPositionSecondsLocked(_instrumentalHandle, 0);
                SetPositionSecondsLocked(_vocalHandle, 0);
            }

            _playRequested = true;
            _vocalStarted = false;

            if (!Bass.ChannelPlay(_instrumentalHandle, false))
            {
                _playRequested = false;
                throw CreateBassException("Starting instrumental playback");
            }

            // Вокал может стартовать сразу (offset <= 0) или позже —
            // тогда его запустит монитор.
            TryStartVocalLocked();
            StartMonitorLocked();
        }
    }

    public void Pause()
    {
        lock (_gate)
        {
            if (_instrumentalHandle == 0)
            {
                return;
            }

            _playRequested = false;
            CancelMonitorLocked();
            _ = Bass.ChannelPause(_instrumentalHandle);
            _ = Bass.ChannelPause(_vocalHandle);
            _vocalStarted = false;
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (_instrumentalHandle == 0)
            {
                return;
            }

            _playRequested = false;
            CancelMonitorLocked();
            _ = Bass.ChannelPause(_instrumentalHandle);
            _ = Bass.ChannelPause(_vocalHandle);
            SetPositionSecondsLocked(_instrumentalHandle, 0);
            SetPositionSecondsLocked(_vocalHandle, 0);
            _vocalStarted = false;
        }
    }

    public void Seek(TimeSpan position)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_instrumentalHandle == 0)
            {
                return;
            }

            // Перемотка всегда ставит оба потока на паузу, а затем
            // восстанавливает только то состояние, которое было до неё.
            var wasPlaying = _playRequested &&
                Bass.ChannelIsActive(_instrumentalHandle) == PlaybackState.Playing;
            _playRequested = false;
            CancelMonitorLocked();
            _ = Bass.ChannelPause(_instrumentalHandle);
            _ = Bass.ChannelPause(_vocalHandle);

            var target = Math.Clamp(position.TotalSeconds, 0, Math.Max(0, _durationSeconds));
            SetPositionSecondsLocked(_instrumentalHandle, target);
            _vocalStarted = false;

            if (!wasPlaying)
            {
                return;
            }

            _playRequested = true;
            if (!Bass.ChannelPlay(_instrumentalHandle, false))
            {
                _playRequested = false;
                throw CreateBassException("Resuming after seek");
            }

            // Пересчитываем позицию вокала относительно новой позиции инструментала.
            TryStartVocalLocked();
            StartMonitorLocked();
        }
    }

    public void SetVolumes(double instrumentalVolume, double vocalVolume)
    {
        if (!double.IsFinite(instrumentalVolume) || !double.IsFinite(vocalVolume))
        {
            throw new ArgumentOutOfRangeException(nameof(instrumentalVolume));
        }

        lock (_gate)
        {
            ThrowIfDisposed();
            _appliedInstrumentalVolume = Math.Clamp(instrumentalVolume, 0, 1);
            _appliedVocalVolume = Math.Clamp(vocalVolume, 0, 1);

            if (_instrumentalHandle == 0)
            {
                return;
            }

            ApplyVolumesLocked();
        }
    }

    public void SetStartOffset(TimeSpan startOffset)
    {
        if (startOffset < TimeSpan.FromMinutes(-30) || startOffset > TimeSpan.FromMinutes(30))
        {
            throw new ArgumentOutOfRangeException(nameof(startOffset));
        }

        lock (_gate)
        {
            ThrowIfDisposed();
            _startOffsetSeconds = startOffset.TotalSeconds;

            // Сдвиг применяется без перезапуска микса: достаточно заново
            // выставить позицию вокала относительно текущего времени.
            if (_playRequested &&
                _instrumentalHandle != 0 &&
                Bass.ChannelIsActive(_instrumentalHandle) == PlaybackState.Playing)
            {
                _vocalStarted = false;
                TryStartVocalLocked();
            }
        }
    }

    public void Unload()
    {
        lock (_gate)
        {
            _playRequested = false;
            CancelMonitorLocked();
            FreeStreamsLocked();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _playRequested = false;
            CancelMonitorLocked();
            FreeStreamsLocked();
            FreePreviewStreamLocked();

            // Bass.Free освобождает нативное устройство; без этого DLL
            // остаётся загруженной до завершения процесса.
            if (_bassInitialized)
            {
                _ = Bass.Free();
                _bassInitialized = false;
            }
        }
    }

    private TimeSpan LoadSourcePreviewCore(
        string filePath,
        CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(filePath);
        if (!File.Exists(fullPath))
        {
            throw new AudioProcessingException($"Source file was not found: {fullPath}");
        }

        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfDisposed();
            EnsureBassInitializedLocked();
            cancellationToken.ThrowIfCancellationRequested();

            var newHandle = 0;
            try
            {
                newHandle = Bass.CreateStream(fullPath, 0, 0, BassFlags.Default);
                if (newHandle == 0)
                {
                    throw CreateBassException("Opening the source file for preview");
                }

                var duration = GetDurationSecondsLocked(newHandle);
                if (duration <= 0)
                {
                    throw new AudioProcessingException(
                        "BASS loaded the source file but reported an invalid duration.");
                }

                SetChannelVolume(newHandle, 0.9, "Setting preview volume");
                SetPositionSecondsLocked(newHandle, 0);

                // Сначала открываем новый поток, и только потом освобождаем
                // старый: так неудачная загрузка не оставляет плеер пустым.
                FreePreviewStreamLocked();
                _sourcePreviewHandle = newHandle;
                _sourcePreviewDurationSeconds = duration;
                return TimeSpan.FromSeconds(duration);
            }
            catch
            {
                FreeStream(newHandle);
                throw;
            }
        }
    }

    private void LoadCore(
        string instrumentalFilePath,
        string vocalFilePath,
        AlignmentParameters alignment,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var instrumentalPath = Path.GetFullPath(instrumentalFilePath);
        var vocalPath = Path.GetFullPath(vocalFilePath);

        if (!File.Exists(instrumentalPath))
        {
            throw new AudioProcessingException($"Instrumental stem was not found: {instrumentalPath}");
        }

        if (!File.Exists(vocalPath))
        {
            throw new AudioProcessingException($"Aligned vocal stem was not found: {vocalPath}");
        }

        lock (_gate)
        {
            ThrowIfDisposed();
            EnsureBassInitializedLocked();
            cancellationToken.ThrowIfCancellationRequested();

            if (_instrumentalHandle != 0)
            {
                _playRequested = false;
                CancelMonitorLocked();
                _ = Bass.ChannelPause(_instrumentalHandle);
                _ = Bass.ChannelPause(_vocalHandle);
            }

            var newInstrumental = 0;
            var newVocal = 0;
            try
            {
                newInstrumental = Bass.CreateStream(instrumentalPath, 0, 0, BassFlags.Default);
                if (newInstrumental == 0)
                {
                    throw CreateBassException("Opening the instrumental WAV");
                }

                newVocal = Bass.CreateStream(vocalPath, 0, 0, BassFlags.Default);
                if (newVocal == 0)
                {
                    throw CreateBassException("Opening the aligned vocal WAV");
                }

                var duration = GetDurationSecondsLocked(newInstrumental);
                var vocalDuration = GetDurationSecondsLocked(newVocal);
                if (duration <= 0 || vocalDuration <= 0)
                {
                    throw new AudioProcessingException("BASS loaded the streams but reported an invalid duration.");
                }

                SetChannelVolume(newInstrumental, _appliedInstrumentalVolume, "Setting instrumental volume");
                SetChannelVolume(newVocal, _appliedVocalVolume, "Setting vocal volume");

                var oldInstrumental = _instrumentalHandle;
                var oldVocal = _vocalHandle;
                _instrumentalHandle = newInstrumental;
                _vocalHandle = newVocal;
                _durationSeconds = duration;
                _vocalDurationSeconds = vocalDuration;
                _startOffsetSeconds = alignment.StartOffset.TotalSeconds;
                _vocalStarted = false;

                // Атомарная подмена для вызывающего кода: старые хэндлы
                // освобождаются только после успешного открытия новых.
                FreeStream(oldInstrumental);
                FreeStream(oldVocal);
            }
            catch
            {
                FreeStream(newInstrumental);
                FreeStream(newVocal);
                throw;
            }
        }
    }

    /// <summary>
    /// Ставит вокал в позицию timeline - startOffset. Три случая:
    /// позиция отрицательна — ждём; позиция за концом — вокал уже закончился;
    /// иначе перематываем и запускаем.
    /// </summary>
    private void TryStartVocalLocked()
    {
        if (_vocalHandle == 0 || !_playRequested)
        {
            return;
        }

        var timeline = GetPositionSecondsLocked(_instrumentalHandle);
        var desiredVocalPosition = timeline - _startOffsetSeconds;

        if (desiredVocalPosition < 0)
        {
            // Вокал должен вступить позже: держим его на паузе, монитор
            // попробует снова через 20 мс.
            _ = Bass.ChannelPause(_vocalHandle);
            _vocalStarted = false;
            return;
        }

        if (desiredVocalPosition >= _vocalDurationSeconds)
        {
            // Вокал короче инструментала и уже закончился.
            _ = Bass.ChannelPause(_vocalHandle);
            _vocalStarted = true;
            return;
        }

        _ = Bass.ChannelPause(_vocalHandle);
        SetPositionSecondsLocked(_vocalHandle, desiredVocalPosition);
        if (!Bass.ChannelPlay(_vocalHandle, false))
        {
            throw CreateBassException("Starting vocal playback");
        }

        _vocalStarted = true;
    }

    private void StartMonitorLocked()
    {
        CancelMonitorLocked();
        var cancellation = new CancellationTokenSource();
        _monitorCancellation = cancellation;
        _ = Task.Run(() => MonitorPlaybackAsync(cancellation.Token), CancellationToken.None);
    }

    /// <summary>
    /// Фоновый цикл на 20 мс. Пока инструментал играет, он ждёт момента
    /// вступления вокала; когда инструментал закончился — останавливает вокал.
    /// </summary>
    private async Task MonitorPlaybackAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(MonitorIntervalMilliseconds));
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                lock (_gate)
                {
                    if (_disposed || !_playRequested || _instrumentalHandle == 0)
                    {
                        return;
                    }

                    var state = Bass.ChannelIsActive(_instrumentalHandle);
                    if (state == PlaybackState.Playing || state == PlaybackState.Stalled)
                    {
                        if (!_vocalStarted)
                        {
                            TryStartVocalLocked();
                        }

                        continue;
                    }

                    if (state == PlaybackState.Paused)
                    {
                        return;
                    }

                    _playRequested = false;
                    _vocalStarted = false;
                    _ = Bass.ChannelPause(_vocalHandle);
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            lock (_gate)
            {
                _playRequested = false;
                _vocalStarted = false;
            }
        }
    }

    private void CancelMonitorLocked()
    {
        if (_monitorCancellation is null)
        {
            return;
        }

        try
        {
            _monitorCancellation.Cancel();
            _monitorCancellation.Dispose();
        }
        catch (ObjectDisposedException)
        {
        }

        _monitorCancellation = null;
    }

    /// <summary>
    /// Ленивая инициализация BASS. Если нативная DLL не найдена или имеет
    /// неверную разрядность, пользователь получает инструкцию по установке,
    /// а не сырое DllNotFoundException.
    /// </summary>
    private void EnsureBassInitializedLocked()
    {
        if (_bassInitialized)
        {
            return;
        }

        try
        {
            if (!Bass.Init(-1, 48_000, DeviceInitFlags.Default, IntPtr.Zero))
            {
                throw new AudioProcessingException(
                    "BASS could not open an audio output device. " +
                    $"BASS error: {SafeLastError()}.");
            }

            _bassInitialized = true;
        }
        catch (AudioProcessingException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is DllNotFoundException or
            BadImageFormatException or
            EntryPointNotFoundException)
        {
            throw new AudioProcessingException(
                "The native BASS library could not be loaded. Install the 64-bit " +
                "bass.dll / libbass.so / libbass.dylib for this platform next to the application.",
                exception.Message);
        }
    }

    private void ApplyVolumesLocked()
    {
        SetChannelVolume(_instrumentalHandle, _appliedInstrumentalVolume, "Setting instrumental volume");
        SetChannelVolume(_vocalHandle, _appliedVocalVolume, "Setting vocal volume");
    }

    private static void SetChannelVolume(int handle, double volume, string action)
    {
        if (!Bass.ChannelSetAttribute(handle, ChannelAttribute.Volume, (float)volume))
        {
            throw CreateBassException(action);
        }
    }

    private static double GetDurationSecondsLocked(int handle)
    {
        if (handle == 0)
        {
            return 0;
        }

        // BASS умеет считать в байтах и в секундах; байты точнее для WAV,
        // поэтому конвертируем их через сам поток.
        var length = Bass.ChannelGetLength(handle, PositionFlags.Bytes);
        return length > 0 ? Bass.ChannelBytes2Seconds(handle, length) : 0;
    }

    private static double GetPositionSecondsLocked(int handle)
    {
        if (handle == 0)
        {
            return 0;
        }

        var position = Bass.ChannelGetPosition(handle, PositionFlags.Bytes);
        return position > 0 ? Bass.ChannelBytes2Seconds(handle, position) : 0;
    }

    private static void SetPositionSecondsLocked(int handle, double seconds)
    {
        if (handle == 0)
        {
            return;
        }

        var bytes = Bass.ChannelSeconds2Bytes(handle, Math.Max(0, seconds));
        if (!Bass.ChannelSetPosition(handle, bytes, PositionFlags.Bytes))
        {
            throw CreateBassException("Seeking the audio stream");
        }
    }

    private void FreeStreamsLocked()
    {
        FreeStream(_instrumentalHandle);
        FreeStream(_vocalHandle);
        _instrumentalHandle = 0;
        _vocalHandle = 0;
        _durationSeconds = 0;
        _vocalDurationSeconds = 0;
        _vocalStarted = false;
    }

    private void FreePreviewStreamLocked()
    {
        FreeStream(_sourcePreviewHandle);
        _sourcePreviewHandle = 0;
        _sourcePreviewDurationSeconds = 0;
    }

    private void EnsurePreviewLoaded()
    {
        if (_sourcePreviewHandle == 0)
        {
            throw new AudioProcessingException("No source file is loaded for preview.");
        }
    }

    private static void FreeStream(int handle)
    {
        if (handle != 0)
        {
            _ = Bass.StreamFree(handle);
        }
    }

    private static AudioProcessingException CreateBassException(string action)
    {
        // LastError читается безопасно: если DLL вообще не загрузилась,
        // SafeLastError вернёт текст вместо нового исключения.
        return new AudioProcessingException($"{action} failed. BASS error: {SafeLastError()}.");
    }

    private static string SafeLastError()
    {
        try
        {
            return Bass.LastError.ToString();
        }
        catch (DllNotFoundException)
        {
            return "native BASS library unavailable";
        }
    }

    private void EnsureLoaded()
    {
        if (_instrumentalHandle == 0 || _vocalHandle == 0)
        {
            throw new AudioProcessingException("No mashup is loaded in the audio player.");
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
