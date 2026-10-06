using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace PerAppVolume;

/// Captures one process tree with the Windows Application Loopback API,
/// applies a linear gain to 16-bit PCM samples, and renders the result.
/// The small native helper is MIT-licensed and is shipped beside the app;
/// it does not install a driver or register a system component.
internal sealed class ProcessBoostEngine : IDisposable
{
    private readonly uint[] processIds;
    private readonly float gain;
    private readonly List<(AudioSession Session, float Volume)> muted = new();
    private WasapiOut? output;
    private BufferedWaveProvider? provider;
    private readonly List<NativeLoopbackCapture> captures = new();
    private bool disposed;

    private ProcessBoostEngine(IEnumerable<uint> processIds, float gain) { this.processIds = processIds.Distinct().ToArray(); this.gain = gain; }

    public static Task<ProcessBoostEngine> StartAsync(uint processId, float gain, CancellationToken cancellationToken = default)
        => StartAsync(new[] { processId }, gain, cancellationToken);

    public static Task<ProcessBoostEngine> StartAsync(IEnumerable<uint> processIds, float gain, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var engine = new ProcessBoostEngine(processIds, gain);
        if (engine.processIds.Length == 0) throw new InvalidOperationException("请至少选择一个正在播放声音的程序。");
        try
        {
            engine.MuteTargetSessions();
            engine.Open();
            return Task.FromResult(engine);
        }
        catch
        {
            engine.Dispose();
            throw;
        }
    }

    private void MuteTargetSessions()
    {
        foreach (var session in AudioSession.List().Where(x => processIds.Contains(x.ProcessId)))
        {
            var old = session.GetVolume();
            session.SetVolume(0);
            muted.Add((session, old));
        }
        if (muted.Count == 0)
            throw new InvalidOperationException("找不到该程序的活动音频会话。请先让程序播放声音，再重试。");
    }

    private void Open()
    {
        var format = new WaveFormat(48000, 16, 2);
        var device = GetDefaultNaudioDevice();
        output = new WasapiOut(device, AudioClientShareMode.Shared, false, 50);
        provider = new BufferedWaveProvider(format)
        {
            ReadFully = true,
            DiscardOnBufferOverflow = true,
            BufferDuration = TimeSpan.FromMilliseconds(300)
        };
        output.Init(provider);
        output.Play();
        foreach (var processId in processIds)
        {
            var capture = new NativeLoopbackCapture(processId, gain, provider);
            capture.Start();
            captures.Add(capture);
        }
    }

    private static NAudio.CoreAudioApi.MMDevice GetDefaultNaudioDevice()
    {
        // Program.cs contains local Core Audio declarations for session enumeration.
        // Reuse that endpoint and bridge it to NAudio without invoking its colliding
        // COM coclass.
        var devices = (IMMDeviceEnumerator)new CoreMMDeviceEnumerator();
        devices.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eMultimedia, out var rawDevice);
        var unknown = Marshal.GetIUnknownForObject(rawDevice);
        try
        {
            var naudioInterface = typeof(NAudio.CoreAudioApi.MMDevice).Assembly.GetType("NAudio.CoreAudioApi.Interfaces.IMMDevice")
                ?? throw new InvalidOperationException("NAudio 音频设备接口不可用。");
            var naudioDevice = Marshal.GetTypedObjectForIUnknown(unknown, naudioInterface);
            var ctor = typeof(NAudio.CoreAudioApi.MMDevice).GetConstructor(
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic,
                null, new[] { naudioInterface }, null)
                ?? throw new InvalidOperationException("无法创建默认播放设备对象。");
            return (NAudio.CoreAudioApi.MMDevice)ctor.Invoke(new[] { naudioDevice });
        }
        finally { Marshal.Release(unknown); }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        foreach (var capture in captures) try { capture.Dispose(); } catch { }
        captures.Clear();
        try { output?.Stop(); } catch { }
        output?.Dispose();
        foreach (var (session, volume) in muted)
            try { session.SetVolume(volume); } catch { }
        muted.Clear();
    }
}

/// Managed wrapper around the MIT-licensed ApplicationLoopback.NET native helper.
internal sealed class NativeLoopbackCapture : IDisposable
{
    private delegate void AudioCallback(IntPtr instance, IntPtr data, uint length);
    private delegate void AudioEvent(IntPtr instance);

    [DllImport("ApplicationLoopback.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern IntPtr InitializeCapture(ushort channels, uint sampleRate, ushort bitsPerSample, AudioCallback callback, AudioEvent stopped);
    [DllImport("ApplicationLoopback.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern int StartCaptureAsync(IntPtr capture, uint processId, [MarshalAs(UnmanagedType.I1)] bool includeProcessTree);
    [DllImport("ApplicationLoopback.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern int StopCaptureAsync(IntPtr capture);
    [DllImport("ApplicationLoopback.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern void FreeCapture(IntPtr capture);

    private readonly uint processId;
    private readonly float gain;
    private readonly BufferedWaveProvider provider;
    private readonly AudioCallback onData;
    private readonly AudioEvent onStopped;
    private readonly ManualResetEventSlim stopped = new(false);
    private IntPtr handle;
    private bool started;
    private bool freed;

    public NativeLoopbackCapture(uint processId, float gain, BufferedWaveProvider provider)
    {
        this.processId = processId;
        this.gain = gain;
        this.provider = provider;
        onData = OnData;
        onStopped = OnStopped;
        handle = InitializeCapture(2, 48000, 16, onData, onStopped);
        if (handle == IntPtr.Zero)
            throw new InvalidOperationException("无法初始化 Windows 进程回环音频组件。");
    }

    public void Start()
    {
        var hr = StartCaptureAsync(handle, processId, true);
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
        started = true;
    }

    private void OnData(IntPtr _, IntPtr data, uint length)
    {
        if (data == IntPtr.Zero || length == 0) return;
        var bytes = new byte[length];
        Marshal.Copy(data, bytes, 0, bytes.Length);
        ApplyGain(bytes, gain);
        provider.AddSamples(bytes, 0, bytes.Length);
    }

    private void OnStopped(IntPtr _) => stopped.Set();

    private static void ApplyGain(byte[] data, float gain)
    {
        for (var i = 0; i + 1 < data.Length; i += 2)
        {
            var value = BitConverter.ToInt16(data, i) * gain;
            var clipped = (short)Math.Clamp((int)Math.Round(value), short.MinValue, short.MaxValue);
            BitConverter.TryWriteBytes(data.AsSpan(i, 2), clipped);
        }
    }

    public void Dispose()
    {
        if (freed) return;
        if (started)
        {
            try { StopCaptureAsync(handle); } catch { }
            stopped.Wait(1500);
        }
        try { FreeCapture(handle); } catch { }
        handle = IntPtr.Zero;
        freed = true;
        stopped.Dispose();
        GC.KeepAlive(onData);
        GC.KeepAlive(onStopped);
    }
}
