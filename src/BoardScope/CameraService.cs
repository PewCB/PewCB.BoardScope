using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using DirectShowLib;
using OpenCvSharp;
using FormatType = DirectShowLib.FormatType;

namespace BoardScope;

// All graph/control operations share one MTA thread and one capture filter.
// The DirectShow callback only copies the newest frame; it never queues stale frames.
public sealed class CameraService : IDisposable, ISampleGrabberCB
{
    private readonly BlockingCollection<Action> _commands = new();
    private readonly Thread _thread;
    private readonly object _frameLock = new();
    private IFilterGraph2? _graph;
    private ICaptureGraphBuilder2? _builder;
    private IBaseFilter? _source, _null;
    private ISampleGrabber? _grabber;
    private string? _deviceId;
    private byte[]? _buffer;
    private int _width, _height, _stride;
    private bool _bottomUp;
    private long _sequence;
    private DateTimeOffset _frameTime;
    private CameraInfo _info = new("No camera", 0, 0, 0, "", false, 0);
    public CameraInfo Info { get { lock (_frameLock) return _info with { Frames = _sequence }; } }
    public string? DeviceId => _deviceId;
    public CameraService()
    {
        _thread = new Thread(() => { foreach (var command in _commands.GetConsumingEnumerable()) command(); }) { IsBackground = true, Name = "UVC camera" };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }
    private Task<T> OnThread<T>(Func<T> work)
    {
        var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _commands.Add(() => { try { result.SetResult(work()); } catch (Exception e) { result.SetException(e); } });
        return result.Task;
    }
    public Task<CameraDevice[]> DevicesAsync() => OnThread(() =>
    {
        var devices = DsDevice.GetDevicesOfCat(FilterCategory.VideoInputDevice);
        try { return devices.Select(d => new CameraDevice(d.DevicePath, d.Name)).ToArray(); }
        finally { foreach (var d in devices) d.Dispose(); }
    });
    private static IBaseFilter Bind(string id, out string name)
    {
        var devices = DsDevice.GetDevicesOfCat(FilterCategory.VideoInputDevice);
        try
        {
            var device = devices.FirstOrDefault(d => d.DevicePath == id) ?? throw new ArgumentException("Camera is no longer connected. Refresh the device list.");
            name = device.Name;
            var iid = typeof(IBaseFilter).GUID;
            device.Mon.BindToObject(null!, null, ref iid, out var filter);
            return (IBaseFilter)filter;
        }
        finally { foreach (var d in devices) d.Dispose(); }
    }
    private static IAMStreamConfig StreamConfig(ICaptureGraphBuilder2 builder, IBaseFilter source)
    {
        DsError.ThrowExceptionForHR(builder.FindInterface(PinCategory.Capture, MediaType.Video, source, typeof(IAMStreamConfig).GUID, out var cfg));
        return (IAMStreamConfig)cfg;
    }
    private static (int Width, int Height, long Interval) Header(AMMediaType type)
    {
        if (type.formatType == FormatType.VideoInfo)
        {
            var h = Marshal.PtrToStructure<VideoInfoHeader>(type.formatPtr)!;
            return (h.BmiHeader.Width, h.BmiHeader.Height, h.AvgTimePerFrame);
        }
        if (type.formatType == FormatType.VideoInfo2)
        {
            var h = Marshal.PtrToStructure<VideoInfoHeader2>(type.formatPtr)!;
            return (h.BmiHeader.Width, h.BmiHeader.Height, h.AvgTimePerFrame);
        }
        throw new NotSupportedException("Camera returned an unsupported video header.");
    }
    private static string FormatName(Guid subtype)
    {
        if (subtype == MediaSubType.RGB24) return "RGB24";
        if (subtype == MediaSubType.RGB32) return "RGB32";
        var bytes = subtype.ToByteArray().Take(4).ToArray();
        return bytes.All(b => b >= 32 && b < 127) ? System.Text.Encoding.ASCII.GetString(bytes) : subtype.ToString()[..8];
    }
    private static CameraMode[] Modes(IAMStreamConfig cfg)
    {
        DsError.ThrowExceptionForHR(cfg.GetNumberOfCapabilities(out int count, out int size));
        var caps = Marshal.AllocCoTaskMem(size);
        var modes = new List<CameraMode>();
        try
        {
            for (int i = 0; i < count; i++)
            {
                AMMediaType? type = null;
                try
                {
                    if (cfg.GetStreamCaps(i, out type, caps) < 0) continue;
                    var h = Header(type);
                    modes.Add(new(i, h.Width, Math.Abs(h.Height), h.Interval > 0 ? Math.Round(10_000_000d / h.Interval, 2) : 0, FormatName(type.subType)));
                }
                finally { if (type != null) DsUtils.FreeAMMediaType(type); }
            }
        }
        finally { Marshal.FreeCoTaskMem(caps); }
        return modes.OrderByDescending(m => (long)m.Width * m.Height).ThenByDescending(m => m.Fps).ToArray();
    }
    public Task<CameraMode[]> ModesAsync(string id) => OnThread(() =>
    {
        if (_deviceId == id && _builder != null && _source != null) return Modes(StreamConfig(_builder, _source));
        var source = Bind(id, out _);
        var graph = (IFilterGraph2)new FilterGraph();
        var builder = (ICaptureGraphBuilder2)new CaptureGraphBuilder2();
        try
        {
            DsError.ThrowExceptionForHR(builder.SetFiltergraph(graph));
            DsError.ThrowExceptionForHR(graph.AddFilter(source, "Camera"));
            return Modes(StreamConfig(builder, source));
        }
        finally { Release(builder); Release(graph); Release(source); }
    });
    public Task<CameraInfo> OpenAsync(CameraOpen request) => OnThread(() =>
    {
        Close();
        try
        {
            _source = Bind(request.DeviceId, out var name);
            _deviceId = request.DeviceId;
            _graph = (IFilterGraph2)new FilterGraph();
            _builder = (ICaptureGraphBuilder2)new CaptureGraphBuilder2();
            DsError.ThrowExceptionForHR(_builder.SetFiltergraph(_graph));
            DsError.ThrowExceptionForHR(_graph.AddFilter(_source, "Microscope"));
            var cfg = StreamConfig(_builder, _source);
            var mode = Modes(cfg).FirstOrDefault(m => m.Index == request.ModeIndex) ?? throw new ArgumentException("Select a supported capture mode.");
            cfg.GetNumberOfCapabilities(out _, out int capSize);
            var caps = Marshal.AllocCoTaskMem(capSize);
            AMMediaType? selected = null;
            try
            {
                DsError.ThrowExceptionForHR(cfg.GetStreamCaps(mode.Index, out selected, caps));
                DsError.ThrowExceptionForHR(cfg.SetFormat(selected));
            }
            finally { Marshal.FreeCoTaskMem(caps); if (selected != null) DsUtils.FreeAMMediaType(selected); }
            _grabber = (ISampleGrabber)new SampleGrabber();
            var requested = new AMMediaType { majorType = MediaType.Video, subType = MediaSubType.RGB24, formatType = FormatType.VideoInfo };
            DsError.ThrowExceptionForHR(_grabber.SetMediaType(requested));
            DsError.ThrowExceptionForHR(_graph.AddFilter((IBaseFilter)_grabber, "Frames"));
            _null = (IBaseFilter)new NullRenderer();
            DsError.ThrowExceptionForHR(_graph.AddFilter(_null, "Sink"));
            DsError.ThrowExceptionForHR(_builder.RenderStream(PinCategory.Capture, MediaType.Video, _source, (IBaseFilter)_grabber, _null));
            var connected = new AMMediaType();
            DsError.ThrowExceptionForHR(_grabber.GetConnectedMediaType(connected));
            try
            {
                var h = Header(connected);
                _width = h.Width; _height = Math.Abs(h.Height); _bottomUp = h.Height > 0;
                _stride = (_width * 3 + 3) & ~3;
                if (_width != mode.Width || _height != mode.Height) throw new InvalidOperationException("Camera negotiated a different resolution. Choose another mode.");
                _info = new(name, _width, _height, h.Interval > 0 ? 10_000_000d / h.Interval : mode.Fps, mode.Format, true, 0);
            }
            finally { DsUtils.FreeAMMediaType(connected); }
            DsError.ThrowExceptionForHR(_grabber.SetBufferSamples(false));
            DsError.ThrowExceptionForHR(_grabber.SetOneShot(false));
            DsError.ThrowExceptionForHR(_grabber.SetCallback(this, 1));
            DsError.ThrowExceptionForHR(((IMediaControl)_graph).Run());
            return Info;
        }
        catch { Close(); throw; }
    });
    public int SampleCB(double sampleTime, IMediaSample sample) => 0;
    public int BufferCB(double sampleTime, IntPtr buffer, int length)
    {
        try
        {
            if (length < _stride * _height || _width <= 0) return 0;
            var bytes = new byte[_stride * _height];
            Marshal.Copy(buffer, bytes, 0, bytes.Length);
            lock (_frameLock) { _buffer = bytes; _sequence++; _frameTime = DateTimeOffset.UtcNow; }
        }
        catch { /* Never throw through a native capture callback. */ }
        return 0;
    }
    public Mat Latest()
    {
        byte[] bytes; int width, height, stride; bool bottom;
        lock (_frameLock)
        {
            if (_buffer == null || DateTimeOffset.UtcNow - _frameTime > TimeSpan.FromSeconds(3)) throw new InvalidOperationException("No fresh camera frame. Reconnect the microscope.");
            bytes = _buffer; width = _width; height = _height; stride = _stride; bottom = _bottomUp;
        }
        var frame = new Mat(height, width, MatType.CV_8UC3);
        for (int y = 0; y < height; y++) Marshal.Copy(bytes, (bottom ? height - 1 - y : y) * stride, frame.Ptr(y), width * 3);
        return frame;
    }
    public async Task<Mat> FreshAsync(int discard, CancellationToken ct)
    {
        long start; lock (_frameLock) start = _sequence;
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            lock (_frameLock) if (_sequence >= start + discard) return Latest();
            await Task.Delay(20, ct);
        }
        throw new TimeoutException("Camera did not deliver fresh frames within 15 seconds.");
    }
    public Task<List<CameraProperty>> PropertiesAsync() => OnThread(Properties);
    private List<CameraProperty> Properties()
    {
        var properties = new List<CameraProperty>();
        if (_source is IAMCameraControl camera)
            foreach (var p in Enum.GetValues<CameraControlProperty>())
                if (camera.GetRange(p, out int min, out int max, out int step, out int def, out var flags) >= 0 && camera.Get(p, out int value, out var mode) >= 0)
                    properties.Add(new("camera", p.ToString(), min, max, Math.Max(1, step), def, value, mode.HasFlag(CameraControlFlags.Auto), flags.HasFlag(CameraControlFlags.Auto), flags.HasFlag(CameraControlFlags.Manual)));
        if (_source is IAMVideoProcAmp video)
            foreach (var p in Enum.GetValues<VideoProcAmpProperty>())
                if (video.GetRange(p, out int min, out int max, out int step, out int def, out var flags) >= 0 && video.Get(p, out int value, out var mode) >= 0)
                    properties.Add(new("video", p.ToString(), min, max, Math.Max(1, step), def, value, mode.HasFlag(VideoProcAmpFlags.Auto), flags.HasFlag(VideoProcAmpFlags.Auto), flags.HasFlag(VideoProcAmpFlags.Manual)));
        return properties;
    }
    public Task<List<CameraProperty>> SetAsync(PropertySet request) => OnThread(() =>
    {
        var property = Properties().FirstOrDefault(p => p.Group == request.Group && p.Name == request.Name) ?? throw new ArgumentException("Camera does not expose this control.");
        if (request.Auto ? !property.CanAuto : !property.CanManual) throw new ArgumentException("The requested auto/manual mode is unsupported.");
        if (!request.Auto && (request.Value < property.Min || request.Value > property.Max || (request.Value - (long)property.Min) % property.Step != 0)) throw new ArgumentException("Value does not match the control range/step.");
        int result = request.Group == "camera"
            ? ((IAMCameraControl)_source!).Set(Enum.Parse<CameraControlProperty>(request.Name), request.Value, request.Auto ? CameraControlFlags.Auto : CameraControlFlags.Manual)
            : ((IAMVideoProcAmp)_source!).Set(Enum.Parse<VideoProcAmpProperty>(request.Name), request.Value, request.Auto ? VideoProcAmpFlags.Auto : VideoProcAmpFlags.Manual);
        DsError.ThrowExceptionForHR(result);
        return Properties();
    });
    private static void Release(object? value) { if (value != null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value); }
    private void Close()
    {
        if (_graph != null) { try { ((IMediaControl)_graph).Stop(); } catch { } }
        if (_grabber != null) { try { _grabber.SetCallback(null, 1); } catch { } }
        Release(_grabber); Release(_null); Release(_source); Release(_builder); Release(_graph);
        _grabber = null; _null = null; _source = null; _builder = null; _graph = null; _deviceId = null;
        lock (_frameLock) { _buffer = null; _sequence = 0; _info = new("No camera", 0, 0, 0, "", false, 0); }
    }
    public Task<bool> CloseAsync() => OnThread(() => { Close(); return true; });
    public void Dispose() { CloseAsync().GetAwaiter().GetResult(); _commands.CompleteAdding(); _thread.Join(2000); }
}
