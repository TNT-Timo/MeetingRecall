using System.Diagnostics;
using System.Text.Json;
using MeetingRecall.Core.Audio;
using MeetingRecall.Core.Entities;
using MeetingRecall.Core.Enums;
using MeetingRecall.Core.Interfaces;
using MeetingRecall.Infrastructure.Storage;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MeetingRecall.Infrastructure.Audio;

internal sealed class NativeAudioRecordingService(
    AppDataPaths paths,
    IAudioArchiveRepository audioArchiveRepository,
    AudioMixdownService mixdownService) : INativeAudioRecordingService, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private RecordingSession? _session;
    private bool _disposed;

    public bool IsRecording => Volatile.Read(ref _session) is not null;

    public DateTimeOffset? StartedAt => Volatile.Read(ref _session)?.StartedAt;

    public TimeSpan Elapsed => Volatile.Read(ref _session)?.Elapsed ?? TimeSpan.Zero;

    public Task<IReadOnlyList<AudioCaptureDevice>> ListMicrophonesAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<AudioCaptureDevice>>(
            ListDevices(DataFlow.Capture, Role.Communications));
    }

    public Task<IReadOnlyList<AudioCaptureDevice>> ListDesktopOutputsAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<AudioCaptureDevice>>(
            ListDevices(DataFlow.Render, Role.Multimedia));
    }

    public async Task StartAsync(
        NativeRecordingOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ThrowIfDisposed();

        if (!options.CaptureMicrophone && !options.CaptureDesktopAudio)
        {
            throw new ArgumentException(
                "Mindestens eine Audioquelle muss aktiviert sein.",
                nameof(options));
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_session is not null)
            {
                throw new InvalidOperationException("Es läuft bereits eine Aufnahme.");
            }

            paths.EnsureCreated();
            RecordingSession session = CreateSession(options);
            try
            {
                session.Start();
                _session = session;
            }
            catch
            {
                session.Dispose();
                TryDeleteDirectory(session.MeetingDirectory);
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<NativeRecordingResult> StopAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        RecordingSession session;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            session = _session
                ?? throw new InvalidOperationException("Es läuft keine Aufnahme.");
            _session = null;
        }
        finally
        {
            _gate.Release();
        }

        try
        {
            await session.StopAsync(cancellationToken).ConfigureAwait(false);

            IReadOnlyList<AudioMixInput> mixInputs = session.CreateMixInputs();
            string primaryPath = Path.Combine(session.AudioDirectory, "primary.wav");
            TimeSpan duration = await mixdownService.CreateAsync(
                mixInputs,
                primaryPath,
                cancellationToken).ConfigureAwait(false);

            if (duration <= TimeSpan.Zero)
            {
                throw new InvalidOperationException("Die Aufnahme enthält keine verwertbare Dauer.");
            }

            string title = string.IsNullOrWhiteSpace(session.Options.Title)
                ? $"Aufnahme {session.StartedAt:dd.MM.yyyy HH:mm}"
                : session.Options.Title.Trim();

            var conversation = new Conversation(
                session.ConversationId,
                title,
                session.StartedAt,
                ConversationSource.NativeRecording);
            conversation.SetDuration(duration);

            var tracks = new List<AudioTrack>
            {
                CreateTrack(
                    conversation.Id,
                    AudioTrackKind.Primary,
                    primaryPath,
                    "Aufnahme.wav",
                    duration)
            };

            foreach (CaptureTrack captureTrack in session.Tracks)
            {
                if (!captureTrack.HasAudioData)
                {
                    continue;
                }

                tracks.Add(CreateTrack(
                    conversation.Id,
                    captureTrack.Kind,
                    captureTrack.FilePath,
                    captureTrack.Kind == AudioTrackKind.MicrophoneRaw
                        ? "microphone.wav"
                        : "desktop.wav",
                    ReadDuration(captureTrack.FilePath)));
            }

            await audioArchiveRepository.ImportAsync(
                conversation,
                tracks,
                cancellationToken).ConfigureAwait(false);

            TryDeleteFile(session.MarkerFilePath);
            return new NativeRecordingResult(conversation, tracks);
        }
        finally
        {
            session.Dispose();
        }
    }

    public async Task CancelAsync(CancellationToken cancellationToken = default)
    {
        RecordingSession? session;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            session = _session;
            _session = null;
        }
        finally
        {
            _gate.Release();
        }

        if (session is null)
        {
            return;
        }

        try
        {
            await session.StopIgnoringErrorsAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            session.Dispose();
            TryDeleteDirectory(session.MeetingDirectory);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            CancelAsync().GetAwaiter().GetResult();
        }
        catch
        {
            // Application shutdown must not be blocked by an audio-device error.
        }

        _gate.Dispose();
    }

    private RecordingSession CreateSession(NativeRecordingOptions options)
    {
        Guid conversationId = Guid.NewGuid();
        string meetingDirectory = paths.GetMeetingDirectory(conversationId);
        string audioDirectory = paths.GetMeetingAudioDirectory(conversationId);
        Directory.CreateDirectory(audioDirectory);

        var tracks = new List<CaptureTrack>();
        try
        {
            if (options.CaptureDesktopAudio)
            {
                MMDevice desktopDevice = ResolveDevice(
                    DataFlow.Render,
                    Role.Multimedia,
                    options.DesktopOutputDeviceId);
                tracks.Add(new CaptureTrack(
                    AudioTrackKind.DesktopRaw,
                    Path.Combine(audioDirectory, "desktop.wav"),
                    desktopDevice,
                    new WasapiLoopbackCapture(desktopDevice)));
            }

            if (options.CaptureMicrophone)
            {
                MMDevice microphoneDevice = ResolveDevice(
                    DataFlow.Capture,
                    Role.Communications,
                    options.MicrophoneDeviceId);
                tracks.Add(new CaptureTrack(
                    AudioTrackKind.MicrophoneRaw,
                    Path.Combine(audioDirectory, "microphone.wav"),
                    microphoneDevice,
                    new WasapiCapture(microphoneDevice)));
            }

            return new RecordingSession(
                conversationId,
                meetingDirectory,
                audioDirectory,
                options,
                tracks);
        }
        catch
        {
            foreach (CaptureTrack track in tracks)
            {
                track.Dispose();
            }

            TryDeleteDirectory(meetingDirectory);
            throw;
        }
    }

    private AudioTrack CreateTrack(
        Guid conversationId,
        AudioTrackKind kind,
        string filePath,
        string originalFileName,
        TimeSpan duration)
    {
        var fileInfo = new FileInfo(filePath);
        return new AudioTrack(
            Guid.NewGuid(),
            conversationId,
            kind,
            paths.ToRelativePath(filePath),
            originalFileName,
            "audio/wav",
            fileInfo.Length,
            duration);
    }

    private static IReadOnlyList<AudioCaptureDevice> ListDevices(DataFlow flow, Role defaultRole)
    {
        using var enumerator = new MMDeviceEnumerator();
        string? defaultDeviceId = null;
        try
        {
            using MMDevice defaultDevice = enumerator.GetDefaultAudioEndpoint(flow, defaultRole);
            defaultDeviceId = defaultDevice.ID;
        }
        catch
        {
            // A machine may legitimately have no default endpoint for this flow.
        }

        var devices = new List<AudioCaptureDevice>();
        MMDeviceCollection endpoints = enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active);
        foreach (MMDevice endpoint in endpoints)
        {
            try
            {
                devices.Add(new AudioCaptureDevice(
                    endpoint.ID,
                    endpoint.FriendlyName,
                    string.Equals(endpoint.ID, defaultDeviceId, StringComparison.OrdinalIgnoreCase)));
            }
            finally
            {
                endpoint.Dispose();
            }
        }

        return devices
            .OrderByDescending(device => device.IsDefault)
            .ThenBy(device => device.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    private static MMDevice ResolveDevice(
        DataFlow flow,
        Role defaultRole,
        string? requestedDeviceId)
    {
        using var enumerator = new MMDeviceEnumerator();
        try
        {
            return string.IsNullOrWhiteSpace(requestedDeviceId)
                ? enumerator.GetDefaultAudioEndpoint(flow, defaultRole)
                : enumerator.GetDevice(requestedDeviceId);
        }
        catch (Exception exception)
        {
            string sourceName = flow == DataFlow.Capture ? "Mikrofon" : "Desktopaudio-Ausgabe";
            throw new InvalidOperationException(
                $"Die ausgewählte {sourceName} ist nicht mehr verfügbar.",
                exception);
        }
    }

    private static TimeSpan ReadDuration(string filePath)
    {
        using var reader = new WaveFileReader(filePath);
        return reader.TotalTime;
    }

    private static void TryDeleteFile(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
        catch
        {
            // The completed recording remains valid even if the marker cannot be removed.
        }
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch
        {
            // Cleanup is best effort; the original capture error remains more useful.
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private sealed class RecordingSession : IDisposable
    {
        private readonly Stopwatch _stopwatch = new();

        public RecordingSession(
            Guid conversationId,
            string meetingDirectory,
            string audioDirectory,
            NativeRecordingOptions options,
            IReadOnlyList<CaptureTrack> tracks)
        {
            ConversationId = conversationId;
            MeetingDirectory = meetingDirectory;
            AudioDirectory = audioDirectory;
            Options = options;
            Tracks = tracks;
            MarkerFilePath = Path.Combine(meetingDirectory, ".recording-session.json");
        }

        public Guid ConversationId { get; }

        public string MeetingDirectory { get; }

        public string AudioDirectory { get; }

        public string MarkerFilePath { get; }

        public NativeRecordingOptions Options { get; }

        public IReadOnlyList<CaptureTrack> Tracks { get; }

        public DateTimeOffset StartedAt { get; private set; }

        public TimeSpan Elapsed => _stopwatch.Elapsed;

        public void Start()
        {
            StartedAt = DateTimeOffset.Now;
            File.WriteAllText(
                MarkerFilePath,
                JsonSerializer.Serialize(new
                {
                    version = 1,
                    conversationId = ConversationId,
                    startedAt = StartedAt,
                    microphone = Tracks.FirstOrDefault(track => track.Kind == AudioTrackKind.MicrophoneRaw)?.DeviceName,
                    desktopOutput = Tracks.FirstOrDefault(track => track.Kind == AudioTrackKind.DesktopRaw)?.DeviceName
                }));

            _stopwatch.Start();
            foreach (CaptureTrack track in Tracks)
            {
                track.Start();
            }
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            _stopwatch.Stop();
            await Task.WhenAll(Tracks.Select(track => track.StopAsync(cancellationToken)))
                .ConfigureAwait(false);
        }

        public async Task StopIgnoringErrorsAsync(CancellationToken cancellationToken)
        {
            _stopwatch.Stop();
            foreach (CaptureTrack track in Tracks)
            {
                try
                {
                    await track.StopAsync(cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    // Cancellation intentionally discards the recording.
                }
            }
        }

        public IReadOnlyList<AudioMixInput> CreateMixInputs()
        {
            CaptureTrack[] tracksWithData = Tracks.Where(track => track.HasAudioData).ToArray();
            long[] packetTimestamps = tracksWithData
                .Select(track => track.FirstPacketTimestamp)
                .Where(timestamp => timestamp > 0)
                .ToArray();
            long earliestPacket = packetTimestamps.Length == 0 ? 0 : packetTimestamps.Min();

            return tracksWithData
                .Select(track => new AudioMixInput(
                    track.FilePath,
                    earliestPacket > 0 && track.FirstPacketTimestamp > earliestPacket
                        ? TimeSpan.FromSeconds(
                            (track.FirstPacketTimestamp - earliestPacket) / (double)Stopwatch.Frequency)
                        : TimeSpan.Zero))
                .ToArray();
        }

        public void Dispose()
        {
            _stopwatch.Stop();
            foreach (CaptureTrack track in Tracks)
            {
                track.Dispose();
            }
        }
    }

    private sealed class CaptureTrack : IDisposable
    {
        private readonly object _writerGate = new();
        private readonly MMDevice _device;
        private readonly IWaveIn _capture;
        private readonly TaskCompletionSource<Exception?> _stopped =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private WaveFileWriter? _writer;
        private Exception? _writeException;
        private bool _started;
        private bool _disposed;
        private long _firstPacketTimestamp;

        public CaptureTrack(
            AudioTrackKind kind,
            string filePath,
            MMDevice device,
            IWaveIn capture)
        {
            Kind = kind;
            FilePath = filePath;
            DeviceName = device.FriendlyName;
            _device = device;
            _capture = capture;
            _writer = new WaveFileWriter(filePath, capture.WaveFormat);
            _capture.DataAvailable += Capture_DataAvailable;
            _capture.RecordingStopped += Capture_RecordingStopped;
        }

        public AudioTrackKind Kind { get; }

        public string FilePath { get; }

        public string DeviceName { get; }

        public long FirstPacketTimestamp => Interlocked.Read(ref _firstPacketTimestamp);

        public bool HasAudioData => File.Exists(FilePath) && new FileInfo(FilePath).Length > 44;

        public void Start()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _capture.StartRecording();
            _started = true;
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            if (_disposed)
            {
                return;
            }

            if (_started && !_stopped.Task.IsCompleted)
            {
                _capture.StopRecording();
            }
            else if (!_started)
            {
                FinalizeWriter();
                _stopped.TrySetResult(null);
            }

            Exception? stopException = await _stopped.Task
                .WaitAsync(TimeSpan.FromSeconds(10), cancellationToken)
                .ConfigureAwait(false);
            Exception? exception = _writeException ?? stopException;
            if (exception is not null)
            {
                throw new InvalidOperationException(
                    $"Die Audioquelle „{DeviceName}“ wurde unerwartet beendet.",
                    exception);
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _capture.DataAvailable -= Capture_DataAvailable;
            _capture.RecordingStopped -= Capture_RecordingStopped;

            if (_started && !_stopped.Task.IsCompleted)
            {
                try
                {
                    _capture.StopRecording();
                    _stopped.Task.Wait(TimeSpan.FromSeconds(2));
                }
                catch
                {
                    // Best-effort shutdown.
                }
            }

            FinalizeWriter();
            _capture.Dispose();
            _device.Dispose();
        }

        private void Capture_DataAvailable(object? sender, WaveInEventArgs e)
        {
            if (e.BytesRecorded <= 0)
            {
                return;
            }

            Interlocked.CompareExchange(
                ref _firstPacketTimestamp,
                Stopwatch.GetTimestamp(),
                comparand: 0);

            try
            {
                lock (_writerGate)
                {
                    _writer?.Write(e.Buffer, 0, e.BytesRecorded);
                    _writer?.Flush();
                }
            }
            catch (Exception exception)
            {
                _writeException = exception;
                try
                {
                    _capture.StopRecording();
                }
                catch
                {
                    // The stored write exception is the actionable failure.
                }
            }
        }

        private void Capture_RecordingStopped(object? sender, StoppedEventArgs e)
        {
            _started = false;
            FinalizeWriter();
            _stopped.TrySetResult(e.Exception);
        }

        private void FinalizeWriter()
        {
            lock (_writerGate)
            {
                _writer?.Dispose();
                _writer = null;
            }
        }
    }
}
