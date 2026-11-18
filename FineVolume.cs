// FineVolume - finer volume steps at low volume, keeping the normal Windows volume popup.
//
// Windows handles the first volume press of a turn, so its popup shows. While
// the popup is up this program takes the following presses and sets the volume
// itself, so the popup only ever shows the finished number.
//
// Usage: FineVolume.exe [threshold] [fineStep]
//   threshold  volume (0-100) below which fine steps apply. Default 20.
//   fineStep   step size used below the threshold. Default 1.
// At or above the threshold Windows' normal step of 2 is left alone.
// Exit from the tray icon's right-click menu.

using System;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

[Guid("657804FA-D6AD-4496-8A60-352752AF4F89"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IAudioEndpointVolumeCallback
{
    [PreserveSig] int OnNotify(IntPtr data);
}

[Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IAudioEndpointVolume
{
    [PreserveSig] int RegisterControlChangeNotify(IAudioEndpointVolumeCallback cb);
    [PreserveSig] int UnregisterControlChangeNotify(IAudioEndpointVolumeCallback cb);
    [PreserveSig] int GetChannelCount(out uint c);
    [PreserveSig] int SetMasterVolumeLevel(float db, ref Guid ctx);
    [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid ctx);
    [PreserveSig] int GetMasterVolumeLevel(out float db);
    [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
}

[Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMMDevice
{
    [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams,
        [MarshalAs(UnmanagedType.IUnknown)] out object obj);
    [PreserveSig] int OpenPropertyStore(int access, out IntPtr store);
    [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
}

[Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMMDeviceEnumerator
{
    [PreserveSig] int EnumAudioEndpoints(int flow, int mask, out IntPtr devices);
    [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IMMDevice device);
}

[ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
class MMDeviceEnumerator { }

// Owns the audio device. Everything here runs off the UI thread; shared state
// is guarded by `gate`.
public sealed class Audio : IAudioEndpointVolumeCallback
{
    // A press is "live" for this long: Windows' own change for it is corrected.
    const long CorrectWindowMs = 300;
    const int DeviceCheckMs = 3000;

    readonly object gate = new object();
    readonly Stopwatch clock = Stopwatch.StartNew();
    readonly double threshold, fineStep;
    Guid ownChange = Guid.NewGuid(); // tags our own volume changes

    IMMDeviceEnumerator enumerator;
    IAudioEndpointVolume endpoint;
    string deviceId;

    bool known;        // `volume` holds a real reading
    double volume;     // last volume reported, 0-100
    double target;     // volume wanted after the latest press
    double settled;    // volume while Windows has not yet applied its own step
    long pressAt = long.MinValue / 2;

    readonly AutoResetEvent pressed = new AutoResetEvent(false);

    public Audio(double threshold, double fineStep)
    {
        this.threshold = threshold;
        this.fineStep = fineStep;
        var t = new Thread(DeviceLoop);
        t.IsBackground = true;
        t.SetApartmentState(ApartmentState.MTA);
        t.Start();

        var c = new Thread(CorrectLoop);
        c.IsBackground = true;
        c.SetApartmentState(ApartmentState.MTA);
        c.Priority = ThreadPriority.AboveNormal;
        c.Start();
    }

    // How each press is handled, and why:
    //
    // Windows' own step always lands on an even number, so an odd number can
    // only be reached by setting the volume ourselves. But the Windows popup
    // only appears, and stays, when Windows itself sees a volume key: it hides
    // about 2.2 s after the last one (measured), even if the volume changes.
    //
    //  - Even target: nudge the volume by 0.1 (too small to change the number
    //    shown) to just the right side of Windows' rounding, then let Windows
    //    have the key. It lands exactly on target and shows/keeps its popup.
    //    Done whenever the popup is due a refresh.
    //  - Odd target while the popup is up: keep the key from Windows and set
    //    the volume ourselves. The popup follows the change.
    //  - Odd target with the popup hidden: Windows must see the key for the
    //    popup to appear, so it steps to an even number and CorrectLoop puts
    //    it right about 6 ms later (how long this device takes to apply a change).
    //  - Leaving 0: always handled that last way, never by setting the volume
    //    ourselves. On the user's USB audio device, sound stays silent after a
    //    direct set from 0 to 1 even though Windows reports 1% and not muted;
    //    Windows' own step up from 0 brings the sound back.
    //  - Going down to 1 with the popup hidden: Windows' step would land on 0
    //    and the correction would then be that same silent set from 0 to 1.
    //    So the volume is first nudged to just above 3, Windows steps to 2,
    //    and CorrectLoop takes it to 1. The volume never touches 0.
    const long RefreshPopupMs = 800; // let Windows see an even-target key this long after the last
    const long PopupUpMs = 2100;     // popup surely still up this long after a key Windows saw
    const double Nudge = 0.1;
    const int PreSetWaitMs = 40;

    long passAt = long.MinValue / 2; // when Windows last saw a volume key
    bool direct;                     // latest press was taken from Windows: just set `target`
    bool prePending;                 // `preValue` must be set before Windows steps
    double preValue;
    readonly ManualResetEvent preDone = new ManualResetEvent(false);

    bool Live { get { return clock.ElapsedMilliseconds - pressAt <= CorrectWindowMs; } }

    // Called from the keyboard hook. Returns true if the key must be kept from Windows.
    public bool OnPress(bool up)
    {
        bool swallow, waitForPre = false;
        lock (gate)
        {
            long now = clock.ElapsedMilliseconds;
            long sincePass = now - passAt;
            if (!known) { passAt = now; return false; }

            // During a quick turn of the knob, continue from the value we are
            // heading to, not from whatever Windows has momentarily set.
            bool live = Live;
            double from = live ? target : volume;

            bool fine = up ? from < threshold : from <= threshold;
            if (!fine && !live) { passAt = now; return false; } // normal Windows step

            double step = fine ? fineStep : 2;
            double want = Math.Max(0, Math.Min(100, from + (up ? step : -step)));

            bool even = want != from && Math.Abs(want / 2 - Math.Round(want / 2)) < 1e-6;

            if (even && sincePass >= RefreshPopupMs)
            {
                // Windows steps up to `want` from anywhere in (want-3, want-1),
                // and down to it from anywhere in (want+1, want+3) (measured).
                double start = up
                    ? Math.Max(want - 3 + Nudge, Math.Min(want - 1 - Nudge, from))
                    : Math.Min(want + 3 - Nudge, Math.Max(want + 1 + Nudge, from));
                preValue = start;
                prePending = true;
                preDone.Reset();
                settled = start;
                waitForPre = true;
                passAt = now;
                swallow = false;
            }
            else if (sincePass < PopupUpMs && !(up && from <= 0.05))
            {
                direct = true;
                swallow = true;
            }
            else if (!up && want > 0.05 && from < 3 + 0.05)
            {
                // Windows would step down to 0 here, and a set from 0 up to
                // `want` leaves this device silent. Start just above 3 so
                // Windows steps to 2 instead; CorrectLoop then sets `want`.
                double start = want + 2 + Nudge;
                preValue = start;
                prePending = true;
                preDone.Reset();
                settled = start;
                waitForPre = true;
                passAt = now;
                swallow = false;
            }
            else
            {
                if (!live) settled = from;
                passAt = now;
                swallow = false;
            }
            target = want;
            pressAt = now;
        }
        pressed.Set();
        // Windows reads the volume as soon as this hook returns.
        if (waitForPre) preDone.WaitOne(PreSetWaitMs);
        return swallow;
    }

    // Called by Windows on its own thread whenever the volume changes.
    // Only keeps `volume` current; the correcting is done by CorrectLoop.
    public int OnNotify(IntPtr data)
    {
        try
        {
            // AUDIO_VOLUME_NOTIFICATION_DATA: GUID context, BOOL muted, float volume
            float level = (float)Marshal.PtrToStructure(IntPtr.Add(data, 20), typeof(float));
            lock (gate) { volume = Math.Round(level * 100.0, 2); known = true; }
        }
        catch { }
        return 0;
    }

    // Does all the volume setting. After each press it either sets the volume
    // outright (key taken from Windows), or watches as fast as it can and puts
    // the volume on target the moment Windows applies its own step.
    void CorrectLoop()
    {
        const double Eps = 0.05;
        const long TightMs = 60; // Windows normally acts within ~10 ms of a press

        while (true)
        {
            pressed.WaitOne();
            while (true)
            {
                IAudioEndpointVolume ep;
                double want, idle, pre;
                long since;
                bool doDirect, doPre;
                lock (gate)
                {
                    ep = endpoint;
                    want = target;
                    idle = settled;
                    since = clock.ElapsedMilliseconds - pressAt;
                    doDirect = direct; direct = false;
                    doPre = prePending; prePending = false; pre = preValue;
                }

                try
                {
                    if (doPre)
                    {
                        if (ep != null) ep.SetMasterVolumeLevelScalar((float)(pre / 100.0), ref ownChange);
                        preDone.Set();
                        continue;
                    }
                    if (ep == null || since > CorrectWindowMs) break;

                    if (doDirect)
                    {
                        ep.SetMasterVolumeLevelScalar((float)(want / 100.0), ref ownChange);
                        lock (gate) { if (target == want) settled = want; }
                        continue;
                    }

                    float level;
                    if (ep.GetMasterVolumeLevelScalar(out level) != 0) break;
                    double now = Math.Round(level * 100.0, 2);

                    if (Math.Abs(now - idle) > Eps) // Windows has stepped
                    {
                        if (Math.Abs(now - want) > Eps)
                            ep.SetMasterVolumeLevelScalar((float)(want / 100.0), ref ownChange);
                        lock (gate) { if (target == want) settled = want; }
                    }
                }
                catch { preDone.Set(); break; }

                if (since < TightMs) Thread.SpinWait(20); else Thread.Sleep(1);
            }
        }
    }

    // Follows the default output device, so switching outputs keeps working.
    void DeviceLoop()
    {
        while (true)
        {
            try { CheckDevice(); } catch { }
            Thread.Sleep(DeviceCheckMs);
        }
    }

    void CheckDevice()
    {
        if (enumerator == null) enumerator = (IMMDeviceEnumerator)(new MMDeviceEnumerator());

        IMMDevice device;
        string id;
        if (enumerator.GetDefaultAudioEndpoint(0 /*render*/, 0 /*console*/, out device) != 0
            || device.GetId(out id) != 0)
        {
            Detach();
            return;
        }

        if (id != deviceId || endpoint == null)
        {
            Detach();
            Guid iid = typeof(IAudioEndpointVolume).GUID;
            object obj;
            if (device.Activate(ref iid, 23 /*CLSCTX_ALL*/, IntPtr.Zero, out obj) != 0) return;
            var ep = (IAudioEndpointVolume)obj;
            ep.RegisterControlChangeNotify(this);
            lock (gate) { endpoint = ep; deviceId = id; }
        }

        float level;
        if (endpoint.GetMasterVolumeLevelScalar(out level) == 0)
            lock (gate) { volume = Math.Round(level * 100.0, 2); known = true; }
    }

    void Detach()
    {
        IAudioEndpointVolume old;
        lock (gate) { old = endpoint; endpoint = null; deviceId = null; known = false; }
        if (old != null) { try { old.UnregisterControlChangeNotify(this); } catch { } }
    }
}

static class Program
{
    const int WH_KEYBOARD_LL = 13;
    const int WM_KEYDOWN = 0x100, WM_SYSKEYDOWN = 0x104;
    const int VK_VOLUME_DOWN = 0xAE, VK_VOLUME_UP = 0xAF;

    delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr module, uint threadId);
    [DllImport("user32.dll")]
    static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")]
    static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")]
    static extern IntPtr GetModuleHandle(string name);

    static readonly HookProc hookProc = OnHook; // keep the delegate alive
    static IntPtr hook;
    static Audio audio;

    [STAThread]
    static void Main(string[] args)
    {
        bool created;
        using (var mutex = new Mutex(true, "FineVolume_SingleInstance", out created))
        {
            if (!created) return;

            double threshold = 20, fineStep = 1, v;
            if (args.Length > 0 && TryParse(args[0], out v) && v >= 0 && v <= 100) threshold = v;
            if (args.Length > 1 && TryParse(args[1], out v) && v > 0 && v <= 2) fineStep = v;

            audio = new Audio(threshold, fineStep);

            hook = SetWindowsHookEx(WH_KEYBOARD_LL, hookProc, GetModuleHandle(null), 0);
            if (hook == IntPtr.Zero)
            {
                MessageBox.Show("Could not watch the volume keys.", "FineVolume");
                return;
            }

            var menu = new ContextMenuStrip();
            menu.Items.Add("Exit FineVolume", null, delegate { Application.Exit(); });
            var tray = new NotifyIcon
            {
                Icon = SystemIcons.Application,
                Text = "FineVolume: steps of " + fineStep.ToString(CultureInfo.InvariantCulture)
                     + " below " + threshold.ToString(CultureInfo.InvariantCulture),
                ContextMenuStrip = menu,
                Visible = true
            };

            Application.Run();

            tray.Visible = false;
            tray.Dispose();
            UnhookWindowsHookEx(hook);
            GC.KeepAlive(mutex);
        }
    }

    static bool TryParse(string s, out double v)
    {
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
    }

    static IntPtr OnHook(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            int msg = wParam.ToInt32();
            int vk = Marshal.ReadInt32(lParam);
            if (vk == VK_VOLUME_UP || vk == VK_VOLUME_DOWN)
            {
                int i = vk == VK_VOLUME_UP ? 0 : 1;
                if (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN)
                {
                    bool swallow = false;
                    try { swallow = audio.OnPress(vk == VK_VOLUME_UP); } catch { }
                    swallowed[i] = swallow;
                    if (swallow) return (IntPtr)1;
                }
                else if (swallowed[i])
                {
                    // the release of a press Windows never saw
                    swallowed[i] = false;
                    return (IntPtr)1;
                }
            }
        }
        return CallNextHookEx(hook, code, wParam, lParam);
    }

    static readonly bool[] swallowed = new bool[2];
}
