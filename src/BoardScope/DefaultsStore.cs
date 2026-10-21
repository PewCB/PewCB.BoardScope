using System.Text.Json;

namespace BoardScope;

public record AppDefaults
{
    public ScanSettings Scan { get; init; } = new();
    public PlatformSettings Platform { get; init; } = new();
    public string CameraDeviceId { get; init; } = "";
    public CameraMode? CameraMode { get; init; }
    public List<PropertySet> CameraProperties { get; init; } = [];
    public string Port { get; init; } = "COM4";
    public int Baud { get; init; } = 115200;
    public bool StatusInches { get; init; }
    public double JogStep { get; init; } = .5;
    public void Validate()
    {
        if (Scan == null || CameraProperties == null || Platform == null) throw new ArgumentException("Missing defaults settings.");
        Scan.Validate();Platform.Validate();
        if (!double.IsFinite(JogStep) || JogStep is <= 0 or > 10 || Baud is < 1 or > 4000000)
            throw new ArgumentException("Invalid jog step or baud rate.");
        if (CameraMode is { } m && (m.Width <= 0 || m.Height <= 0 || !double.IsFinite(m.Fps) || m.Fps <= 0))
            throw new ArgumentException("Invalid saved camera mode.");
        if (CameraProperties.Count > 100 || CameraProperties.Any(p => p == null || p.Group is not ("camera" or "video") || string.IsNullOrWhiteSpace(p.Name)))
            throw new ArgumentException("Invalid saved camera controls.");
    }
}

public sealed class DefaultsStore
{
    private readonly object _gate = new();
    private string PathName => Path.Combine(Files.Root, "defaults.json");
    public AppDefaults? Load()
    {
        lock (_gate)
        {
            if (!File.Exists(PathName)) return null;
            var settings = JsonSerializer.Deserialize<AppDefaults>(File.ReadAllText(PathName), Files.Json);
            settings?.Validate();
            return settings;
        }
    }
    public AppDefaults Save(AppDefaults settings)
    {
        settings.Validate();
        lock (_gate) Files.Save(PathName, settings);
        return settings;
    }
    public PlatformSettings LoadPlatform()=>Load()?.Platform??new();
    public PlatformSettings SavePlatform(PlatformSettings platform)
    {
        platform.Validate();
        lock(_gate) return Save((Load()??new AppDefaults()) with{Platform=platform}).Platform;
    }
}
