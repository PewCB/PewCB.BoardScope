using System.Globalization;
using System.Text.Json;

namespace BoardScope;

public static class Files
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public static string Root { get; set; } = "";
    public static void Save(string path, object value)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value, Json));
        File.Move(temp, path, true);
    }
    public static ScanProject Load(string id) => JsonSerializer.Deserialize<ScanProject>(File.ReadAllText(Path.Combine(ProjectPath(id), "project.json")), Json)!;
    public static string ProjectPath(string id)
    {
        if (id.Length > 100 || id.Length == 0 || id.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-')) throw new ArgumentException("Invalid project ID.");
        return Path.Combine(Root, id);
    }
    public static string N(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);
}

public record CameraDevice(string Id, string Name);
public record CameraMode(int Index, int Width, int Height, double Fps, string Format);
public record CameraProperty(string Group, string Name, int Min, int Max, int Step, int Default, int Value, bool Auto, bool CanAuto, bool CanManual);
public record CameraInfo(string Name, int Width, int Height, double Fps, string Format, bool Connected, long Frames);
public record CameraOpen(string DeviceId, int ModeIndex);
public record PropertySet(string Group, string Name, int Value, bool Auto);
public record MotionConnect(string Port, int Baud = 115200, bool StatusInches = false);
public record Jog(double X, double Y, double Feed = 300);
public record XY(double X, double Y);
public record BoardReferenceRequest(string Point);
public static class BoardReference
{
    public static XY Offset(string point, double width, double height) => point switch
    {
        "center" => new(width/2,height/2),
        "top-left" => new(0,0),
        "top-right" => new(width,0),
        "bottom-left" => new(0,height),
        "bottom-right" => new(width,height),
        _ => throw new ArgumentException("Select the board center or one of its four corners.")
    };
}

