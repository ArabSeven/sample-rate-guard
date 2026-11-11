// Sample Rate Guard
// A tiny, silent, event-driven Windows tray-less utility that notices when something changes your
// audio device's sample rate, tells you what did it (once), and optionally puts it back.
//
// https://github.com/ArabSeven/sample-rate-guard   -   MIT License
//
// Design notes
//  * No polling. Windows calls us (IMMNotificationClient) on audio endpoint changes; we debounce
//    and check. Between events the process sleeps.
//  * Two kinds of "rate" are watched:
//      1. The Windows shared-mode format of selected endpoints (works for any device / driver).
//      2. The hardware rate of interfaces using a Thesycon-based USB audio driver (UA Volt and
//         many others), discovered automatically from the ASIO driver registration.
//  * Restores via the vendor driver API (hardware) and/or the same interface the Windows Sound
//    control panel uses (IPolicyConfig) for the Windows format.
//  * Never fights a DAW: while a program from DeferTo is running, it only notifies, and restores
//    once that program exits.
//
// Builds with the C# compiler that ships with Windows (.NET Framework 4.x) - see build.cmd.
// C# 5 syntax on purpose (that compiler's language level).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32;

[assembly: System.Reflection.AssemblyTitle("Sample Rate Guard")]
[assembly: System.Reflection.AssemblyProduct("Sample Rate Guard")]
[assembly: System.Reflection.AssemblyDescription("Notices and undoes unwanted audio sample-rate changes on Windows.")]
[assembly: System.Reflection.AssemblyVersion("1.0.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.0.0.0")]

namespace SampleRateGuard
{
    // =====================================================================================
    // Configuration (SampleRateGuard.ini next to the exe)
    // =====================================================================================
    class Config
    {
        public uint TargetRate = 48000;
        public string Devices = "default";
        public bool Restore = true;
        public bool Notify = true;
        public string[] DeferTo = new string[] {
            "Sonar", "Cakewalk", "reaper", "Ableton Live*", "FL64", "FL", "Cubase*", "Nuendo*",
            "Studio One", "Bitwig Studio", "ProTools", "Reason", "Waveform*", "Mixcraft*", "Ardour*" };
        public string DriverApi = "auto";
        public int MaxRestores = 3;
        public int RestoreWindowSeconds = 120;
        public int DebounceMs = 1500;

        public const string DefaultIni =
@"; Sample Rate Guard settings. Edit, then restart the guard (or sign out/in).
; Lines starting with ; are comments.

[General]
; The sample rate you want to keep (Hz). Common values: 44100, 48000, 88200, 96000.
TargetRate=48000

; Which Windows audio devices to watch:
;   default            = the current default playback + default recording device
;   all                = every active audio device (not recommended: Bluetooth headsets etc.
;                        often run at other rates)
;   Volt, Scarlett     = comma-separated parts of device names (as shown in Sound settings)
Devices=default

; Put the target rate back automatically (true) or only notify (false).
Restore=true

; Show a Windows notification (once per incident) saying what changed the rate.
Notify=true

; Programs (process names, without .exe; * = prefix wildcard) that are allowed to run another
; rate. While one of them is running the guard only notifies; it restores after it closes.
DeferTo=Sonar,Cakewalk,reaper,Ableton Live*,FL64,FL,Cubase*,Nuendo*,Studio One,Bitwig Studio,ProTools,Reason,Waveform*,Mixcraft*,Ardour*

; Vendor driver API for reading/setting the interface hardware rate:
;   auto  = detect Thesycon-based USB audio drivers from the ASIO registrations
;   none  = only watch the Windows device format
;   C:\path\to\SomethingUsbAudioapi_x64.dll = use this API DLL
DriverApi=auto

; Anti-fighting: if the rate is changed back more than MaxRestores times within
; RestoreWindowSeconds, the guard stops restoring until the rate returns to target on its own.
MaxRestores=3
RestoreWindowSeconds=120

; How long to wait for a burst of device events to settle before checking (milliseconds).
DebounceMs=1500
";

