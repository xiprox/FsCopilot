using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;

namespace FsCopilot.Audio;

/// <summary>
/// WASAPI process loopback: captures what one process (and its children) renders, whatever
/// device it renders to, and nothing else. Windows 10 2004+ in practice, documented for build
/// 20348+. NAudio has no wrapper for the activation, so that part is hand-rolled on the pattern
/// of Microsoft's ApplicationLoopback sample; the resulting IAudioClient is driven through
/// NAudio's own COM interface definitions.
/// </summary>
public sealed class ProcessLoopback : IDisposable
{
    const string VirtualDevicePath = "VAD\\Process_Loopback";
    const int ActivationTypeProcessLoopback = 1;
    const int ModeIncludeTargetProcessTree = 0;
    const int ModeExcludeTargetProcessTree = 1;
    const ushort VT_BLOB = 0x41;

    const uint StreamFlagsLoopback = 0x00020000;
    const uint StreamFlagsEventCallback = 0x00040000;
    const uint StreamFlagsAutoConvertPcm = 0x80000000;
    const uint StreamFlagsSrcDefaultQuality = 0x08000000;

    static readonly Guid IID_IAudioClient = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
    static readonly Guid IID_IAudioCaptureClient = new("C8ADBD64-E71E-48a0-A4DE-185C395CD317");

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

    // NAudio keeps its IAudioCaptureClient internal; this is the same vtable.
    [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioCaptureClient
    {
        void GetBuffer(out IntPtr dataBuffer, out int numFramesToRead, out int bufferFlags, out long devicePosition, out long qpcPosition);
        void ReleaseBuffer(int numFramesRead);
        void GetNextPacketSize(out int numFramesInNextPacket);
    }
    const int BufferFlagSilent = 0x2;

    [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = false)]
    static extern void ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        ref Guid riid,
        IntPtr activationParams,
        IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation activationOperation);

    [ComVisible(true)]
    sealed class Completion : IActivateAudioInterfaceCompletionHandler
    {
        public readonly ManualResetEventSlim Done = new();
        public object? Client; public int Hr;
        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation op)
        {
            try { op.GetActivateResult(out Hr, out Client); }
            catch (Exception e) { Hr = e.HResult; }
            Done.Set();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct ActivationParams
    {
        public int ActivationType;
        public uint TargetProcessId;
        public int ProcessLoopbackMode;
    }

    readonly IAudioClient _client;
    readonly IAudioCaptureClient _capture;
    readonly EventWaitHandle _event = new(false, EventResetMode.AutoReset);
    readonly Thread _thread;
    volatile bool _stop;

    public WaveFormat Format { get; }
    public event Action<byte[], int>? Data;      // PCM bytes in Format, count
    public long FramesCaptured { get; private set; }
    public long SilentPackets { get; private set; }

    public ProcessLoopback(int pid, WaveFormat format, bool includeTree = true)
    {
        Format = format;

        var p = new ActivationParams
        {
            ActivationType = ActivationTypeProcessLoopback,
            TargetProcessId = (uint)pid,
            ProcessLoopbackMode = includeTree ? ModeIncludeTargetProcessTree : ModeExcludeTargetProcessTree
        };
        var pSize = Marshal.SizeOf<ActivationParams>();
        var pParams = Marshal.AllocHGlobal(pSize);
        var pVariant = Marshal.AllocHGlobal(24);
        try
        {
            Marshal.StructureToPtr(p, pParams, false);
            // PROPVARIANT { vt=VT_BLOB; BLOB { DWORD cbSize; BYTE* pBlobData } } — blob at offset 8,
            // pointer at 16 on x64.
            for (var i = 0; i < 24; i++) Marshal.WriteByte(pVariant, i, 0);
            Marshal.WriteInt16(pVariant, 0, unchecked((short)VT_BLOB));
            Marshal.WriteInt32(pVariant, 8, pSize);
            Marshal.WriteIntPtr(pVariant, 16, pParams);

            var iid = IID_IAudioClient;
            var completion = new Completion();
            ActivateAudioInterfaceAsync(VirtualDevicePath, ref iid, pVariant, completion, out var op);
            if (!completion.Done.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("ActivateAudioInterfaceAsync did not complete");
            if (completion.Hr != 0 || completion.Client is null) Marshal.ThrowExceptionForHR(completion.Hr == 0 ? unchecked((int)0x80004005) : completion.Hr);
            _client = (IAudioClient)completion.Client!;
            GC.KeepAlive(op);
        }
        finally
        {
            Marshal.FreeHGlobal(pVariant);
            Marshal.FreeHGlobal(pParams);
        }

        var session = Guid.Empty;
        var flags = unchecked((AudioClientStreamFlags)(StreamFlagsLoopback | StreamFlagsEventCallback | StreamFlagsAutoConvertPcm | StreamFlagsSrcDefaultQuality));
        _client.Initialize(AudioClientShareMode.Shared, flags, 2_000_000, 0, format, ref session);
        _client.SetEventHandle(_event.SafeWaitHandle.DangerousGetHandle());
        var captureIid = IID_IAudioCaptureClient;
        _client.GetService(captureIid, out var captureObj);
        _capture = (IAudioCaptureClient)captureObj;

        _thread = new Thread(Loop) { IsBackground = true, Name = "ProcessLoopback" };
    }

    public void Start()
    {
        _client.Start();
        _thread.Start();
    }

    void Loop()
    {
        var buffer = new byte[Format.AverageBytesPerSecond];    // one second is plenty
        while (!_stop)
        {
            if (!_event.WaitOne(100)) continue;
            while (!_stop)
            {
                _capture.GetNextPacketSize(out var packetFrames);
                if (packetFrames == 0) break;
                _capture.GetBuffer(out var data, out var frames, out var flags, out _, out _);
                var bytes = frames * Format.BlockAlign;
                if (bytes > buffer.Length) buffer = new byte[bytes];
                if ((flags & BufferFlagSilent) != 0)
                {
                    Array.Clear(buffer, 0, bytes);
                    SilentPackets++;
                }
                else Marshal.Copy(data, buffer, 0, bytes);
                _capture.ReleaseBuffer(frames);
                FramesCaptured += frames;
                Data?.Invoke(buffer, bytes);
            }
        }
    }

    public void Dispose()
    {
        _stop = true;
        try { _client.Stop(); } catch { /* ignore */ }
        if (_thread.IsAlive) _thread.Join(500);
        _event.Dispose();
    }
}