public record ScanSettings
{
    public string Name { get; init; } = "PCB scan";
    public double BoardWidth { get; init; } = 54;
    public double BoardHeight { get; init; } = 54;
    public double FovWidth { get; init; } = 12;
    public double FovHeight { get; init; } = 9;
    public double Overlap { get; init; } = 30;
    public bool TestGrid { get; init; }
    public double StepX { get; init; } = 5;
    public double StepY { get; init; } = 5;
    public double Feed { get; init; } = 500;
    public int SettleMs { get; init; } = 300;
    public int DiscardFrames { get; init; } = 3;
    public double RowApproachMm { get; init; } = .5;
    public int StageXSign { get; init; } = 1;
    public int StageYSign { get; init; } = 1;
    public double StageRotationDegrees { get; init; }
    public bool SwapStageAxes { get; init; }
    public int GridYSign { get; init; } = -1;
    public string ReferencePoint { get; init; } = "center";
    public double RegionOffsetX { get; init; }
    public double RegionOffsetY { get; init; }
    public double MaxTravelX { get; init; } = 60;
    public double MaxTravelY { get; init; } = 60;
    public int Rotation { get; init; }
    public bool FlipX { get; init; }
    public bool FlipY { get; init; }
    public bool AutoStitch { get; init; } = true;
    public string StitchMethod { get; init; } = "stage";
    public string StitchBlendMode { get; init; } = "feather";
    public RegistrationOptions? StitchOptions { get; init; }
    public RegistrationOptions? AutomaticRefinement() => StitchMethod=="stage"?null:(StitchOptions??new RegistrationOptions()) with{Method=StitchMethod};
    public bool Refine { get; init; } // Legacy setting. Position-only stitching ignores it.
    public void Validate()
    {
        if(StitchMethod is not ("stage" or "sift" or "phase" or "ncc" or "hybrid" or "parallax"))throw new ArgumentException("Choose a valid after-scan stitch method.");
        MosaicBlend.Validate(StitchBlendMode);AutomaticRefinement()?.Validate();
        var values = new[] { BoardWidth, BoardHeight, FovWidth, FovHeight, Overlap, Feed, MaxTravelX, MaxTravelY, StepX, StepY, RowApproachMm, StageRotationDegrees, RegionOffsetX, RegionOffsetY };
        if (values.Any(v => !double.IsFinite(v))) throw new ArgumentException("All dimensions must be finite numbers.");
        if (BoardWidth is < 0.1 or > 1000 || BoardHeight is < 0.1 or > 1000 || FovWidth is < 0.05 or > 1000 || FovHeight is < 0.05 or > 1000) throw new ArgumentException("Board/FOV dimensions are outside the supported range.");
        if (Overlap is < 10 or > 60 || Feed is < 1 or > 3000 || SettleMs is < 0 or > 10000 || DiscardFrames is < 1 or > 30) throw new ArgumentException("Check overlap (10–60%), feed (1–3000), settle time and fresh-frame count.");
        if (Math.Abs(StageXSign) != 1 || Math.Abs(StageYSign) != 1 || Math.Abs(GridYSign) != 1 || Math.Abs(StageRotationDegrees)>180 || Rotation is not (0 or 90 or 180 or 270)) throw new ArgumentException("Invalid axis direction or camera rotation.");
        if (StepX is < .05 or > 100 || StepY is < .05 or > 100) throw new ArgumentException("3×3 steps must be between 0.05 and 100 mm.");
        if (RowApproachMm is < 0 or > 5) throw new ArgumentException("Row-start approach must be between 0 and 5 mm.");
        if (MaxTravelX is < 0.1 or > 1000 || MaxTravelY is < 0.1 or > 1000) throw new ArgumentException("Set a valid travel envelope from the board center.");
        BoardReference.Offset(ReferencePoint,BoardWidth,BoardHeight);
    }
}
public record CapturePoint(int Index, int Row, int Col, double U, double V, double StageDx, double StageDy);
public record ScanMove(CapturePoint Point, double StageDx, double StageDy, bool Capture);
public record ScanPlan(int Rows, int Cols, double StepX, double StepY, IReadOnlyList<CapturePoint> Points, IReadOnlyList<ScanMove> Moves);
public static class Planner
{
    public static ScanPlan Create(ScanSettings s)
    {
        s.Validate();
        var xs = s.TestGrid ? Enumerable.Range(0,3).Select(i=>s.FovWidth/2+i*s.StepX).ToArray() : Centres(s.BoardWidth, s.FovWidth, s.Overlap);
        var ys = s.TestGrid ? Enumerable.Range(0,3).Select(i=>s.FovHeight/2+i*s.StepY).ToArray() : Centres(s.BoardHeight, s.FovHeight, s.Overlap);
        if ((long)xs.Length * ys.Length > 10000) throw new ArgumentException("MVP limit is 10,000 captures. Reduce magnification or scan a smaller region.");
        var points = new List<CapturePoint>();
        var moves = new List<ScanMove>();
        var firstStage = CaptureToStage(s, xs[0], ys[0]);
        double startDx = s.TestGrid ? firstStage.X : 0;
        double startDy = s.TestGrid ? firstStage.Y : 0;
        var regionOffset=s.TestGrid?new XY(0,0):GridToStage(s,s.RegionOffsetX,s.RegionOffsetY);
        void CheckTravel(double dx, double dy)
        {
            if (Math.Abs(dx-startDx+regionOffset.X) > s.MaxTravelX || Math.Abs(dy-startDy+regionOffset.Y) > s.MaxTravelY)
                throw new ArgumentException("Scan including row-start approach exceeds the travel envelope. Check directions, travel limits and approach distance.");
        }
        for (int row = 0; row < ys.Length; row++)
            for (int k = 0; k < xs.Length; k++)
            {
                int col = k;
                var u = xs[col]; var v = ys[row];
                var stage = CaptureToStage(s, u, v);
                double dx = stage.X, dy = stage.Y;
                CheckTravel(dx, dy);
                var point = new CapturePoint(points.Count, row, col, u, v, dx, dy);
                if (col == 0 && s.RowApproachMm > 0)
                {
                    var approach = GridToStage(s,s.RowApproachMm,0);
                    double approachDx = dx - approach.X;
                    double approachDy = dy - approach.Y;
                    CheckTravel(approachDx, approachDy);
                    moves.Add(new(point, approachDx, approachDy, false));
                }
                points.Add(point);
                moves.Add(new(point, dx, dy, true));
            }
        return new(ys.Length, xs.Length, xs.Length > 1 ? xs[1] - xs[0] : 0, ys.Length > 1 ? ys[1] - ys[0] : 0, points, moves);
    }
    public static XY GridToStage(ScanSettings s, double u, double v)
    {
        double angle=s.StageRotationDegrees*Math.PI/180,c=Math.Cos(angle),sn=Math.Sin(angle);
        double x=c*u+sn*v,y=-sn*u+c*v;
        return new(s.StageXSign*(s.SwapStageAxes?y*s.GridYSign:x),s.StageYSign*(s.SwapStageAxes?x:y*s.GridYSign));
    }
    public static XY CaptureToStage(ScanSettings s, double u, double v)
    {
        var reference=BoardReference.Offset(s.ReferencePoint,s.BoardWidth,s.BoardHeight);
        return GridToStage(s,u-reference.X,v-reference.Y);
    }
    private static double[] Centres(double size, double fov, double overlap)
    {
        if (size <= fov) return [size / 2];
        int count = (int)Math.Ceiling((size - fov) / (fov * (1 - overlap / 100)) - 1e-10) + 1;
        if (count > 10000) throw new ArgumentException("Too many scan positions.");
        return Enumerable.Range(0, count).Select(i => fov / 2 + i * (size - fov) / (count - 1)).ToArray();
    }
}
public class Tile
{
    public string File { get; set; } = "";
    public int Row { get; set; }
    public int Col { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double NominalX { get; set; }
    public double NominalY { get; set; }
    public double StageX { get; set; }
    public double StageY { get; set; }
    public double? BaseX { get; set; }
    public double? BaseY { get; set; }
    public TileRegistration? Registration { get; set; }
    public string? SourceDzi { get; set; }
    public string? PreviewFile { get; set; }
    public DateTimeOffset CapturedAt { get; set; }
}
public class ScanProject
{
    public LightBackground? LightBackground { get; set; }
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public DateTimeOffset Created { get; set; } = DateTimeOffset.UtcNow;
    public bool Demo { get; set; }
    public bool CropToBoard { get; set; }
    public double OriginX { get; set; }
    public double OriginY { get; set; }
    public PlacementSettings? Placement { get; set; }
    public ScanSettings Settings { get; set; } = new();
    public CameraInfo? Camera { get; set; }
    public PlatformSettings? Platform { get; set; }
    public List<CameraProperty> CameraProperties { get; set; } = [];
    public XY? Reference { get; set; }
    public string Status { get; set; } = "capturing";
    public int Planned { get; set; }
    public List<Tile> Tiles { get; set; } = [];
    public int Width { get; set; }
    public int Height { get; set; }
    public string? Dzi { get; set; }
    public string BlendMode { get; set; } = "feather";
    public int AcceptedMatches { get; set; }
    public int RejectedMatches { get; set; }
    public RegistrationReport? Registration { get; set; }
    public MosaicVersion? PreviousMosaic { get; set; }
    public MosaicVersion? StageMosaic { get; set; }
    public int StitchCatalogVersion { get; set; }
    public List<SavedStitch> Stitches { get; set; } = [];
    public string? ActiveStitchId { get; set; }
    public string? ActiveMapId { get; set; }
    public string? Error { get; set; }
}
public record PlacementSettings
{
    public double FovWidth { get; init; } = 12;
    public double FovHeight { get; init; } = 9;
    public int StageXSign { get; init; } = 1;
    public int StageYSign { get; init; } = 1;
    public double StageRotationDegrees { get; init; }
    public bool SwapStageAxes { get; init; }
    public int GridYSign { get; init; } = -1;
    public static PlacementSettings From(ScanSettings s) => new(){FovWidth=s.FovWidth,FovHeight=s.FovHeight,StageXSign=s.StageXSign,StageYSign=s.StageYSign,StageRotationDegrees=s.StageRotationDegrees,SwapStageAxes=s.SwapStageAxes,GridYSign=s.GridYSign};
    public void Validate()
    {
        if (!double.IsFinite(FovWidth) || !double.IsFinite(FovHeight) || !double.IsFinite(StageRotationDegrees) || Math.Abs(StageRotationDegrees)>180 || FovWidth is < .05 or > 1000 || FovHeight is < .05 or > 1000 || Math.Abs(StageXSign)!=1 || Math.Abs(StageYSign)!=1 || Math.Abs(GridYSign)!=1) throw new ArgumentException("Enter finite frame dimensions and valid stage directions.");
    }
    public XY Pixels(double dx,double dy,int width,int height)
    {
        double x=dx*StageXSign,y=dy*StageYSign;
        double u=SwapStageAxes?y:x,v=(SwapStageAxes?x:y)*GridYSign;
        double angle=StageRotationDegrees*Math.PI/180,c=Math.Cos(angle),sn=Math.Sin(angle);
        return new((c*u-sn*v)*width/FovWidth,(sn*u+c*v)*height/FovHeight);
    }
}
public static class MosaicBlend
{
    public static void Validate(string mode)
    {
        if (mode is not ("sharp" or "feather" or "content")) throw new ArgumentException("Choose sharp, content-aware sharp or feathered seams.");
    }
}
public record StitchRequest(PlacementSettings? Placement = null, bool Refine = false, RegistrationOptions? Registration = null, string? BlendMode = null, bool RenderOnly = false,string? StitchId=null);
public record JobState(bool Busy, string Phase, int Done, int Total, string Message, string? ProjectId, string? Error)
{
    public bool Stopping { get; init; }
}
