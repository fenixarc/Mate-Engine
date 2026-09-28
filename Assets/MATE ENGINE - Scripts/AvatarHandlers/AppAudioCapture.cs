using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

// Captures one process's audio output (Windows 10 2004+ process loopback) and feeds a BeatTracker.
// Falls back to loopback of the default output device (all system audio) when that is unavailable.
//
// WASAPI is called through minimal interop declared here rather than NAudio's wrappers: under Unity's Mono,
// NAudio's WaveFormat does not marshal correctly (Initialize fails or process loopback delivers no data),
// and NAudio keeps process-loopback activation internal. Formats are passed as raw WAVEFORMATEX memory.
//
// Captures are pooled per target process and paused instead of destroyed: Windows refuses a new process-loopback
// client for a process this app captured before until Mono finalizes the old COM wrappers, which Unity's
// conservative GC may never do. A paused capture has its audio client stopped and its thread asleep (no CPU).
// Everything COM-related lives on the capture thread; the pool API is main-thread only.
public sealed class AppAudioCapture
{
    public enum CaptureMode { Starting, PerApp, System, Paused, Failed, Stopped }

    const string ProcessLoopbackPath = "VAD\\Process_Loopback";
    const string DefaultRenderPath = "{E6327CAD-DCEC-4949-AE8A-991E976A79D2}"; // DEVINTERFACE_AUDIO_RENDER: follows the default device
    const int SampleRate = 48000;
    const long BufferDuration100ns = 200 * 10000; // 200 ms

    const int StreamFlagsLoopback = 0x00020000;
    const int StreamFlagsEventCallback = 0x00040000;
    const int StreamFlagsAutoConvertPcm = unchecked((int)0x80000000);
    const int BufferFlagsSilent = 0x2;
    const int E_UNEXPECTED = unchecked((int)0x8000FFFF);

    public readonly BeatTracker Tracker = new BeatTracker();
    public int TargetPid { get; private set; }

    volatile CaptureMode mode = CaptureMode.Stopped;
    public CaptureMode Mode => mode;
    bool IsAlive => thread != null && thread.IsAlive && mode != CaptureMode.Failed && mode != CaptureMode.Stopped;

    Thread thread;
    volatile bool run;
    volatile bool paused;
    readonly ManualResetEvent resumeSignal = new ManualResetEvent(true);
    float[] mono = new float[4096];
    short[] pcm16 = new short[8192];
    float[] pcm32 = new float[8192];

    static bool loggedFallback;

    // ---- Pool (main thread only) ----

    const int MaxPooled = 4;
    static readonly Dictionary<int, AppAudioCapture> pool = new Dictionary<int, AppAudioCapture>();
    static readonly List<int> recentPids = new List<int>();
    static bool quitHooked;

    // Returns a running capture for pid, resuming a paused one when possible.
    public static AppAudioCapture Acquire(int pid)
    {
        if (!quitHooked) { quitHooked = true; UnityEngine.Application.quitting += ShutdownAll; }

        if (pool.TryGetValue(pid, out var capture) && !capture.IsAlive)
        {
            pool.Remove(pid);
            capture = null;
        }
        if (capture == null)
        {
            capture = new AppAudioCapture();
            capture.StartThread(pid);
            pool[pid] = capture;
        }
        else capture.Resume();

        recentPids.Remove(pid);
        recentPids.Add(pid);
        while (recentPids.Count > MaxPooled)
        {
            int oldest = recentPids[0];
            recentPids.RemoveAt(0);
            if (pool.TryGetValue(oldest, out var evicted)) { evicted.Shutdown(false); pool.Remove(oldest); }
        }
        return capture;
    }

    // Stops analysing but keeps the capture for reuse.
    public void Pause()
    {
        paused = true;
        resumeSignal.Reset();
    }

    void Resume()
    {
        paused = false;
        resumeSignal.Set();
    }

    public static void ShutdownAll()
    {
        foreach (var c in pool.Values) c.Shutdown(true);
        pool.Clear();
        recentPids.Clear();
    }

    // ---- Capture thread ----

    void StartThread(int pid)
    {
        TargetPid = pid;
        run = true;
        mode = CaptureMode.Starting;
        thread = new Thread(CaptureLoop) { IsBackground = true, Name = "ME BPM Capture" };
        // Mono does not initialise COM on new threads by itself; WASAPI needs a multithreaded apartment.
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
    }

    void Shutdown(bool wait)
    {
        run = false;
        resumeSignal.Set();
        var t = thread;
        if (wait && t != null)
        {
            try { t.Join(250); } catch { }
        }
    }

