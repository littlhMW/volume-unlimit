using System;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace PerAppVolume;

internal static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}

internal sealed class MainForm : Form
{
    private static readonly Color Surface = Color.White;
    private static readonly Color Canvas = Color.FromArgb(244, 247, 251);
    private static readonly Color Ink = Color.FromArgb(27, 36, 51);
    private static readonly Color Muted = Color.FromArgb(91, 103, 122);
    private static readonly Color Accent = Color.FromArgb(36, 104, 200);
    private readonly ListBox sessions = new()
    {
        SelectionMode = SelectionMode.MultiSimple,
        IntegralHeight = false,
        BorderStyle = BorderStyle.FixedSingle,
        BackColor = Color.White,
        Dock = DockStyle.Fill,
        Font = new Font("Segoe UI", 10.5F)
    };
    private readonly TrackBar volume = new() { Minimum = 0, Maximum = 100, TickFrequency = 10, Dock = DockStyle.Fill };
    private readonly Label value = new() { AutoSize = true, Text = "—", Anchor = AnchorStyles.Left };
    private readonly NumericUpDown boost = new() { Minimum = 100, Maximum = 1000, Increment = 10, Value = 500, Width = 80 };
    private readonly Button boostButton = new() { Text = "启动增益", AutoSize = true };
    private readonly Button refresh = new() { Text = "刷新", AutoSize = true };
    private readonly Label status = new() { AutoSize = true, ForeColor = Color.FromArgb(70, 78, 90) };
    private readonly Label selectionSummary = new() { AutoSize = true, ForeColor = Color.FromArgb(70, 78, 90), Text = "未选择会话" };
    private readonly System.Windows.Forms.Timer poll = new() { Interval = 1500 };
    private List<AudioSession> items = new();
    private ProcessBoostEngine? boostEngine;
    private bool updatingSessions;

    public MainForm()
    {
        Text = "音量破限 / Volume Unlimit";
        MinimumSize = new Size(760, 620);
        ClientSize = new Size(860, 700);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 10F);
        BackColor = Canvas;
        AutoScaleMode = AutoScaleMode.Font;
        DoubleBuffered = true;

        var title = new Label
        {
            Text = "音量破限",
            AutoSize = true,
            Font = new Font("Segoe UI Semibold", 19F),
            ForeColor = Ink,
            Margin = new Padding(0, 0, 0, 2)
        };
        var subtitle = new Label
        {
            Text = "Volume Unlimit  ·  选择程序，直接调整音量或开启 100–1000% 增益。",
            AutoSize = true,
            ForeColor = Muted,
            Margin = new Padding(0, 0, 0, 0)
        };
        refresh.Click += (_, _) => LoadSessions();
        sessions.SelectedIndexChanged += (_, _) => ShowSelected();
        volume.Scroll += (_, _) => SetSelectedVolume();
        sessions.SelectedIndexChanged += (_, _) => { if (!updatingSessions) StopBoostIfSelectionChanged(); };
        boostButton.Click += async (_, _) => await ToggleBoostAsync();
        poll.Tick += (_, _) => LoadSessions(keepSelection: true);

        refresh.BackColor = Surface;
        refresh.ForeColor = Accent;
        refresh.FlatStyle = FlatStyle.Flat;
        refresh.FlatAppearance.BorderColor = Color.FromArgb(170, 195, 230);
        refresh.FlatAppearance.BorderSize = 1;
        refresh.Padding = new Padding(10, 3, 10, 3);
        boostButton.BackColor = Accent;
        boostButton.ForeColor = Color.White;
        boostButton.FlatStyle = FlatStyle.Flat;
        boostButton.FlatAppearance.BorderSize = 0;
        boostButton.Padding = new Padding(14, 5, 14, 5);
        boostButton.Font = new Font(Font, FontStyle.Bold);
        boostButton.Cursor = Cursors.Hand;
        volume.TickStyle = TickStyle.BottomRight;
        volume.BackColor = Surface;
        boost.BackColor = Surface;
        sessions.ForeColor = Ink;
        // Every click toggles a session, so selecting several programs does not
        // require a keyboard modifier.
        sessions.SelectionMode = SelectionMode.MultiSimple;

