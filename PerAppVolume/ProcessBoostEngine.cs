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
    private readonly List<(AudioSession Session, float Volume, bool Muted)> muted = new();
    private WasapiOut? output;
    private readonly List<NativeLoopbackCapture> captures = new();
    private bool disposed;

    private ProcessBoostEngine(IEnumerable<uint> processIds, float gain) { this.processIds = CollapseRoots(processIds); this.gain = gain; }

    public static Task<ProcessBoostEngine> StartAsync(uint processId, float gain, CancellationToken cancellationToken = default)
        => StartAsync(new[] { processId }, gain, cancellationToken);

    public static Task<ProcessBoostEngine> StartAsync(IEnumerable<uint> processIds, float gain, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var engine = new ProcessBoostEngine(processIds, gain);
        if (engine.processIds.Length == 0) throw new InvalidOperationException("请至少选择一个正在播放声音的程序。");
        try
        {
            engine.SuppressOriginalSessions();
            engine.Open();
            return Task.FromResult(engine);
        }
        catch
        {
            engine.Dispose();
            throw;
        }
    }

    private void SuppressOriginalSessions()
    {
        var treeIds = ProcessTreeIds(processIds);
        foreach (var session in AudioSession.List().Where(x => treeIds.Contains(x.ProcessId)))
        {
            var old = session.GetVolume();
            var oldMute = session.GetMute();
            try
            {
                // Windows process loopback also observes ISimpleAudioVolume
                // mute.  Muting therefore silences the copy as well.  Lower
                // the original session by gain² and compensate the captured
                // stream by gain² instead: the replay is still `gain` times
                // the user's original level, while the direct path is only
                // 1/gain² of it (about -28 dB at 500%).
                if (oldMute) throw new InvalidOperationException("目标会话已经静音");
                var attenuation = Math.Clamp(old / (gain * gain), 0.001f, 1f);
                session.SetMute(false);
                session.SetVolume(attenuation);
                Thread.Sleep(20);
                var confirmedVolume = session.GetVolume();
                if (session.GetMute() || Math.Abs(confirmedVolume - attenuation) > 0.01f)
                    throw new InvalidOperationException("系统未能降低原始音频会话");
                muted.Add((session, old, oldMute));
            }
            catch
            {
                try { session.SetVolume(old); session.SetMute(oldMute); } catch { }
                throw new InvalidOperationException(
                    "无法可靠地降低原始音频，会产生回音。请停止该程序的独占音频/通话模式后重试。"
                );
            }
        }
        if (muted.Count == 0)
            throw new InvalidOperationException("找不到该程序的活动音频会话。请先让程序播放声音，再重试。");
    }

    private static HashSet<uint> ProcessTreeIds(IEnumerable<uint> roots)
    {
        var parent = ProcessParentMap();
        var result = roots.ToHashSet();
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var pair in parent)
                if (result.Contains(pair.Value) && result.Add(pair.Key)) changed = true;
        }
        return result;
    }

    private static uint[] CollapseRoots(IEnumerable<uint> input)
    {
        var roots = input.Distinct().ToHashSet();
        var parent = ProcessParentMap();
        return roots.Where(pid =>
        {
            var current = pid;
            var seen = new HashSet<uint>();
            while (parent.TryGetValue(current, out var p) && p != 0 && seen.Add(current))
            {
                if (roots.Contains(p)) return false;
                current = p;
            }
            return true;
        }).ToArray();
    }

    private static Dictionary<uint, uint> ProcessParentMap()
    {
        var parent = new Dictionary<uint, uint>();
        var snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snapshot != IntPtr.Zero && snapshot != INVALID_HANDLE_VALUE)
        {
            try
            {
                var entry = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
                if (Process32First(snapshot, ref entry))
                {
                    do { parent[entry.th32ProcessID] = entry.th32ParentProcessID; }
                    while (Process32Next(snapshot, ref entry));
                }
            }
            finally { CloseHandle(snapshot); }
        }
        return parent;
    }

    private void Open()
    {
        var format = new WaveFormat(48000, 16, 2);
        var device = GetDefaultNaudioDevice();
        var mixer = new ProcessWaveMixer(format);
        // Give the shared-mode session attenuation time to propagate before
        // capture starts; otherwise the first full-volume packets can leak.
        Thread.Sleep(120);
        output = new WasapiOut(device, AudioClientShareMode.Shared, false, 20);
        output.Init(mixer);
        output.Play();
        foreach (var processId in processIds)
        {
            var buffer = new BufferedWaveProvider(format)
            {
                ReadFully = true,
                DiscardOnBufferOverflow = true,
                BufferDuration = TimeSpan.FromMilliseconds(40)
            };
            mixer.AddInput(buffer);
            var capture = new NativeLoopbackCapture(processId, gain * gain, buffer);
            capture.Start();
            captures.Add(capture);
        }
    }

    private const uint TH32CS_SNAPPROCESS = 0x00000002;
    private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool Process32First(IntPtr snapshot, ref PROCESSENTRY32 entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool Process32Next(IntPtr snapshot, ref PROCESSENTRY32 entry);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct PROCESSENTRY32
    {
        public uint dwSize, cntUsage, th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID, cntThreads, th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
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
        foreach (var (session, volume, wasMuted) in muted)
            try { session.SetVolume(volume); session.SetMute(wasMuted); } catch { }
        muted.Clear();
    }
}

/// Combines simultaneous process streams sample by sample. Appending each
/// stream to one BufferedWaveProvider causes an ever-growing delayed echo.
internal sealed class ProcessWaveMixer : IWaveProvider
{
    private readonly List<BufferedWaveProvider> inputs = new();
    public WaveFormat WaveFormat { get; }
    public ProcessWaveMixer(WaveFormat format) => WaveFormat = format;
    public void AddInput(BufferedWaveProvider input) => inputs.Add(input);

    public int Read(byte[] buffer, int offset, int count)
    {
        Array.Clear(buffer, offset, count);
        if (inputs.Count == 0) return count;
        var mixed = new int[count / 2];
        var scratch = new byte[count];
        foreach (var input in inputs)
        {
            Array.Clear(scratch);
            input.Read(scratch, 0, count);
            for (var i = 0; i < mixed.Length; i++)
                mixed[i] += BitConverter.ToInt16(scratch, i * 2);
        }
        for (var i = 0; i < mixed.Length; i++)
            BitConverter.TryWriteBytes(buffer.AsSpan(offset + i * 2, 2),
                (short)Math.Clamp(mixed[i], short.MinValue, short.MaxValue));
        return count;
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
