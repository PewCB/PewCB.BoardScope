using OpenCvSharp;

namespace BoardScope;

public sealed class PhaseRegistrar
{
    public RegistrationReport Run(ScanProject p,string folder,RegistrationOptions options,Action<string,int,int,string> progress,CancellationToken ct)
        => new TranslationRegistrar().Run(p,folder,options,progress,ct);
    public PairDiagnostics InspectPair(ScanProject p,string folder,int first,int second,CancellationToken ct=default,RegistrationOptions? trialOptions=null)
        => new TranslationRegistrar().InspectPair(p,folder,first,second,ct,trialOptions);
    private static Rect2d Overlap(Tile a,Tile b){double x=Math.Max(a.X,b.X),y=Math.Max(a.Y,b.Y);return new(x,y,Math.Min(a.X+a.Width,b.X+b.Width)-x,Math.Min(a.Y+a.Height,b.Y+b.Height)-y);}
    public static (PairDiagnostics Diagnostics,FeatureLink? Link) Match(int i,int j,Tile a,Tile b,Mat imageA,Mat imageB,RegistrationOptions options,double pixelsPerMm,CancellationToken ct=default)
    {
        options.Validate();ct.ThrowIfCancellationRequested();
        var r=Overlap(a,b);int width=(int)Math.Floor(r.Width)-2,height=(int)Math.Floor(r.Height)-2;
        var center=new Point2d(r.X+r.Width/2,r.Y+r.Height/2);
        double gate=2*options.MaxShiftMm*pixelsPerMm+double.Hypot(b.X+b.Width/2d-a.X-a.Width/2d,b.Y+b.Height/2d-a.Y-a.Height/2d)*(options.MaxScalePercent/100+options.MaxRotationDegrees*Math.PI/180);
        PhaseMeasurement? measurement=null;
        (PairDiagnostics,FeatureLink?) Result(bool accepted,string reason,FeatureLink? link=null,DebugFeatureMatch? constraint=null){
            var report=new PairReport(i,j,0,0,0,0,1,accepted,reason){Method="phase",Phase=measurement};
            return(new(i,j,report,new(r.X,r.Y,r.Width,r.Height,gate),0,0,[],[],[]){AnalysisOptions=options,TranslationConstraint=constraint},link);
        }
        if(width<32||height<32)return Result(false,"Predicted overlap is too small for phase correlation");
        using var patchA=new Mat();using var patchB=new Mat();
        Cv2.GetRectSubPix(imageA,new Size(width,height),new Point2f((float)(center.X-a.X),(float)(center.Y-a.Y)),patchA,MatType.CV_32FC1.Value);
        Cv2.GetRectSubPix(imageB,new Size(width,height),new Point2f((float)(center.X-b.X),(float)(center.Y-b.Y)),patchB,MatType.CV_32FC1.Value);
        double factor=options.ImageFactor(a.Width,a.Height);
        using var smallA=new Mat();using var smallB=new Mat();
        if(factor<1){var size=new Size(Math.Max(16,(int)Math.Round(width*factor)),Math.Max(16,(int)Math.Round(height*factor)));Cv2.Resize(patchA,smallA,size,0,0,InterpolationFlags.Area);Cv2.Resize(patchB,smallB,size,0,0,InterpolationFlags.Area);}
        double fx=(factor<1?smallA.Width:width)/(double)width,fy=(factor<1?smallA.Height:height)/(double)height;
        using var preparedA=Prepare(factor<1?smallA:patchA,options.PhaseHighPass);using var preparedB=Prepare(factor<1?smallB:patchB,options.PhaseHighPass);
        if(preparedA.Empty()||preparedB.Empty())return Result(false,"Overlap has no measurable texture");
        using var window=new Mat();Cv2.CreateHanningWindow(window,preparedA.Size(),MatType.CV_32FC1);
        // phaseCorrelate can modify its arrays when a window is supplied.
        using var fftA=preparedA.Clone();using var fftB=preparedB.Clone();
        var shift=Cv2.PhaseCorrelate(fftA,fftB,window,out double response);
        ct.ThrowIfCancellationRequested();double sx=shift.X/fx,sy=shift.Y/fy;
        if(!double.IsFinite(response)||!double.IsFinite(sx)||!double.IsFinite(sy))return Result(false,"Phase correlation did not produce a finite offset");
        measurement=new(response,null,-sx,-sy,b.X-a.X-sx,b.Y-a.Y-sy,width,height);
        if(response<options.PhaseMinResponse)return Result(false,"Phase response is below the confidence threshold");
        if(double.Hypot(sx,sy)>gate)return Result(false,"Phase displacement exceeds stage tolerance");
        if(Math.Abs(shift.X)>preparedA.Width*.4||Math.Abs(shift.Y)>preparedA.Height*.4)return Result(false,"Phase offset is too close to FFT wraparound");
        double correlation=AlignedCorrelation(preparedA,preparedB,shift);
        measurement=measurement with {Correlation=correlation};
        if(!double.IsFinite(correlation)||correlation<options.PhaseMinCorrelation)return Result(false,"Shifted overlap correlation is below the threshold");
        var p=new Point2d(center.X-a.X,center.Y-a.Y);var q=new Point2d(center.X-b.X+sx,center.Y-b.Y+sy);
        // One measured translation per overlap, not synthetic detected features.
        double weight=32*Math.Clamp(response,.01,1)*Math.Max(.1,correlation*correlation);
        var link=new FeatureLink(i,j,p,q,weight);
        return Result(true,"Accepted phase correlation",link,new(new(p.X,p.Y),new(q.X,q.Y),"phase-constraint"));
    }
    internal static Mat Prepare(Mat patch,bool highPass)
    {
        var image=new Mat();patch.ConvertTo(image,MatType.CV_32FC1,1/255d);
        if(highPass){using var smooth=new Mat();Cv2.GaussianBlur(image,smooth,new Size(0,0),5);Cv2.Subtract(image,smooth,image);}
        Cv2.MeanStdDev(image,out var mean,out var deviation);
        if(deviation.Val0<=1e-6){image.Dispose();return new Mat();}
        Cv2.Subtract(image,new Scalar(mean.Val0),image);Cv2.Divide(image,new Scalar(deviation.Val0),image);return image;
    }
    private static double AlignedCorrelation(Mat a,Mat b,Point2d shift)
    {
        int border=8,left=border+(int)Math.Ceiling(Math.Max(0,-shift.X)),top=border+(int)Math.Ceiling(Math.Max(0,-shift.Y));
        int right=a.Width-border-(int)Math.Ceiling(Math.Max(0,shift.X)),bottom=a.Height-border-(int)Math.Ceiling(Math.Max(0,shift.Y));
        if(right-left<16||bottom-top<16)return -1;
        var size=new Size(right-left,bottom-top);var center=new Point2f((left+right-1)/2f,(top+bottom-1)/2f);
        using var pa=new Mat();using var pb=new Mat();
        Cv2.GetRectSubPix(a,size,center,pa);Cv2.GetRectSubPix(b,size,new Point2f(center.X+(float)shift.X,center.Y+(float)shift.Y),pb);
        Cv2.MeanStdDev(pa,out var ma,out var sa);Cv2.MeanStdDev(pb,out var mb,out var sb);
        if(sa.Val0<1e-6||sb.Val0<1e-6)return -1;
        Cv2.Subtract(pa,new Scalar(ma.Val0),pa);Cv2.Subtract(pb,new Scalar(mb.Val0),pb);
        return Math.Clamp(pa.Dot(pb)/(pa.Total()*sa.Val0*sb.Val0),-1,1);
    }
}