        var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = new Padding(0, 0, 0, 8) };
        header.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        header.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        header.Controls.Add(title, 0, 0);
        header.Controls.Add(subtitle, 0, 1);

        var sessionBox = new GroupBox { Text = "① 选择音频会话（点击即可多选）", Dock = DockStyle.Fill, Padding = new Padding(12), ForeColor = Ink, BackColor = Surface, Margin = new Padding(0, 0, 0, 12) };
        var sessionLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2 };
        sessionLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        sessionLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 78));
        sessionLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        sessionLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        sessionLayout.Controls.Add(selectionSummary, 0, 0);
        refresh.Anchor = AnchorStyles.Right;
        sessionLayout.Controls.Add(refresh, 1, 0);
        sessionLayout.Controls.Add(sessions, 0, 1);
        sessionLayout.SetColumnSpan(sessions, 2);
        sessionBox.Controls.Add(sessionLayout);

        var volumeBox = new GroupBox { Text = "② 独立音量（0–100%）", Dock = DockStyle.Fill, Padding = new Padding(12), ForeColor = Ink, BackColor = Surface, Margin = new Padding(0, 0, 0, 12) };
        var volumeLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        volumeLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        volumeLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        volumeLayout.Controls.Add(volume, 0, 0);
        volumeLayout.Controls.Add(value, 1, 0);
        volumeBox.Controls.Add(volumeLayout);

        var boostBox = new GroupBox { Text = "③ 真正增益（实时捕获、放大并重新播放）", Dock = DockStyle.Fill, Padding = new Padding(12), ForeColor = Ink, BackColor = Surface, Margin = new Padding(0, 0, 0, 12) };
        var boostLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2 };
        boostLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        boostLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
        boostLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        boostLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var boostPanel = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, AutoSize = true, Margin = new Padding(0) };
        boostPanel.Controls.Add(new Label { Text = "增益：", AutoSize = true, Margin = new Padding(0, 7, 5, 0) });
        boostPanel.Controls.Add(boost);
        boostPanel.Controls.Add(new Label { Text = "%（100–1000）", AutoSize = true, Margin = new Padding(4, 7, 12, 0) });
        boostPanel.Controls.Add(boostButton);
        boostLayout.Controls.Add(boostPanel, 0, 0);
        boostLayout.SetColumnSpan(boostPanel, 2);
        var boostHint = new Label { Text = "为降低回音，原会话会暂降至增益²比例，捕获副本再补偿。500% 约为 5 倍，满幅声音可能削波；停止后自动恢复。", AutoSize = true, ForeColor = Muted, Dock = DockStyle.Fill, Margin = new Padding(0, 4, 0, 0) };
        boostLayout.Controls.Add(boostHint, 0, 1);
        boostLayout.SetColumnSpan(boostHint, 2);
        boostBox.Controls.Add(boostLayout);

        var note = new Label { Text = "提示：增益只处理当前默认多媒体输出设备，可能有轻微延迟。遇到回音请降低增益或停止后重新启动。", AutoSize = true, ForeColor = Muted, Dock = DockStyle.Fill, Margin = new Padding(0, 2, 0, 0) };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(22, 18, 22, 18), ColumnCount = 1, RowCount = 6, BackColor = Canvas };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 66));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 94));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 132));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        layout.Controls.Add(header, 0, 0);
        layout.Controls.Add(sessionBox, 0, 1);
        layout.Controls.Add(volumeBox, 0, 2);
        layout.Controls.Add(boostBox, 0, 3);
        layout.Controls.Add(status, 0, 4);
        layout.Controls.Add(note, 0, 5);
        Controls.Add(layout);
        Load += (_, _) => { LoadSessions(); poll.Start(); };
        FormClosed += (_, _) => { poll.Stop(); StopBoostIfSelectionChanged(); };
    }

    private void LoadSessions(bool keepSelection = false)
    {
        var old = keepSelection
            ? sessions.SelectedItems.Cast<AudioSession>().Select(a => a.Key).ToHashSet()
            : new HashSet<string>();
        try
        {
            items = AudioSession.List();
            updatingSessions = true;
            sessions.BeginUpdate(); sessions.Items.Clear();
            foreach (var item in items) sessions.Items.Add(item);
            sessions.EndUpdate();
            if (items.Count == 0) status.Text = "没有检测到正在播放的程序；启动音频后点“刷新”。";
            else status.Text = $"检测到 {items.Count} 个音频会话。";
            if (old.Count > 0)
            {
                for (var i = 0; i < items.Count; i++)
                    if (old.Contains(items[i].Key)) sessions.SetSelected(i, true);
            }
            if (sessions.SelectedItems.Count == 0 && items.Count > 0) sessions.SetSelected(0, true);
        }
        catch (Exception ex)
        {
            status.Text = "读取音频会话失败：" + ex.Message;
        }
        finally { updatingSessions = false; }
    }

    private void ShowSelected()
    {
        var selected = sessions.SelectedItems.Cast<AudioSession>().ToList();
        selectionSummary.Text = selected.Count switch
        {
            0 => "未选择会话",
            1 => "已选择 1 个会话",
            _ => $"已选择 {selected.Count} 个会话（音量将同时应用）"
        };
        if (selected.Count == 0) { volume.Value = 0; value.Text = "—"; return; }
        try
        {
            var values = selected.Select(x => x.GetVolume() * 100).ToArray();
            volume.Value = Math.Clamp((int)Math.Round(values.Average()), 0, 100);
            value.Text = values.Min() == values.Max()
                ? $"{values[0]:0}%"
                : $"{values.Min():0}–{values.Max():0}%";
        }
        catch { value.Text = "—"; }
    }

    private void SetSelectedVolume()
    {
        var selected = sessions.SelectedItems.Cast<AudioSession>().ToList();
        if (selected.Count == 0) return;
        var failed = 0;
        foreach (var item in selected)
        {
            try { item.SetVolume(volume.Value / 100f); }
            catch { failed++; }
        }
        value.Text = $"{volume.Value}%";
        status.Text = failed == 0
            ? $"已将 {selected.Count} 个会话设置为 {volume.Value}%。"
            : $"已设置 {selected.Count - failed} 个会话，{failed} 个会话设置失败。";
    }

    private void StopBoostIfSelectionChanged()
    {
        if (boostEngine is null) return;
        boostEngine.Dispose();
        boostEngine = null;
        boostButton.Text = "启动增益";
        status.Text = "增益已停止，原音量已恢复。";
    }

    private async Task ToggleBoostAsync()
    {
        if (boostEngine is not null) { StopBoostIfSelectionChanged(); return; }
        var selected = sessions.SelectedItems.Cast<AudioSession>().ToList();
        if (selected.Count == 0) { status.Text = "请先选择一个或多个正在播放的程序。"; return; }
        try
        {
            boostButton.Enabled = false;
            status.Text = $"正在为 {selected.Count} 个会话启动 {boost.Value}% 增益…";
            boostEngine = await ProcessBoostEngine.StartAsync(
                selected.Select(x => x.ProcessId).Distinct(), (float)boost.Value / 100f);
            boostButton.Text = "停止增益";
            status.Text = $"已对 {selected.Count} 个会话启用 {boost.Value}% 增益。";
        }
        catch (Exception ex)
        {
            boostEngine?.Dispose();
            boostEngine = null;
            status.Text = ex is DllNotFoundException
                ? "增益组件缺失，请重新运行最新安装包。"
                : "增益启动失败：" + ex.Message;
        }
        finally { boostButton.Enabled = true; }
    }
}