    void CaptureLoop()
    {
        try
        {
            if (TargetPid > 0)
            {
                try
                {
                    try
                    {
                        RunCapture(true);
                    }
                    catch (COMException e) when (e.HResult == E_UNEXPECTED && run)
                    {
                        // An earlier client for this process may be waiting on finalization; collect once and retry.
                        GC.Collect();
                        GC.WaitForPendingFinalizers();
                        RunCapture(true);
                    }
                    return;
                }
                catch (Exception e)
                {
                    if (!run) return;
                    if (!loggedFallback)
                    {
                        loggedFallback = true;
                        UnityEngine.Debug.Log($"[BPM Sync] Per-app audio capture unavailable for PID {TargetPid} ({Describe(e)}); using system audio instead.");
                    }
                }
            }
            if (run) RunCapture(false);
        }
        catch (Exception e)
        {
            mode = CaptureMode.Failed;
            UnityEngine.Debug.LogWarning("[BPM Sync] Audio capture stopped: " + Describe(e));
        }
        finally
        {
            if (mode != CaptureMode.Failed) mode = CaptureMode.Stopped;
        }
    }

    void RunCapture(bool perApp)
    {
        IAudioClient client = null;
        IAudioCaptureClient capture = null;
        EventWaitHandle evt = null;
        IntPtr format = IntPtr.Zero;
        bool ownFormat = false;
        try
        {
            int flags = StreamFlagsLoopback | StreamFlagsEventCallback;
            if (perApp)
            {
                client = Activate(ProcessLoopbackPath, TargetPid);
                // Process loopback has no mix format; ask for 16-bit PCM and let Windows convert.
                format = MakePcm16Format(SampleRate, 2);
                ownFormat = true;
                flags |= StreamFlagsAutoConvertPcm;
            }
            else
            {
                client = Activate(DefaultRenderPath, 0);
                Check(client.GetMixFormat(out format));
            }

            ReadFormat(format, out int rate, out int channels, out int bits, out bool isFloat);
            Check(client.Initialize(0, flags, BufferDuration100ns, 0, format, IntPtr.Zero));
            evt = new EventWaitHandle(false, EventResetMode.AutoReset);
            Check(client.SetEventHandle(evt.SafeWaitHandle.DangerousGetHandle()));
            Check(client.GetService(typeof(IAudioCaptureClient).GUID, out object service));
            capture = (IAudioCaptureClient)service;

            Tracker.Configure(rate);
            var activeMode = perApp ? CaptureMode.PerApp : CaptureMode.System;
            bool clientRunning = false;

            while (run)
            {
                if (paused)
                {
                    if (clientRunning) { client.Stop(); clientRunning = false; }
                    mode = CaptureMode.Paused;
                    resumeSignal.WaitOne();
                    continue;
                }
                if (!clientRunning)
                {
                    // Fresh start or resume: forget the previous song's tempo. (IAudioClient.Reset is deliberately
                    // not used: on a process-loopback stream it stops packets from arriving at all.)
                    Tracker.Reset();
                    Check(client.Start());
                    clientRunning = true;
                    mode = activeMode;
                }

                evt.WaitOne(50);
                while (run && !paused && capture.GetNextPacketSize(out int packet) >= 0 && packet > 0)
                {
                    Check(capture.GetBuffer(out IntPtr data, out int frames, out int bufferFlags, out _, out _));
                    ToMono(data, frames, channels, bits, isFloat, (bufferFlags & BufferFlagsSilent) != 0);
                    capture.ReleaseBuffer(frames);
                    Tracker.Process(mono, frames);
                }
            }
        }
        finally
        {
            try { client?.Stop(); } catch { }
            if (capture != null) try { Marshal.ReleaseComObject(capture); } catch { }
            if (client != null) try { Marshal.ReleaseComObject(client); } catch { }
            try { evt?.Dispose(); } catch { }
            if (format != IntPtr.Zero)
            {
                if (ownFormat) Marshal.FreeHGlobal(format);
                else Marshal.FreeCoTaskMem(format);
            }
        }
    }

    void ToMono(IntPtr data, int frames, int channels, int bits, bool isFloat, bool silent)
    {
        if (mono.Length < frames) mono = new float[frames * 2];
        if (silent || data == IntPtr.Zero || channels <= 0) { Array.Clear(mono, 0, frames); return; }

        int n = frames * channels;
        float scale = 1f / channels;
        if (isFloat && bits == 32)
        {
            if (pcm32.Length < n) pcm32 = new float[n * 2];
            Marshal.Copy(data, pcm32, 0, n);
            for (int i = 0, j = 0; i < frames; i++, j += channels)
            {
                float sum = 0f;
                for (int c = 0; c < channels; c++) sum += pcm32[j + c];
                mono[i] = sum * scale;
            }
        }
        else if (!isFloat && bits == 16)
        {
            if (pcm16.Length < n) pcm16 = new short[n * 2];
            Marshal.Copy(data, pcm16, 0, n);
            scale /= 32768f;
            for (int i = 0, j = 0; i < frames; i++, j += channels)
            {
                int sum = 0;
                for (int c = 0; c < channels; c++) sum += pcm16[j + c];
                mono[i] = sum * scale;
            }
        }
        else Array.Clear(mono, 0, frames);
    }

    // ---- WAVEFORMATEX helpers (raw memory, no marshalling) ----