        public static Config Load(string path)
        {
            var c = new Config();
            if (!File.Exists(path))
            {
                try { File.WriteAllText(path, DefaultIni, Encoding.UTF8); } catch { }
                return c;
            }
            foreach (string raw in File.ReadAllLines(path))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith(";") || line.StartsWith("#") || line.StartsWith("[")) continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string k = line.Substring(0, eq).Trim().ToLowerInvariant();
                string v = line.Substring(eq + 1).Trim();
                int sc = v.IndexOf(" ;"); if (sc >= 0) v = v.Substring(0, sc).Trim();
                uint u; int i;
                switch (k)
                {
                    case "targetrate": if (uint.TryParse(v, out u) && u >= 8000) c.TargetRate = u; break;
                    case "devices": if (v.Length > 0) c.Devices = v; break;
                    case "restore": c.Restore = IsTrue(v); break;
                    case "notify": c.Notify = IsTrue(v); break;
                    case "deferto": c.DeferTo = SplitList(v); break;
                    case "driverapi": if (v.Length > 0) c.DriverApi = v; break;
                    case "maxrestores": if (int.TryParse(v, out i) && i >= 1) c.MaxRestores = i; break;
                    case "restorewindowseconds": if (int.TryParse(v, out i) && i >= 10) c.RestoreWindowSeconds = i; break;
                    case "debouncems": if (int.TryParse(v, out i) && i >= 200) c.DebounceMs = i; break;
                }
            }
            return c;
        }

        static bool IsTrue(string v) { v = v.ToLowerInvariant(); return v == "true" || v == "1" || v == "yes" || v == "on"; }

        public static string[] SplitList(string v)
        {
            var l = new List<string>();
            foreach (string s in v.Split(',')) { string t = s.Trim(); if (t.Length > 0) l.Add(t); }
            return l.ToArray();
        }
    }

    // =====================================================================================
    // Logging
    // =====================================================================================
    static class Log
    {
        public static string FilePath;

        public static void Write(string line)
        {
            if (FilePath == null) return;
            try
            {
                if (File.Exists(FilePath) && new FileInfo(FilePath).Length > 2 * 1024 * 1024)
                {
                    File.Copy(FilePath, FilePath + ".old", true);   // keep one previous log
                    File.Delete(FilePath);
                }
                File.AppendAllText(FilePath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + line + Environment.NewLine, Encoding.UTF8);
            }
            catch { }
        }
    }

    // =====================================================================================
    // Windows Core Audio interop (minimal)
    // =====================================================================================
    [StructLayout(LayoutKind.Sequential)]
    struct PropertyKey
    {
        public Guid fmtid; public int pid;
        public PropertyKey(string g, int p) { fmtid = new Guid(g); pid = p; }
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    struct PropVariant
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public IntPtr pointer;     // VT_LPWSTR
        [FieldOffset(8)] public uint blobSize;      // VT_BLOB
        [FieldOffset(16)] public IntPtr blobData;   // VT_BLOB
    }

    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPropertyStore
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetAt(int index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore store);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out int state);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int Item(int index, out IMMDevice device);
    }

    [ComImport, Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMNotificationClient
    {
        void OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string id, int state);
        void OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string id);
        void OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string id);
        void OnDefaultDeviceChanged(int flow, int role, [MarshalAs(UnmanagedType.LPWStr)] string id);
        void OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string id, PropertyKey key);
    }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int flow, int stateMask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IMMDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IMMNotificationClient client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IMMNotificationClient client);
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class MMDeviceEnumeratorCom { }

    // Audio sessions (to find which apps are using a device)
    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionManager2
    {
        [PreserveSig] int GetAudioSessionControl(IntPtr sessionGuid, int flags, out IntPtr control);
        [PreserveSig] int GetSimpleAudioVolume(IntPtr sessionGuid, int flags, out IntPtr volume);
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator sessions);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetSession(int index, [MarshalAs(UnmanagedType.IUnknown)] out object session);
    }

    [ComImport, Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionControl2
    {
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName(out IntPtr name);
        [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr ctx);
        [PreserveSig] int GetIconPath(out IntPtr path);
        [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, IntPtr ctx);
        [PreserveSig] int GetGroupingParam(out Guid g);
        [PreserveSig] int SetGroupingParam(ref Guid g, IntPtr ctx);
        [PreserveSig] int RegisterAudioSessionNotification(IntPtr n);
        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr n);
        [PreserveSig] int GetSessionIdentifier(out IntPtr id);
        [PreserveSig] int GetSessionInstanceIdentifier(out IntPtr id);
        [PreserveSig] int GetProcessId(out uint pid);
    }

    // IPolicyConfig - undocumented, used by the Windows Sound control panel to change a device's
    // default format. Interface layout for Windows 10/11.
    [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string id, out IntPtr format);
        [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, int defaultFormat, out IntPtr format);
        [PreserveSig] int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id);
        [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr endpointFormat, IntPtr mixFormat);
    }

    [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")] class PolicyConfigCom { }

    static class Native
    {
        [DllImport("ole32.dll")] public static extern int PropVariantClear(ref PropVariant pv);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] public static extern IntPtr LoadLibrary(string path);
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi)] public static extern IntPtr GetProcAddress(IntPtr module, string name);
    }

    class Endpoint
    {
        public string Id;
        public string Name;
        public bool Capture;
        public uint Rate;        // Windows shared-mode device format rate, 0 = unknown
        public override string ToString() { return string.Format("{0} [{1}]", Name, Capture ? "recording" : "playback"); }
    }

    static class Audio
    {
        static readonly PropertyKey PKEY_FriendlyName = new PropertyKey("a45c254e-df1c-4efd-8020-67d146a850e0", 14);
        static readonly PropertyKey PKEY_DeviceFormat = new PropertyKey("f19f064d-082c-4e27-bc73-6882a1bb8e4c", 0);
        public static readonly Guid FmtDeviceFormat = new Guid("f19f064d-082c-4e27-bc73-6882a1bb8e4c");
        const int DEVICE_STATE_ACTIVE = 1;
        const int eRender = 0, eCapture = 1, eAll = 2, eConsole = 0;

        public static IMMDeviceEnumerator NewEnumerator() { return (IMMDeviceEnumerator)new MMDeviceEnumeratorCom(); }

        static Endpoint Describe(IMMDevice d, bool capture)
        {
            var e = new Endpoint();
            e.Capture = capture;
            d.GetId(out e.Id);
            IPropertyStore ps;
            if (d.OpenPropertyStore(0, out ps) != 0) return e;
            try
            {
                PropertyKey k = PKEY_FriendlyName; PropVariant v;
                if (ps.GetValue(ref k, out v) == 0)
                {
                    if (v.vt == 31 && v.pointer != IntPtr.Zero) e.Name = Marshal.PtrToStringUni(v.pointer);
                    Native.PropVariantClear(ref v);
                }
                k = PKEY_DeviceFormat;
                if (ps.GetValue(ref k, out v) == 0)
                {
                    if (v.vt == 65 && v.blobData != IntPtr.Zero && v.blobSize >= 16) e.Rate = (uint)Marshal.ReadInt32(v.blobData, 4);
                    Native.PropVariantClear(ref v);
                }
            }
            finally { Marshal.ReleaseComObject(ps); }
            if (e.Name == null) e.Name = e.Id;
            return e;
        }

        public static List<Endpoint> ActiveEndpoints(IMMDeviceEnumerator en)
        {
            var list = new List<Endpoint>();
            foreach (int flow in new int[] { eRender, eCapture })
            {
                IMMDeviceCollection col;
                if (en.EnumAudioEndpoints(flow, DEVICE_STATE_ACTIVE, out col) != 0) continue;
                int n; col.GetCount(out n);
                for (int i = 0; i < n; i++)
                {
                    IMMDevice d;
                    if (col.Item(i, out d) != 0) continue;
                    try { list.Add(Describe(d, flow == eCapture)); } catch { }
                    Marshal.ReleaseComObject(d);
                }
                Marshal.ReleaseComObject(col);
            }
            return list;
        }

        public static List<string> DefaultIds(IMMDeviceEnumerator en)
        {
            var ids = new List<string>();
            foreach (int flow in new int[] { eRender, eCapture })
            {
                IMMDevice d;
                if (en.GetDefaultAudioEndpoint(flow, eConsole, out d) == 0)
                {
                    string id; d.GetId(out id); ids.Add(id);
                    Marshal.ReleaseComObject(d);
                }
            }
            return ids;
        }

        // Process ids with an active audio session on this endpoint (shared-mode apps).
        public static List<int> ActiveSessionPids(IMMDeviceEnumerator en, string endpointId)
        {
            var pids = new List<int>();
            IMMDevice d;
            if (en.GetDevice(endpointId, out d) != 0) return pids;
            try
            {
                Guid iid = typeof(IAudioSessionManager2).GUID; object o;
                if (d.Activate(ref iid, 23, IntPtr.Zero, out o) != 0 || o == null) return pids;
                var mgr = (IAudioSessionManager2)o;
                IAudioSessionEnumerator se;
                if (mgr.GetSessionEnumerator(out se) == 0)
                {
                    int n; se.GetCount(out n);
                    for (int i = 0; i < n; i++)
                    {
                        object so;
                        if (se.GetSession(i, out so) != 0 || so == null) continue;
                        try
                        {
                            var s2 = so as IAudioSessionControl2;
                            if (s2 == null) continue;
                            int state; uint pid;
                            if (s2.GetState(out state) == 0 && state == 1 && s2.GetProcessId(out pid) == 0 && pid != 0 && !pids.Contains((int)pid))
                                pids.Add((int)pid);
                        }
                        finally { Marshal.ReleaseComObject(so); }
                    }
                    Marshal.ReleaseComObject(se);
                }
                Marshal.ReleaseComObject(mgr);
            }
            catch { }
            finally { Marshal.ReleaseComObject(d); }
            return pids;
        }

        // Change the Windows default format of an endpoint to `rate`, keeping bit depth/channels.
        public static int SetEndpointRate(string id, uint rate)
        {
            IPolicyConfig pc = null; IntPtr dev = IntPtr.Zero, mix = IntPtr.Zero, newDev = IntPtr.Zero, newMix = IntPtr.Zero;
            try
            {
                pc = (IPolicyConfig)new PolicyConfigCom();
                int hr = pc.GetDeviceFormat(id, 0, out dev); if (hr != 0) return hr;
                hr = pc.GetMixFormat(id, out mix); if (hr != 0) mix = IntPtr.Zero;
                newDev = CloneWithRate(dev, rate);
                newMix = mix != IntPtr.Zero ? CloneWithRate(mix, rate) : IntPtr.Zero;
                return pc.SetDeviceFormat(id, newDev, newMix != IntPtr.Zero ? newMix : newDev);
            }
            catch (Exception ex) { return Marshal.GetHRForException(ex); }
            finally
            {
                if (dev != IntPtr.Zero) Marshal.FreeCoTaskMem(dev);
                if (mix != IntPtr.Zero) Marshal.FreeCoTaskMem(mix);
                if (newDev != IntPtr.Zero) Marshal.FreeHGlobal(newDev);
                if (newMix != IntPtr.Zero) Marshal.FreeHGlobal(newMix);
                if (pc != null) Marshal.ReleaseComObject(pc);
            }
        }

        static IntPtr CloneWithRate(IntPtr wfx, uint rate)
        {
            int cbSize = Marshal.ReadInt16(wfx, 16);
            int total = 18 + Math.Max(0, cbSize);
            byte[] b = new byte[total];
            Marshal.Copy(wfx, b, 0, total);
            int blockAlign = BitConverter.ToUInt16(b, 12);
            BitConverter.GetBytes(rate).CopyTo(b, 4);
            BitConverter.GetBytes((uint)(rate * blockAlign)).CopyTo(b, 8);
            IntPtr p = Marshal.AllocHGlobal(total);
            Marshal.Copy(b, 0, p, total);
            return p;
        }
    }

    class EndpointListener : IMMNotificationClient
    {
        readonly Action poke;
        public EndpointListener(Action poke) { this.poke = poke; }
        public void OnDeviceStateChanged(string id, int state) { poke(); }
        public void OnDeviceAdded(string id) { poke(); }
        public void OnDeviceRemoved(string id) { poke(); }
        public void OnDefaultDeviceChanged(int flow, int role, string id) { if (role == 0) poke(); }
        public void OnPropertyValueChanged(string id, PropertyKey key) { if (key.fmtid == Audio.FmtDeviceFormat) poke(); }
    }

    // =====================================================================================
    // ASIO driver registry + Thesycon (TUSBAUDIO) hardware-rate API
    // =====================================================================================
    class AsioDriver
    {
        public string Name;
        public string DllPath;
        public string DllFile { get { return Path.GetFileName(DllPath ?? "").ToLowerInvariant(); } }
    }

    static class AsioRegistry
    {
        public static List<AsioDriver> Drivers()
        {
            var list = new List<AsioDriver>();
            foreach (RegistryView view in new RegistryView[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
                    using (var asio = hklm.OpenSubKey(@"SOFTWARE\ASIO"))
                    {
                        if (asio == null) continue;
                        foreach (string name in asio.GetSubKeyNames())
                        {
                            using (var k = asio.OpenSubKey(name))
                            {
                                string clsid = k == null ? null : k.GetValue("CLSID") as string;
                                if (string.IsNullOrEmpty(clsid)) continue;
                                using (var cls = hklm.OpenSubKey(@"SOFTWARE\Classes\CLSID\" + clsid + @"\InprocServer32"))
                                {
                                    string path = cls == null ? null : cls.GetValue("") as string;
                                    if (string.IsNullOrEmpty(path)) continue;
                                    bool dup = false;
                                    foreach (var d in list) if (string.Equals(d.DllPath, path, StringComparison.OrdinalIgnoreCase)) dup = true;
                                    if (!dup) { var a = new AsioDriver(); a.Name = name; a.DllPath = path; list.Add(a); }
                                }
                            }
                        }
                    }
                }
                catch { }
            }
            return list;
        }
    }

    // Thesycon USB Audio driver API, loaded dynamically from the vendor's *api_x64.dll.
    class ThesyconApi
    {
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate uint FnVoid();
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate uint FnOpen(uint index, out IntPtr handle);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate uint FnHandle(IntPtr handle);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate uint FnGetRate(IntPtr handle, out uint rate);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate uint FnSetRate(IntPtr handle, uint rate);

        public string Name;      // e.g. the ASIO driver name
        public string DllPath;
        FnVoid enumerate, count; FnOpen open; FnHandle close; FnGetRate getRate; FnSetRate setRate;
        readonly object gate = new object();

        public static ThesyconApi TryLoad(string dllPath, string name)
        {
            if (!File.Exists(dllPath)) return null;
            IntPtr m = Native.LoadLibrary(dllPath);
            if (m == IntPtr.Zero) return null;
            var t = new ThesyconApi();
            t.Name = name; t.DllPath = dllPath;
            t.enumerate = Fn<FnVoid>(m, "TUSBAUDIO_EnumerateDevices");
            t.count = Fn<FnVoid>(m, "TUSBAUDIO_GetDeviceCount");
            t.open = Fn<FnOpen>(m, "TUSBAUDIO_OpenDeviceByIndex");
            t.close = Fn<FnHandle>(m, "TUSBAUDIO_CloseDevice");
            t.getRate = Fn<FnGetRate>(m, "TUSBAUDIO_GetCurrentSampleRate");
            t.setRate = Fn<FnSetRate>(m, "TUSBAUDIO_SetSampleRate");
            if (t.enumerate == null || t.count == null || t.open == null || t.close == null || t.getRate == null || t.setRate == null) return null;
            return t;
        }

        static T Fn<T>(IntPtr module, string name) where T : class
        {
            IntPtr p = Native.GetProcAddress(module, name);
            return p == IntPtr.Zero ? null : (T)(object)Marshal.GetDelegateForFunctionPointer(p, typeof(T));
        }

        // Thesycon drivers ship "<prefix>asio_x64.dll" next to "<prefix>api_x64.dll".
        public static List<ThesyconApi> Discover(Config cfg, List<AsioDriver> asio)
        {
            var found = new List<ThesyconApi>();
            string mode = cfg.DriverApi.Trim();
            if (mode.Equals("none", StringComparison.OrdinalIgnoreCase)) return found;
            if (!mode.Equals("auto", StringComparison.OrdinalIgnoreCase))
            {
                var t = TryLoad(mode, Path.GetFileNameWithoutExtension(mode));
                if (t != null) found.Add(t); else Log.Write("DriverApi '" + mode + "' could not be loaded (not a Thesycon USB audio API DLL?).");
                return found;
            }
            if (!Environment.Is64BitProcess) return found;
            foreach (var a in asio)
            {
                string p = a.DllPath;
                string lower = p.ToLowerInvariant();
                string candidate = null;
                if (lower.EndsWith("asio_x64.dll")) candidate = p.Substring(0, p.Length - "asio_x64.dll".Length) + "api_x64.dll";
                else if (lower.EndsWith("asio64.dll")) candidate = p.Substring(0, p.Length - "asio64.dll".Length) + "api64.dll";
                if (candidate == null || !File.Exists(candidate)) continue;
                bool dup = false;
                foreach (var f in found) if (string.Equals(f.DllPath, candidate, StringComparison.OrdinalIgnoreCase)) dup = true;
                if (dup) continue;
                try { var t = TryLoad(candidate, a.Name); if (t != null) found.Add(t); } catch { }
            }
            return found;
        }

        public int DeviceCount()
        {
            lock (gate) { try { if (enumerate() != 0) return 0; return (int)count(); } catch { return 0; } }
        }

        public uint GetRate(int index)
        {
            lock (gate)
            {
                try
                {
                    if (enumerate() != 0 || count() <= index) return 0;
                    IntPtr h; if (open((uint)index, out h) != 0) return 0;
                    try { uint r; return getRate(h, out r) == 0 ? r : 0; } finally { close(h); }
                }
                catch { return 0; }
            }
        }

        public uint SetRate(int index, uint rate)
        {
            lock (gate)
            {
                try
                {
                    if (enumerate() != 0 || count() <= index) return 0xFFFFFFFF;
                    IntPtr h; uint s = open((uint)index, out h); if (s != 0) return s;
                    try { return setRate(h, rate); } finally { close(h); }
                }
                catch { return 0xFFFFFFFE; }
            }
        }
    }

    // =====================================================================================
    // Notifications (Windows toast, "important" so it can pass Do Not Disturb if allowed)
    // =====================================================================================
    static class Toast
    {
        public const string AppId = "SampleRateGuard";

        public static void RegisterAppId()
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(@"Software\Classes\AppUserModelId\" + AppId))
                {
                    k.SetValue("DisplayName", "Sample Rate Guard");
                    k.SetValue("IconUri", Path.Combine(Environment.SystemDirectory, "SndVol.exe"));
                }
            }
            catch { }
        }

        static string Esc(string s) { return System.Security.SecurityElement.Escape(s ?? ""); }

        public static void Show(string title, string text)
        {
            try
            {
                var xml = new Windows.Data.Xml.Dom.XmlDocument();
                xml.LoadXml("<toast scenario='urgent' duration='long'><visual><binding template='ToastGeneric'><text>" + Esc(title) +
                            "</text><text>" + Esc(text) + "</text></binding></visual><audio src='ms-winsoundevent:Notification.Default'/></toast>");
                Windows.UI.Notifications.ToastNotificationManager.CreateToastNotifier(AppId)
                    .Show(new Windows.UI.Notifications.ToastNotification(xml));
                Log.Write("notification shown: " + title);
            }
            catch (Exception e) { Log.Write("notification failed: " + e.Message); }
        }
    }

    // =====================================================================================
    // The guard
    // =====================================================================================
    class Watched
    {
        public string Key;       // stable identity for this check
        public string Label;     // human readable
        public uint Rate;
        public Endpoint Endpoint;     // when it's a Windows endpoint
        public ThesyconApi Api; public int Index;   // when it's interface hardware
    }

    class Guard
    {
        readonly Config cfg;
        readonly List<AsioDriver> asio;
        readonly List<ThesyconApi> apis;
        readonly IMMDeviceEnumerator en;
        readonly EndpointListener listener;
        readonly Timer debounce;
        readonly object gate = new object();
        readonly HashSet<int> watchedDeferred = new HashSet<int>();
        readonly HashSet<string> unsupported = new HashSet<string>();
        readonly List<DateTime> restores = new List<DateTime>();
        bool inIncident, notified, gaveUp;

        static readonly string[] Noise = { "svchost", "conhost", "dllhost", "WmiPrvSE", "SearchProtocolHost", "SearchFilterHost",
            "RuntimeBroker", "backgroundTaskHost", "smartscreen", "sppsvc", "taskhostw", "SampleRateGuard", "cmd", "consent",
            "MoUsoCoreWorker", "TiWorker", "TrustedInstaller", "CompPkgSrv", "SecurityHealthService", "audiodg" };

        public Guard(Config cfg)
        {
            this.cfg = cfg;
            asio = AsioRegistry.Drivers();
            apis = ThesyconApi.Discover(cfg, asio);
            en = Audio.NewEnumerator();
            listener = new EndpointListener(Poke);
            debounce = new Timer(delegate { Check("device change"); }, null, Timeout.Infinite, Timeout.Infinite);
        }

        public string Describe()
        {
            var sb = new StringBuilder();
            sb.AppendFormat("target {0} Hz, devices '{1}', restore={2}, notify={3}", cfg.TargetRate, cfg.Devices, cfg.Restore, cfg.Notify);
            foreach (var w in Collect()) sb.AppendFormat("{0}    watching: {1} = {2} Hz", Environment.NewLine, w.Label, w.Rate);
            if (apis.Count == 0) sb.Append(Environment.NewLine + "    (no Thesycon-based interface driver found; watching Windows device formats only)");
            sb.Append(Environment.NewLine + "    ASIO drivers: ");
            var names = new List<string>(); foreach (var a in asio) if (!names.Contains(a.Name)) names.Add(a.Name);
            sb.Append(names.Count > 0 ? string.Join(", ", names.ToArray()) : "none");
            return sb.ToString();
        }

        public void Start()
        {
            Log.Write("Sample Rate Guard started. " + Describe());
            int hr = en.RegisterEndpointNotificationCallback(listener);
            if (hr != 0) Log.Write(string.Format("Could not register for audio device notifications (0x{0:X8}).", hr));
            Check("startup");
        }

        void Poke() { debounce.Change(cfg.DebounceMs, Timeout.Infinite); }

        bool DeviceSelected(Endpoint e, List<string> defaults)
        {
            string sel = cfg.Devices.Trim();
            if (sel.Equals("all", StringComparison.OrdinalIgnoreCase)) return true;
            if (sel.Equals("default", StringComparison.OrdinalIgnoreCase)) return defaults.Contains(e.Id);
            foreach (string part in Config.SplitList(sel))
            {
                if (part.Equals("default", StringComparison.OrdinalIgnoreCase) && defaults.Contains(e.Id)) return true;
                if (e.Name.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        List<Watched> Collect()
        {
            var list = new List<Watched>();
            foreach (var api in apis)
            {
                int n = api.DeviceCount();
                for (int i = 0; i < n; i++)
                {
                    var w = new Watched();
                    w.Api = api; w.Index = i; w.Rate = api.GetRate(i);
                    w.Key = "hw:" + api.DllPath + "#" + i;
                    w.Label = api.Name + (n > 1 ? " #" + (i + 1) : "") + " (interface hardware)";
                    if (w.Rate != 0) list.Add(w);
                }
            }
            try
            {
                var defaults = Audio.DefaultIds(en);
                foreach (var e in Audio.ActiveEndpoints(en))
                {
                    if (e.Rate == 0 || !DeviceSelected(e, defaults) || unsupported.Contains(e.Id)) continue;
                    var w = new Watched();
                    w.Endpoint = e; w.Rate = e.Rate; w.Key = "ep:" + e.Id; w.Label = e.ToString();
                    list.Add(w);
                }
            }
            catch (Exception ex) { Log.Write("endpoint scan failed: " + ex.Message); }
            return list;
        }

        Process[] RunningDeferred()
        {
            var l = new List<Process>();
            foreach (Process p in Process.GetProcesses())
            {
                string n = p.ProcessName;
                foreach (string pat in cfg.DeferTo)
                {
                    bool m = pat.EndsWith("*")
                        ? n.StartsWith(pat.TrimEnd('*'), StringComparison.OrdinalIgnoreCase)
                        : n.Equals(pat, StringComparison.OrdinalIgnoreCase);
                    if (m) { l.Add(p); break; }
                }
            }
            return l.ToArray();
        }

        void Check(string why)
        {
            lock (gate)
            {
                List<Watched> all = Collect();
                var off = new List<Watched>();
                foreach (var w in all) if (w.Rate != cfg.TargetRate) off.Add(w);

                if (off.Count == 0)
                {
                    if (inIncident) Log.Write(string.Format("All watched devices back at {0} Hz.", cfg.TargetRate));
                    inIncident = notified = gaveUp = false; restores.Clear();
                    return;
                }

                inIncident = true;
                Process[] deferred = RunningDeferred();

                if (!notified)
                {
                    string detail;
                    string culprit = FindCulprit(off, deferred, out detail);
                    var what = new List<string>();
                    foreach (var w in off) what.Add(string.Format("{0}: {1} Hz", w.Label, w.Rate));
                    string action = !cfg.Restore ? "Not restoring (Restore=false)."
                        : deferred.Length > 0 ? string.Format("{0} is open, so it's left alone; {1} Hz will be restored when it closes.", deferred[0].ProcessName, cfg.TargetRate)
                        : string.Format("Restoring {0} Hz now.", cfg.TargetRate);
                    Log.Write(string.Format("RATE CHANGED ({0}): {1}. Caused by: {2}. {3}{4}", why, string.Join("; ", what.ToArray()), culprit, action, detail));
                    if (cfg.Notify)
                        Toast.Show(string.Format("Sample rate changed to {0}", Khz(off[0].Rate)), "By: " + culprit + "\n" + action);
                    notified = true;
                }

                if (!cfg.Restore) return;

                if (deferred.Length > 0)
                {
                    foreach (Process p in deferred) WatchExit(p);
                    return;
                }
                if (gaveUp) return;

                DateTime now = DateTime.Now;
                restores.RemoveAll(delegate (DateTime t) { return (now - t).TotalSeconds > cfg.RestoreWindowSeconds; });
                if (restores.Count >= cfg.MaxRestores)
                {
                    gaveUp = true;
                    Log.Write(string.Format("The rate keeps being changed back ({0}x in {1}s) - something insists on it. Stopped restoring until it returns to {2} Hz by itself.",
                        restores.Count, cfg.RestoreWindowSeconds, cfg.TargetRate));
                    return;
                }
                restores.Add(now);
                RestoreAll(off);
            }
        }

        void RestoreAll(List<Watched> off)
        {
            bool hw = false;
            foreach (var w in off)
            {
                if (w.Api == null) continue;
                uint s = w.Api.SetRate(w.Index, cfg.TargetRate);
                Log.Write(string.Format("Restore {0} -> {1} Hz: {2}", w.Label, cfg.TargetRate, s == 0 ? "ok" : string.Format("failed (0x{0:X})", s)));
                hw = true;
            }
            if (hw) Thread.Sleep(1500);   // hardware change re-creates/updates the Windows endpoints

            foreach (var w in Collect())
            {
                if (w.Endpoint == null || w.Rate == cfg.TargetRate) continue;
                int hr = Audio.SetEndpointRate(w.Endpoint.Id, cfg.TargetRate);
                if (hr == 0) Log.Write(string.Format("Restore {0} -> {1} Hz: ok", w.Label, cfg.TargetRate));
                else
                {
                    unsupported.Add(w.Endpoint.Id);
                    string reason = (uint)hr == 0x88890008
                        ? "Windows isn't offering that rate for this device right now. Many interface drivers only expose the rate the hardware is currently running at; " +
                          "for those, the rate can only be changed through the vendor driver (see DriverApi) or its control panel"
                        : "the device or driver refused the format";
                    Log.Write(string.Format("Restore {0} -> {1} Hz failed (0x{2:X8}): {3}. Notify-only for this device until the guard restarts.",
                        w.Label, cfg.TargetRate, hr, reason));
                }
            }
        }

        void WatchExit(Process p)
        {
            if (watchedDeferred.Contains(p.Id)) return;
            try
            {
                p.EnableRaisingEvents = true;
                int pid = p.Id; string name = p.ProcessName;
                p.Exited += delegate
                {
                    lock (gate) watchedDeferred.Remove(pid);
                    Log.Write(name + " closed.");
                    Thread.Sleep(2000);   // let it release the driver
                    Check(name + " closed");
                };
                watchedDeferred.Add(p.Id);
            }
            catch { }
        }

        static string Khz(uint rate) { return (rate / 1000.0).ToString("0.###") + " kHz"; }

        static readonly string[] GenericWords = { "asio", "audio", "usb", "driver", "virtual", "device", "input", "output",
            "the", "and", "pro", "high", "definition", "generic", "low", "latency", "interface", "sound" };
        static bool IsGenericWord(string w) { return Array.IndexOf(GenericWords, w.ToLowerInvariant()) >= 0; }

        // Best-effort attribution:
        //  1. a DAW from DeferTo (with its window title = open project)
        //  2. the newest program that has an ASIO driver loaded
        //  3. apps with an active audio session on an affected Windows device
        //  4. programs started in the last 2 minutes
        string FindCulprit(List<Watched> off, Process[] deferred, out string detail)
        {
            var det = new StringBuilder();
            string primary = null;
            int self = Process.GetCurrentProcess().Id;
            try
            {
                foreach (Process p in deferred)
                {
                    string title = ""; try { title = p.MainWindowTitle; } catch { }
                    primary = string.IsNullOrEmpty(title) ? p.ProcessName : p.ProcessName + " - " + title;
                    break;
                }

                var asioByFile = new Dictionary<string, string>();
                foreach (var a in asio) if (!asioByFile.ContainsKey(a.DllFile)) asioByFile[a.DllFile] = a.Name;

                // Only ASIO drivers that belong to an affected device can be blamed: the driver of an
                // affected interface, or a driver whose name shares a word with an affected endpoint.
                var relevant = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var w in off)
                {
                    if (w.Api != null) relevant.Add(w.Api.Name);
                    if (w.Endpoint == null) continue;
                    foreach (var a in asio)
                        foreach (string tok in a.Name.Split(new char[] { ' ', '-', '_', '(', ')' }, StringSplitOptions.RemoveEmptyEntries))
                            if (tok.Length >= 3 && !IsGenericWord(tok) && w.Endpoint.Name.IndexOf(tok, StringComparison.OrdinalIgnoreCase) >= 0)
                                relevant.Add(a.Name);
                }

                Process newest = null; DateTime newestStart = DateTime.MinValue; var clients = new List<string>();
                foreach (Process p in Process.GetProcesses())
                {
                    if (p.Id == self || p.Id <= 4) continue;
                    try
                    {
                        string drv = null;
                        foreach (ProcessModule m in p.Modules)
                        {
                            string f = m.ModuleName.ToLowerInvariant();
                            if (asioByFile.TryGetValue(f, out drv)) break;
                            drv = null;
                        }
                        if (drv == null) continue;
                        clients.Add(p.ProcessName + " (" + drv + ")");
                        if (!relevant.Contains(drv)) continue;
                        DateTime s = p.StartTime;
                        if (s > newestStart) { newestStart = s; newest = p; }
                    }
                    catch { }
                }
                if (clients.Count > 0) det.Append(" | ASIO clients: " + string.Join(", ", clients.ToArray()));
                if (primary == null && newest != null)
                {
                    string drvName = "ASIO";
                    foreach (ProcessModule m in newest.Modules) { string dn; if (asioByFile.TryGetValue(m.ModuleName.ToLowerInvariant(), out dn)) { drvName = dn; break; } }
                    primary = string.Format("{0} (via {1} ASIO, started {2:HH:mm:ss})", newest.ProcessName, drvName, newestStart);
                }

                var sessionApps = new List<string>();
                foreach (var w in off)
                {
                    if (w.Endpoint == null) continue;
                    foreach (int pid in Audio.ActiveSessionPids(en, w.Endpoint.Id))
                    {
                        try { string n = Process.GetProcessById(pid).ProcessName; if (!sessionApps.Contains(n)) sessionApps.Add(n); } catch { }
                    }
                }
                if (sessionApps.Count > 0) det.Append(" | apps playing/recording on the device: " + string.Join(", ", sessionApps.ToArray()));
                if (primary == null && sessionApps.Count > 0) primary = sessionApps[0] + " (Windows audio app using the device)";

                var recent = new List<string>();
                foreach (Process p in Process.GetProcesses())
                {
                    try
                    {
                        if (p.Id == self || (DateTime.Now - p.StartTime).TotalMinutes > 2) continue;
                        if (Array.IndexOf(Noise, p.ProcessName) >= 0 || recent.Contains(p.ProcessName)) continue;
                        recent.Add(p.ProcessName);
                    }
                    catch { }
                }
                if (recent.Count > 0) det.Append(" | started in the last 2 min: " + string.Join(", ", recent.ToArray()));
                if (primary == null)
                    primary = recent.Count > 0 ? "unknown - recently started: " + string.Join(", ", recent.ToArray()) : "unknown (no ASIO host, audio app or new program found)";
            }
            catch (Exception e) { if (primary == null) primary = "unknown (" + e.Message + ")"; }
            detail = det.ToString();
            return primary;
        }

        public void ShowStatus()
        {
            string d = Describe();
            Log.Write("Status: " + d);
            var lines = new List<string>();
            foreach (var w in Collect()) lines.Add(string.Format("{0}: {1} Hz", w.Label, w.Rate));
            Toast.Show("Sample Rate Guard status (target " + Khz(cfg.TargetRate) + ")",
                lines.Count > 0 ? string.Join("\n", lines.ToArray()) : "No watched devices found. Check Devices= in SampleRateGuard.ini.");
        }
    }

    // =====================================================================================
    // Entry point
    // =====================================================================================
    static class Program
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunName = "Sample Rate Guard";

        [MTAThread]
        static int Main(string[] args)
        {
            string exe = Process.GetCurrentProcess().MainModule.FileName;
            string dir = Path.GetDirectoryName(exe);
            if (!CanWrite(dir))
            {
                dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SampleRateGuard");
                Directory.CreateDirectory(dir);
            }
            Log.FilePath = Path.Combine(dir, "SampleRateGuard.log");
            Config cfg = Config.Load(Path.Combine(dir, "SampleRateGuard.ini"));
            Toast.RegisterAppId();

            string cmd = args.Length > 0 ? args[0].ToLowerInvariant() : "";
            switch (cmd)
            {
                case "--install":
                    using (var k = Registry.CurrentUser.CreateSubKey(RunKey)) k.SetValue(RunName, "\"" + exe + "\"");
                    Log.Write("Installed: starts at sign-in (HKCU Run).");
                    StopOthers();
                    Process.Start(exe);
                    Toast.Show("Sample Rate Guard installed", "It now runs silently at sign-in, watching for " + (cfg.TargetRate / 1000.0).ToString("0.###") + " kHz.");
                    Thread.Sleep(1500);
                    return 0;
                case "--uninstall":
                    using (var k = Registry.CurrentUser.OpenSubKey(RunKey, true)) if (k != null) k.DeleteValue(RunName, false);
                    StopOthers();
                    Log.Write("Uninstalled: removed from sign-in and stopped.");
                    Toast.Show("Sample Rate Guard removed", "It no longer starts at sign-in. You can delete its folder.");
                    Thread.Sleep(1500);
                    try { Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\AppUserModelId\" + Toast.AppId, false); } catch { }
                    return 0;
                case "--stop":
                    StopOthers();
                    Log.Write("Stopped.");
                    return 0;
                case "--test-notify":
                    Toast.Show("Sample Rate Guard test", "This is what you'll see if something changes your sample rate.");
                    Thread.Sleep(1500);
                    return 0;
                case "--status":
                    new Guard(cfg).ShowStatus();
                    Thread.Sleep(1500);
                    return 0;
            }

            bool created;
            using (var mutex = new Mutex(true, @"Local\SampleRateGuard", out created))
            {
                if (!created) return 0;
                Guard g;
                try { g = new Guard(cfg); g.Start(); }
                catch (Exception e) { Log.Write("startup failed: " + e); return 1; }
                new ManualResetEvent(false).WaitOne();   // sleep; Windows calls us on device changes
                GC.KeepAlive(g);
                GC.KeepAlive(mutex);
            }
            return 0;
        }

        static void StopOthers()
        {
            int self = Process.GetCurrentProcess().Id;
            foreach (Process p in Process.GetProcessesByName("SampleRateGuard"))
                if (p.Id != self) { try { p.Kill(); p.WaitForExit(3000); } catch { } }
        }

        static bool CanWrite(string dir)
        {
            try
            {
                string t = Path.Combine(dir, ".write-test");
                File.WriteAllText(t, "");
                File.Delete(t);
                return true;
            }
            catch { return false; }
        }
    }
}