internal sealed class AudioSession
{
    private readonly ISimpleAudioVolume volume;
    public string Key { get; }
    public string Display { get; }
    public uint ProcessId { get; }
    private AudioSession(string key, string display, uint processId, ISimpleAudioVolume volume) { Key = key; Display = display; ProcessId = processId; this.volume = volume; }
    public override string ToString() => Display;
    public float GetVolume() { volume.GetMasterVolume(out var v); return v; }
    public void SetVolume(float v) { volume.SetMasterVolume(v, Guid.Empty); }
    public bool GetMute() { volume.GetMute(out var muted); return muted; }
    public void SetMute(bool muted) { volume.SetMute(muted, Guid.Empty); }

    public static List<AudioSession> List()
    {
        var result = new List<AudioSession>();
        var enumerator = (IMMDeviceEnumerator)new CoreMMDeviceEnumerator();
        enumerator.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eMultimedia, out var device);
        var iid = typeof(IAudioSessionManager2).GUID;
        device.Activate(ref iid, CLSCTX.CLSCTX_ALL, IntPtr.Zero, out var managerObject);
        var manager = (IAudioSessionManager2)managerObject;
        manager.GetSessionEnumerator(out var list);
        list.GetCount(out var count);
        for (var i = 0; i < count; i++)
        {
            list.GetSession(i, out var control);
            var c2 = (IAudioSessionControl2)control;
            c2.GetState(out var state);
            if (state != 1) continue;
            c2.GetProcessId(out var pid);
            if (pid == 0) continue;
            try
            {
                using var p = Process.GetProcessById((int)pid);
                var name = p.MainWindowTitle;
                if (string.IsNullOrWhiteSpace(name)) name = p.ProcessName;
                var display = $"{name}  ({p.ProcessName}.exe)";
                var key = $"{pid}:{i}";
                result.Add(new AudioSession(key, display, pid, (ISimpleAudioVolume)control));
            }
            catch { }
        }
        return result;
    }
}