    static IntPtr MakePcm16Format(int rate, int channels)
    {
        IntPtr p = Marshal.AllocHGlobal(18);
        int block = channels * 2;
        Marshal.WriteInt16(p, 0, 1);                  // WAVE_FORMAT_PCM
        Marshal.WriteInt16(p, 2, (short)channels);
        Marshal.WriteInt32(p, 4, rate);
        Marshal.WriteInt32(p, 8, rate * block);
        Marshal.WriteInt16(p, 12, (short)block);
        Marshal.WriteInt16(p, 14, 16);
        Marshal.WriteInt16(p, 16, 0);
        return p;
    }

    static readonly Guid SubtypeIeeeFloat = new Guid("00000003-0000-0010-8000-00aa00389b71");

    static void ReadFormat(IntPtr p, out int rate, out int channels, out int bits, out bool isFloat)
    {
        int tag = (ushort)Marshal.ReadInt16(p, 0);
        channels = Marshal.ReadInt16(p, 2);
        rate = Marshal.ReadInt32(p, 4);
        bits = Marshal.ReadInt16(p, 14);
        isFloat = tag == 3;
        if (tag == 0xFFFE) // WAVE_FORMAT_EXTENSIBLE: SubFormat GUID at offset 24
        {
            var raw = new byte[16];
            Marshal.Copy(p + 24, raw, 0, 16);
            isFloat = new Guid(raw) == SubtypeIeeeFloat;
        }
    }

    static void Check(int hr)
    {
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
    }

    static string Describe(Exception e)
    {
        e = e.GetBaseException();
        return $"{e.GetType().Name} 0x{e.HResult:X8} {e.Message}".TrimEnd();
    }

    // ---- Activation (ActivateAudioInterfaceAsync) ----

    const ushort VT_BLOB = 65;
    const int ActivationTypeProcessLoopback = 1;
    const int LoopbackModeIncludeTargetProcessTree = 0;

    [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = false)]
    static extern void ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        IntPtr activationParams,
        IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation activationOperation);

    sealed class ActivationHandler : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        public readonly ManualResetEvent Done = new ManualResetEvent(false);
        public int Result;
        public object Interface;

        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation op)
        {
            try { op.GetActivateResult(out Result, out Interface); }
            catch (Exception e) { Result = e.HResult; }
            finally { Done.Set(); }
        }
    }

    // pid > 0: process loopback for that process tree; pid == 0: plain activation of the device path.
    static IAudioClient Activate(string path, int pid)
    {
        IntPtr blob = IntPtr.Zero, variant = IntPtr.Zero;
        var handler = new ActivationHandler();
        IActivateAudioInterfaceAsyncOperation operation = null;
        try
        {
            if (pid > 0)
            {
                // AUDIOCLIENT_ACTIVATION_PARAMS { ActivationType; { TargetProcessId; ProcessLoopbackMode } }
                blob = Marshal.AllocHGlobal(12);
                Marshal.WriteInt32(blob, 0, ActivationTypeProcessLoopback);
                Marshal.WriteInt32(blob, 4, pid);
                Marshal.WriteInt32(blob, 8, LoopbackModeIncludeTargetProcessTree);

                // PROPVARIANT (VT_BLOB): vt at 0, BLOB.cbSize at 8, BLOB.pBlobData at 8 + pointer alignment.
                int ptrOffset = 8 + IntPtr.Size;
                variant = Marshal.AllocHGlobal(ptrOffset + IntPtr.Size);
                for (int i = 0; i < ptrOffset + IntPtr.Size; i++) Marshal.WriteByte(variant, i, 0);
                Marshal.WriteInt16(variant, 0, (short)VT_BLOB);
                Marshal.WriteInt32(variant, 8, 12);
                Marshal.WriteIntPtr(variant, ptrOffset, blob);
            }

            ActivateAudioInterfaceAsync(path, typeof(IAudioClient).GUID, variant, handler, out operation);
            if (!handler.Done.WaitOne(3000)) throw new TimeoutException("audio activation timed out");
            Check(handler.Result);
            return (IAudioClient)handler.Interface;
        }
        finally
        {
            if (operation != null) try { Marshal.ReleaseComObject(operation); } catch { }
            if (variant != IntPtr.Zero) Marshal.FreeHGlobal(variant);
            if (blob != IntPtr.Zero) Marshal.FreeHGlobal(blob);
            handler.Done.Close();
        }
    }

    // ---- COM interfaces (vtable order matters) ----

    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, int streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr audioSessionGuid);
        [PreserveSig] int GetBufferSize(out uint frames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out int padding);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closestMatch);
        [PreserveSig] int GetMixFormat(out IntPtr format);
        [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr eventHandle);
        [PreserveSig] int GetService([MarshalAs(UnmanagedType.LPStruct)] Guid interfaceId, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr data, out int frames, out int flags, out long devicePosition, out long qpcPosition);
        [PreserveSig] int ReleaseBuffer(int frames);
        [PreserveSig] int GetNextPacketSize(out int frames);
    }

    [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IActivateAudioInterfaceCompletionHandler
    {
        void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation);
    }

    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IActivateAudioInterfaceAsyncOperation
    {
        void GetActivateResult(out int activateResult, [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
    }

    [ComImport, Guid("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAgileObject { }
}
