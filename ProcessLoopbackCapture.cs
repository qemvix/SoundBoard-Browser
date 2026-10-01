using System.Runtime.InteropServices;
using NAudio.Wave;

namespace SoundboardBrowser;

/// <summary>
/// Captures only the audio played by one process tree (the WebView2 browser) using the
/// Windows 10 2004+/11 "process loopback" API. It does not change what you hear.
/// </summary>
public sealed class ProcessLoopbackCapture : IDisposable
{
    public BufferedWaveProvider Buffer { get; }
    public event Action<string> Failed;

    readonly uint _pid;
    Thread _thread;
    volatile bool _run;

    public ProcessLoopbackCapture(uint pid)
    {
        _pid = pid;
        Buffer = new BufferedWaveProvider(new WaveFormat(AudioEngine.Rate, 16, 2))
        {
            DiscardOnBufferOverflow = true,
            BufferDuration = TimeSpan.FromMilliseconds(300),
            ReadFully = true
        };
    }

    public void Start()
    {
        _run = true;
        _thread = new Thread(Loop) { IsBackground = true, Name = "ProcessLoopback" };
        _thread.Start();
    }

    public void Dispose()
    {
        _run = false;
        _thread?.Join(1000);
    }

    void Loop()
    {
        try
        {
            var client = Activate(_pid);
            var fmt = new WAVEFORMATEX
            {
                wFormatTag = 1, nChannels = 2, nSamplesPerSec = AudioEngine.Rate,
                wBitsPerSample = 16, nBlockAlign = 4, nAvgBytesPerSec = AudioEngine.Rate * 4, cbSize = 0
            };
            const int LOOPBACK = 0x00020000, EVENTCALLBACK = 0x00040000;
            Check(client.Initialize(0, LOOPBACK | EVENTCALLBACK, 200000, 0, ref fmt, IntPtr.Zero));

            using var evt = new AutoResetEvent(false);
            Check(client.SetEventHandle(evt.SafeWaitHandle.DangerousGetHandle()));

            var iid = typeof(IAudioCaptureClient).GUID;
            Check(client.GetService(ref iid, out object o));
            var cap = (IAudioCaptureClient)o;
            Check(client.Start());

            var tmp = new byte[AudioEngine.Rate * 4];
            while (_run)
            {
                evt.WaitOne(50);
                while (_run && cap.GetNextPacketSize(out uint n) == 0 && n > 0)
                {
                    Check(cap.GetBuffer(out IntPtr p, out uint frames, out uint flags, out _, out _));
                    int bytes = (int)frames * 4;
                    if (bytes > tmp.Length) tmp = new byte[bytes];
                    if ((flags & 2) != 0) Array.Clear(tmp, 0, bytes);   // AUDCLNT_BUFFERFLAGS_SILENT
                    else Marshal.Copy(p, tmp, 0, bytes);
                    Buffer.AddSamples(tmp, 0, bytes);
                    cap.ReleaseBuffer(frames);
                }
            }
            client.Stop();
        }
        catch (Exception ex)
        {
            Failed?.Invoke(ex.Message);
        }
    }

    static void Check(int hr) { if (hr < 0) Marshal.ThrowExceptionForHR(hr); }

    static IAudioClient Activate(uint pid)
    {
        var prm = new AUDIOCLIENT_ACTIVATION_PARAMS { ActivationType = 1, TargetProcessId = pid, ProcessLoopbackMode = 0 };
        int size = Marshal.SizeOf<AUDIOCLIENT_ACTIVATION_PARAMS>();
        IntPtr pParams = Marshal.AllocHGlobal(size);
        IntPtr pv = Marshal.AllocHGlobal(24);   // PROPVARIANT (x64)
        try
        {
            Marshal.StructureToPtr(prm, pParams, false);
            for (int i = 0; i < 24; i++) Marshal.WriteByte(pv, i, 0);
            Marshal.WriteInt16(pv, 0, 65);          // VT_BLOB
            Marshal.WriteInt32(pv, 8, size);        // BLOB.cbSize
            Marshal.WriteIntPtr(pv, 16, pParams);   // BLOB.pBlobData

            var handler = new Handler();
            var iid = typeof(IAudioClient).GUID;
            ActivateAudioInterfaceAsync("VAD\\Process_Loopback", ref iid, pv, handler, out var op);
            if (!handler.Done.Wait(5000)) throw new TimeoutException("Timed out activating browser audio capture.");
            op.GetActivateResult(out int hr, out object unk);
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);
            return (IAudioClient)unk;
        }
        finally
        {
            Marshal.FreeHGlobal(pv);
            Marshal.FreeHGlobal(pParams);
        }
    }

    // ---- COM interop -------------------------------------------------------------------------

    [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = false)]
    static extern void ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        ref Guid riid, IntPtr activationParams,
        IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation activationOperation);

    [StructLayout(LayoutKind.Sequential)]
    struct AUDIOCLIENT_ACTIVATION_PARAMS { public int ActivationType; public uint TargetProcessId; public int ProcessLoopbackMode; }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    struct WAVEFORMATEX
    {
        public ushort wFormatTag, nChannels;
        public uint nSamplesPerSec, nAvgBytesPerSec;
        public ushort nBlockAlign, wBitsPerSample, cbSize;
    }

    [ComImport, Guid("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAgileObject { }

    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IActivateAudioInterfaceAsyncOperation
    {
        void GetActivateResult(out int activateResult, [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
    }

    [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IActivateAudioInterfaceCompletionHandler
    {
        void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation);
    }

    [ComVisible(true)]
    sealed class Handler : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        public readonly ManualResetEventSlim Done = new(false);
        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation op) => Done.Set();
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, int streamFlags, long hnsBufferDuration, long hnsPeriodicity, ref WAVEFORMATEX format, IntPtr sessionGuid);
        [PreserveSig] int GetBufferSize(out uint bufferSize);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint padding);
        [PreserveSig] int IsFormatSupported(int shareMode, ref WAVEFORMATEX format, IntPtr closest);
        [PreserveSig] int GetMixFormat(out IntPtr format);
        [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr eventHandle);
        [PreserveSig] int GetService(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr data, out uint numFrames, out uint flags, out ulong devicePosition, out ulong qpcPosition);
        [PreserveSig] int ReleaseBuffer(uint numFrames);
        [PreserveSig] int GetNextPacketSize(out uint numFrames);
    }
}
