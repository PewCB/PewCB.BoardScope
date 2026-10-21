using OpenCvSharp;

namespace BoardScope;

public record RegistrationOptions
{
    public string Method { get; init; } = "sift";
    public double PhaseMinResponse { get; init; } = .15;
    public double PhaseMinCorrelation { get; init; } = .3;
    public bool PhaseHighPass { get; init; } = true;
    public double NccMinCorrelation { get; init; } = .5;
    public double NccMinPeakGap { get; init; }
    public bool NccHighPass { get; init; } = true;
    public static string MethodName(string method) => method switch {"parallax"=>"Parallax-aware matching","hybrid"=>"Hybrid matching","phase"=>"Phase correlation","ncc"=>"Normalized cross-correlation",_=>"Feature matching"};
    public double MaxRotationDegrees { get; init; } = 3;
    public double MaxScalePercent { get; init; } = 3;
    public double MaxShiftMm { get; init; } = .25;
    // Zero selects the original pixels, without a size-dependent reduction.
    public int WorkingSize { get; init; }
    public int MaxFeatures { get; init; } = 6000;
    public double ContrastThreshold { get; init; } = .04;
    public bool EnhanceLocalContrast { get; init; }
    public string FeatureKey() => $"c{ContrastThreshold.ToString("R",System.Globalization.CultureInfo.InvariantCulture)}-e{(EnhanceLocalContrast?1:0)}";
    public double ImageFactor(int width,int height) => WorkingSize==0 ? 1 : Math.Min(1,WorkingSize/(double)Math.Max(width,height));
    public void Validate()
    {
        if(Method is not ("sift" or "phase" or "ncc" or "hybrid" or "parallax"))throw new ArgumentException("Choose parallax-aware, hybrid, feature matching, phase correlation or normalized cross-correlation for refinement.");
        if(!double.IsFinite(PhaseMinResponse)||PhaseMinResponse is < .01 or > 1||!double.IsFinite(PhaseMinCorrelation)||PhaseMinCorrelation is < 0 or > 1)throw new ArgumentException("Phase response must be 0.01–1 and aligned correlation must be 0–1.");
        if(!double.IsFinite(NccMinCorrelation)||NccMinCorrelation is < .01 or > 1||!double.IsFinite(NccMinPeakGap)||NccMinPeakGap is < 0 or > 1)throw new ArgumentException("NCC minimum correlation must be 0.01–1 and peak gap must be 0–1.");
        if (!double.IsFinite(MaxRotationDegrees) || MaxRotationDegrees is < .01 or > 15 || !double.IsFinite(MaxScalePercent) || MaxScalePercent is < .01 or > 15 || !double.IsFinite(MaxShiftMm) || MaxShiftMm is < .005 or > 5 || (WorkingSize!=0 && (WorkingSize is < 800 or > 12000)) || MaxFeatures is < 500 or > 20000 || !double.IsFinite(ContrastThreshold) || ContrastThreshold is < .0001 or > .2)
            throw new ArgumentException("Check refinement limits: rotation 0.01–15°, scale 0.01–15%, shift 0.005–5 mm; use full resolution or an alignment size of 800–12000 px, 500–20000 features, and a detection contrast threshold of 0.0001–0.2.");
    }
}
public record Similarity(double A, double B, double X, double Y)
{
    public double Scale => Math.Sqrt(A*A+B*B);
    public double Degrees => Math.Atan2(B,A)*180/Math.PI;
    public XY Apply(double x,double y) => new(X+A*x-B*y,Y+B*x+A*y);
    public Rect2d Bounds(int width,int height)
    {
        var points=new[]{Apply(0,0),Apply(width,0),Apply(0,height),Apply(width,height)};
        double left=points.Min(p=>p.X),top=points.Min(p=>p.Y);
        return new(left,top,points.Max(p=>p.X)-left,points.Max(p=>p.Y)-top);
    }
    public static Similarity For(Tile tile) => tile.Registration?.Transform ?? new(1,0,tile.X,tile.Y);
}
public record TileRegistration(Similarity Transform,double DeltaX,double DeltaY,int Links,double ErrorPixels,string Status)
{
    public double? MechanicalDeltaX { get; init; }
    public double? MechanicalDeltaY { get; init; }
}
// This transforms stage-derived centres, never the pixels within a tile.
public record SharedStageCorrection(double Scale,double RotationDegrees,double PivotX,double PivotY);
public record RegistrationSolution(TileRegistration[] Tiles,SharedStageCorrection StageCorrection);
public record MosaicVersion(string Dzi,int Width,int Height,double OriginX,double OriginY)
{
    public string? Label { get; init; }
}
public record PairReport(int First,int Second,int Matches,int Inliers,double ErrorPixels,double RotationDegrees,double Scale,bool Accepted,string Reason)
{
    public string Method { get; init; } = "sift";
    public PhaseMeasurement? Phase { get; init; }
    public NccMeasurement? Ncc { get; init; }
    public string? DebugFile { get; init; }
    public double? GlobalErrorPixels { get; init; }
    public string? AttemptLabel { get; init; }
    public List<PairAttempt>? Attempts { get; init; }
    public ParallaxMeasurement? Parallax { get; init; }
}
public record PatchObservation(int Patch,double Resolution,string Method,XY First,XY Second,double Confidence);
public record OffsetHypothesis(int Id,string Label,double DeltaX,double DeltaY,int Patches,double Confidence,double SpreadPixels,double? GlobalErrorPixels);
public record ParallaxMeasurement(int PatchCount,int ValidPatches,int? SelectedHypothesis,int GlobalRetries,List<OffsetHypothesis> Hypotheses)
{
    public int GlobalChanges { get; init; }
}
public record PairAttempt(string Label,RegistrationOptions Options,PairReport Report);
public record PhaseMeasurement(double Response,double? Correlation,double CorrectionX,double CorrectionY,double DeltaX,double DeltaY,int Width,int Height);
public record NccMeasurement(double PeakCorrelation,double Correlation,double? SecondCorrelation,double? PeakGap,double CorrectionX,double CorrectionY,double DeltaX,double DeltaY,int Width,int Height,double SearchRadiusX,double SearchRadiusY,bool AtSearchBoundary);
public record DebugOverlap(double X,double Y,double Width,double Height,double SearchMargin);
public record DebugFeatureMatch(XY First,XY Second,string Status);
public record PairDiagnostics(int First,int Second,PairReport Report,DebugOverlap Overlap,int TotalFeaturesFirst,int TotalFeaturesSecond,XY[] FeaturesFirst,XY[] FeaturesSecond,List<DebugFeatureMatch> Matches,bool Reconstructed=false)
{
    public PairReport? ReconstructionReport { get; init; }
    public RegistrationOptions? AnalysisOptions { get; init; }
    public bool Trial { get; init; }
    public DebugFeatureMatch? TranslationConstraint { get; init; }
    public List<PatchObservation>? PatchObservations { get; init; }
}
public record PairRecheckRequest(string Version,RegistrationOptions Options,string? StitchId=null);
public record RegistrationReport(RegistrationOptions Options,DateTimeOffset Created,int AcceptedPairs,int RejectedPairs,int SupportedTiles,int LimitedTiles,double ErrorPixels,string ReportFile,List<PairReport> Pairs)
{
    public SharedStageCorrection? StageCorrection { get; init; }
    public int? FeaturePairs { get; init; }
    public int? TranslationPairs { get; init; }
}
public record FeatureLink(int First,int Second,Point2d P,Point2d Q,double Weight=1);
