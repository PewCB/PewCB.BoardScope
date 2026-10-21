using OpenCvSharp;

namespace BoardScope;

public sealed class NccRegistrar
{
    public RegistrationReport Run(ScanProject p,string folder,RegistrationOptions options,Action<string,int,int,string> progress,CancellationToken ct)
        => new TranslationRegistrar().Run(p,folder,options,progress,ct);
    public PairDiagnostics InspectPair(ScanProject p,string folder,int first,int second,CancellationToken ct=default,RegistrationOptions? trialOptions=null)
        => new TranslationRegistrar().InspectPair(p,folder,first,second,ct,trialOptions);

    public static (PairDiagnostics Diagnostics,FeatureLink? Link) Match(int i,int j,Tile a,Tile b,Mat imageA,Mat imageB,RegistrationOptions options,double pixelsPerMm,CancellationToken ct=default)
    {
        options.Validate();ct.ThrowIfCancellationRequested();
        double x=Math.Max(a.X,b.X),y=Math.Max(a.Y,b.Y);
        var overlap=new Rect2d(x,y,Math.Min(a.X+a.Width,b.X+b.Width)-x,Math.Min(a.Y+a.Height,b.Y+b.Height)-y);
        int width=(int)Math.Floor(overlap.Width)-2,height=(int)Math.Floor(overlap.Height)-2;
        var center=new Point2d(overlap.X+overlap.Width/2,overlap.Y+overlap.Height/2);
        double gate=2*options.MaxShiftMm*pixelsPerMm+double.Hypot(b.X+b.Width/2d-a.X-a.Width/2d,b.Y+b.Height/2d-a.Y-a.Height/2d)*(options.MaxScalePercent/100+options.MaxRotationDegrees*Math.PI/180);
        NccMeasurement? measurement=null;
        (PairDiagnostics,FeatureLink?) Result(bool accepted,string reason,FeatureLink? link=null,DebugFeatureMatch? constraint=null){
            var report=new PairReport(i,j,0,0,0,0,1,accepted,reason){Method="ncc",Ncc=measurement};
            return(new(i,j,report,new(overlap.X,overlap.Y,overlap.Width,overlap.Height,gate),0,0,[],[],[]){AnalysisOptions=options,TranslationConstraint=constraint},link);
        }
        if(width<64||height<64)return Result(false,"Predicted overlap is too small for NCC search");
        using var patchA=new Mat();using var patchB=new Mat();
        Cv2.GetRectSubPix(imageA,new(width,height),new((float)(center.X-a.X),(float)(center.Y-a.Y)),patchA,MatType.CV_32FC1.Value);
        Cv2.GetRectSubPix(imageB,new(width,height),new((float)(center.X-b.X),(float)(center.Y-b.Y)),patchB,MatType.CV_32FC1.Value);
        double factor=options.ImageFactor(a.Width,a.Height);
        using var smallA=new Mat();using var smallB=new Mat();
        if(factor<1){var size=new Size(Math.Max(16,(int)Math.Round(width*factor)),Math.Max(16,(int)Math.Round(height*factor)));Cv2.Resize(patchA,smallA,size,0,0,InterpolationFlags.Area);Cv2.Resize(patchB,smallB,size,0,0,InterpolationFlags.Area);}
        double fx=(factor<1?smallA.Width:width)/(double)width,fy=(factor<1?smallA.Height:height)/(double)height;
        using var preparedA=PhaseRegistrar.Prepare(factor<1?smallA:patchA,options.NccHighPass);
        using var preparedB=PhaseRegistrar.Prepare(factor<1?smallB:patchB,options.NccHighPass);
        if(preparedA.Empty()||preparedB.Empty())return Result(false,"Overlap has no measurable texture");
        // Keep a fixed template for every candidate shift, wholly inside real
        // source pixels. Search is bounded by both stage tolerance and overlap.
        const int border=8,minTemplate=48;
        int radiusX=Math.Min((int)Math.Ceiling(gate*fx),(preparedA.Width-minTemplate)/2-border);
        int radiusY=Math.Min((int)Math.Ceiling(gate*fy),(preparedA.Height-minTemplate)/2-border);
        if(radiusX<1||radiusY<1)return Result(false,"Overlap at the selected resolution is too small for NCC search");
        var rect=new Rect(border+radiusX,border+radiusY,preparedA.Width-2*(border+radiusX),preparedA.Height-2*(border+radiusY));
        using var template=new Mat(preparedA,rect);
        Cv2.MeanStdDev(template,out _,out var texture);
        if(texture.Val0<1e-6)return Result(false,"NCC template has no measurable texture");
        using var search=new Mat(preparedB,new Rect(border,border,preparedB.Width-2*border,preparedB.Height-2*border));
        using var scores=new Mat();Cv2.MatchTemplate(search,template,scores,TemplateMatchModes.CCoeffNormed);
        ct.ThrowIfCancellationRequested();
        var peak=FindPeak(scores,radiusX,radiusY,fx,fy,gate,ct);
        if(peak.Score< -1)return Result(false,"NCC did not produce a finite peak within the stage tolerance");
        var second=SecondPeak(scores,peak.Position,Math.Max(3,(int)Math.Ceiling(5*factor)),ct);
        double? gap=second is double competitor?peak.Score-competitor:null;
        var offset=RefinePeak(scores,peak.Position);
        double shiftX=(peak.Position.X-radiusX+offset.X)/fx,shiftY=(peak.Position.Y-radiusY+offset.Y)/fy;
        bool boundary=AtBoundary(scores,peak.Position);
        measurement=new(peak.Score,peak.Score,second,gap,-shiftX,-shiftY,b.X-a.X-shiftX,b.Y-a.Y-shiftY,(int)Math.Round(rect.Width/fx),(int)Math.Round(rect.Height/fy),radiusX/fx,radiusY/fy,boundary);
        if(peak.Score<options.NccMinCorrelation)return Result(false,"NCC peak correlation is below the threshold");
        if(boundary)return Result(false,"NCC peak is at the search boundary; verify overlap or increase the mechanical shift limit");
        if(double.Hypot(shiftX,shiftY)>gate)return Result(false,"NCC displacement exceeds stage tolerance");
        if(options.NccMinPeakGap>0&&gap.HasValue&&gap.Value<options.NccMinPeakGap)return Result(false,"Competing NCC peaks are too similar");
        // Check the actual interpolated patch rather than reporting the fitted
        // parabola's height as an image correlation.
        using var aligned=new Mat();Cv2.GetRectSubPix(preparedB,template.Size(),new((float)(rect.X+(rect.Width-1)/2d+shiftX*fx),(float)(rect.Y+(rect.Height-1)/2d+shiftY*fy)),aligned);
        double correlation=Correlation(template,aligned);
        if(!double.IsFinite(correlation))return Result(false,"Aligned NCC patch has no measurable texture");
        measurement=measurement with{Correlation=correlation};
        if(correlation<options.NccMinCorrelation)return Result(false,"Subpixel-aligned NCC correlation is below the threshold");
        var p=new Point2d(center.X-a.X,center.Y-a.Y);var q=new Point2d(center.X-b.X+shiftX,center.Y-b.Y+shiftY);
        var link=new FeatureLink(i,j,p,q,32*Math.Max(.1,correlation*correlation));
        ct.ThrowIfCancellationRequested();
        return Result(true,"Accepted normalized cross-correlation",link,new(new(p.X,p.Y),new(q.X,q.Y),"ncc-constraint"));
    }
    private static unsafe (double Score,Point Position) FindPeak(Mat scores,int radiusX,int radiusY,double fx,double fy,double gate,CancellationToken ct)
    {
        double best=-2;var position=new Point();int width=scores.Width,height=scores.Height;
        for(int y=0;y<height;y++){
            if(y%32==0)ct.ThrowIfCancellationRequested();var row=(float*)scores.Ptr(y);
            for(int x=0;x<width;x++){
                if(!float.IsFinite(row[x])||double.Hypot((x-radiusX)/fx,(y-radiusY)/fy)>gate)row[x]=-2;
                if(row[x]>best){best=row[x];position=new(x,y);}
            }
        }
        return(best,position);
    }
    private static unsafe double? SecondPeak(Mat scores,Point peak,int exclude,CancellationToken ct)
    {
        double second=-2;int width=scores.Width,height=scores.Height;
        for(int y=0;y<height;y++){
            if(y%32==0)ct.ThrowIfCancellationRequested();var row=(float*)scores.Ptr(y);
            for(int x=0;x<width;x++)if(double.Hypot(x-peak.X,y-peak.Y)>exclude&&row[x]>second)second=row[x];
        }
        return second< -1?null:second;
    }
    private static Point2d RefinePeak(Mat scores,Point p)
    {
        if(p.X<1||p.Y<1||p.X>=scores.Width-1||p.Y>=scores.Height-1)return new();
        double f(int x,int y)=>scores.At<float>(p.Y+y,p.X+x);
        if(Enumerable.Range(-1,3).Any(y=>Enumerable.Range(-1,3).Any(x=>f(x,y)< -1)))return new();
        double xx=f(1,0)+f(-1,0)-2*f(0,0),yy=f(0,1)+f(0,-1)-2*f(0,0),xy=(f(1,1)-f(-1,1)-f(1,-1)+f(-1,-1))/4;
        double gx=(f(1,0)-f(-1,0))/2,gy=(f(0,1)-f(0,-1))/2,det=xx*yy-xy*xy;
        if(xx>=0||yy>=0||det<1e-12)return new();
        return new(Math.Clamp((xy*gy-yy*gx)/det,-.5,.5),Math.Clamp((xy*gx-xx*gy)/det,-.5,.5));
    }
    private static bool AtBoundary(Mat scores,Point p) => p.X<1||p.Y<1||p.X>=scores.Width-1||p.Y>=scores.Height-1||Enumerable.Range(-1,3).Any(y=>Enumerable.Range(-1,3).Any(x=>scores.At<float>(p.Y+y,p.X+x)< -1));
    private static double Correlation(Mat a,Mat b)
    {
        Cv2.MeanStdDev(a,out var ma,out var sa);Cv2.MeanStdDev(b,out var mb,out var sb);
        if(sa.Val0<1e-6||sb.Val0<1e-6)return double.NaN;
        using var aa=new Mat();using var bb=new Mat();Cv2.Subtract(a,new Scalar(ma.Val0),aa);Cv2.Subtract(b,new Scalar(mb.Val0),bb);
        return Math.Clamp(aa.Dot(bb)/(aa.Total()*sa.Val0*sb.Val0),-1,1);
    }
}