enum EDataFlow { eRender, eCapture, eAll, EDataFlow_enum_count }
enum ERole { eConsole, eMultimedia, eCommunications, ERole_enum_count }
[Flags] enum CLSCTX : uint { CLSCTX_INPROC_SERVER = 0x1, CLSCTX_ALL = 0x17 }

[ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"), ClassInterface(ClassInterfaceType.None)] class CoreMMDeviceEnumerator { }

[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDeviceEnumerator
{
    void EnumAudioEndpoints(EDataFlow dataFlow, uint stateMask, out object devices);
    void GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice endpoint);
    void GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
    void RegisterEndpointNotificationCallback(IntPtr client); void UnregisterEndpointNotificationCallback(IntPtr client);
}
[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDevice
{
    void Activate(ref Guid iid, CLSCTX clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object interfacePointer);
    void OpenPropertyStore(uint access, out object properties); void GetId([MarshalAs(UnmanagedType.LPWStr)] out string id); void GetState(out uint state);
}
[ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioSessionManager2
{
    void GetAudioSessionControl(ref Guid audioSessionGuid, uint streamFlags, out IAudioSessionControl sessionControl);
    void GetSimpleAudioVolume(ref Guid audioSessionGuid, uint streamFlags, out ISimpleAudioVolume audioVolume);
    void GetSessionEnumerator(out IAudioSessionEnumerator sessionEnum);
    void RegisterSessionNotification(IntPtr client); void UnregisterSessionNotification(IntPtr client); void RegisterDuckNotification([MarshalAs(UnmanagedType.LPWStr)] string sessionId, IntPtr client); void UnregisterDuckNotification(IntPtr client);
}
[ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] interface IAudioSessionEnumerator { void GetCount(out int count); void GetSession(int sessionCount, out IAudioSessionControl session); }
[ComImport, Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] interface IAudioSessionControl { void GetState(out int state); void GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string displayName); void SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string value, ref Guid eventContext); void GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string iconPath); void SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string value, ref Guid eventContext); void GetGroupingParam(out Guid groupingId); void SetGroupingParam(ref Guid groupingId, ref Guid eventContext); void RegisterAudioSessionNotification(IntPtr client); void UnregisterAudioSessionNotification(IntPtr client); }
[ComImport, Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] interface IAudioSessionControl2 { void GetState(out int state); void GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string displayName); void SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string value, ref Guid eventContext); void GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string iconPath); void SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string value, ref Guid eventContext); void GetGroupingParam(out Guid groupingId); void SetGroupingParam(ref Guid groupingId, ref Guid eventContext); void RegisterAudioSessionNotification(IntPtr client); void UnregisterAudioSessionNotification(IntPtr client); void GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id); void GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string instanceId); void GetProcessId(out uint processId); void IsSystemSoundsSession(); void SetDuckingPreference(bool optOut); }
[ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] interface ISimpleAudioVolume { void SetMasterVolume(float level, Guid eventContext); void GetMasterVolume(out float level); void SetMute(bool mute, Guid eventContext); void GetMute(out bool mute); }
