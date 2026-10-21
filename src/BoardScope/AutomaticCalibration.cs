using OpenCvSharp;

namespace BoardScope;

public record CalibrationMeasurement(double WidthMm,double HeightMm,double RotationDegrees,double MoveMm,double OffsetX,double OffsetY,double PhaseResponse,double Correlation,int VerifiedRegions,double ErrorPixels);
public record AutomaticCalibrationRequest(ScanSettings Settings,int Direction=1);
public record CalibrationApproval(ScanSettings Settings);
public record CalibrationContext(string? DeviceId,int Width,int Height,int PositionEpoch,ScanSettings Settings);
public record AutomaticCalibrationResult(string Id,DateTimeOffset Created,int Width,int Height,CalibrationContext Context,CalibrationMeasurement Coarse,CalibrationMeasurement Fine,XY FirstPosition,XY SecondPosition);

public static class AutomaticCalibration
{
    public static double WrapAngle(double degrees) => (degrees+540)%360-180;
    public static XY Target(XY start,ScanSettings settings,double distance,XY? reference=null)
    {
        var delta=Planner.GridToStage(settings,distance,0);
        var target=new XY(start.X+delta.X,start.Y+delta.Y);
        var origin=reference??start;
        if(Math.Abs(target.X-origin.X)>settings.MaxTravelX || Math.Abs(target.Y-origin.Y)>settings.MaxTravelY)
            throw new ArgumentException("Calibration move exceeds the configured travel limit. Choose the other direction or adjust the limit.");
        return target;
    }
    public static CalibrationMeasurement Match(Mat first,Mat second,XY firstPosition,XY secondPosition,int direction,double priorAngle,CancellationToken ct,XY? expectedOffset=null)
    {
        if(first.Empty() || second.Empty() || first.Size()!=second.Size())throw new InvalidOperationException("Calibration image dimensions changed. Start again.");
        ct.ThrowIfCancellationRequested();
        using var grayFirst=new Mat();using var graySecond=new Mat();
        Cv2.CvtColor(first,grayFirst,ColorConversionCodes.BGR2GRAY);Cv2.CvtColor(second,graySecond,ColorConversionCodes.BGR2GRAY);
        using var a=PhaseRegistrar.Prepare(grayFirst,true);using var b=PhaseRegistrar.Prepare(graySecond,true);
        if(a.Empty()||b.Empty())throw new InvalidOperationException("No measurable image texture for phase correlation. Aim at a flat area with visible detail and try again.");
        double x,y;
        if(expectedOffset is {} predicted){x=predicted.X;y=predicted.Y;}
        else
        {
            // Zero padding makes this a linear search: shifts around half a frame
            // cannot alias to the opposite direction as they do in a circular FFT.
            var initial=PhaseShift(a,b,out double initialResponse,linear:true);ct.ThrowIfCancellationRequested();
            if(!double.IsFinite(initialResponse)||initialResponse<.05||!double.IsFinite(initial.X)||!double.IsFinite(initial.Y))
                throw new InvalidOperationException("Phase correlation could not find a reliable initial offset. Check overlap and image detail.");
            x=-initial.X;y=-initial.Y;
        }
        double response=0;
        for(int pass=0;pass<3;pass++)
        {
            ct.ThrowIfCancellationRequested();
            var patches=Overlap(a,b,x,y);using var pa=patches.First;using var pb=patches.Second;
            var shift=PhaseShift(pa,pb,out response);
            if(!double.IsFinite(response)||response<.1||!double.IsFinite(shift.X)||!double.IsFinite(shift.Y)||Math.Abs(shift.X)>pa.Width*.35||Math.Abs(shift.Y)>pa.Height*.35)
                throw new InvalidOperationException("Phase correlation is unreliable in the overlap. Check image detail and keep the camera fixed.");
            x-=shift.X;y-=shift.Y;
            if(double.Hypot(shift.X,shift.Y)<.03)break;
        }
        var aligned=Overlap(a,b,x,y);using var alignedA=aligned.First;using var alignedB=aligned.Second;
        double correlation=Correlation(alignedA,alignedB);
        if(correlation<.3)throw new InvalidOperationException("The images do not agree after phase alignment. Keep magnification fixed and try a flatter board area.");
        var residuals=new List<double>();
        for(int row=0;row<2;row++)for(int col=0;col<2;col++)
        {
            ct.ThrowIfCancellationRequested();
            int w=alignedA.Width/2,h=alignedA.Height/2;
            using var partA=new Mat(alignedA,new Rect(col*w,row*h,w,h));using var partB=new Mat(alignedB,new Rect(col*w,row*h,w,h));
            var residual=PhaseShift(partA,partB,out double localResponse);
            if(localResponse<.1||Correlation(partA,partB)<.3)continue;
            if(!double.IsFinite(residual.X)||!double.IsFinite(residual.Y)||double.Hypot(residual.X,residual.Y)>2.5)
                throw new InvalidOperationException("Different image regions disagree on translation. Keep camera rotation and magnification fixed, and choose a flat board area.");
            residuals.Add(double.Hypot(residual.X,residual.Y));
        }
        if(residuals.Count<3)throw new InvalidOperationException("Too little reliable texture across the overlap for phase calibration. Aim at more board detail.");
        double error=Median(residuals);
        double pixels=double.Hypot(x,y),mm=double.Hypot(secondPosition.X-firstPosition.X,secondPosition.Y-firstPosition.Y);
        if(pixels<10 || mm<.05 || error>1.5)throw new InvalidOperationException("The measured movement is too small or the phase alignment is unreliable.");
        double width=first.Width*mm/pixels,height=first.Height*mm/pixels;
        if(width is <.05 or >1000 || height is <.05 or >1000)throw new InvalidOperationException("Calculated frame size is outside the supported range.");
        double angle=WrapAngle(priorAngle+Math.Atan2(direction*y,direction*x)*180/Math.PI);
        return new(width,height,angle,mm,x,y,response,correlation,residuals.Count,error);
    }
    private static Point2d PhaseShift(Mat a,Mat b,out double response,bool linear=false)
    {
        // OpenCV's FFT peak center has a half-pixel bias for odd transform sizes.
        // Explicit even, FFT-friendly padding keeps repeated subpixel refinements stable.
        static int Size(int n){int result=Cv2.GetOptimalDFTSize(n);while(result%2!=0)result=Cv2.GetOptimalDFTSize(result+1);return result;}
        int width=Size(a.Width*(linear?2:1)),height=Size(a.Height*(linear?2:1));
        using var sourceA=a.Clone();using var sourceB=b.Clone();
        if(!linear){using var window=new Mat();Cv2.CreateHanningWindow(window,a.Size(),MatType.CV_32FC1);Cv2.Multiply(sourceA,window,sourceA);Cv2.Multiply(sourceB,window,sourceB);}
        using var paddedA=new Mat();using var paddedB=new Mat();using var noWindow=new Mat();
        Cv2.CopyMakeBorder(sourceA,paddedA,0,height-a.Height,0,width-a.Width,BorderTypes.Constant,Scalar.All(0));
        Cv2.CopyMakeBorder(sourceB,paddedB,0,height-b.Height,0,width-b.Width,BorderTypes.Constant,Scalar.All(0));
        return Cv2.PhaseCorrelate(paddedA,paddedB,noWindow,out response);
    }
    private static (Mat First,Mat Second) Overlap(Mat a,Mat b,double x,double y)
    {
        double left=Math.Max(0,x)+8,top=Math.Max(0,y)+8,right=Math.Min(a.Width,b.Width+x)-8,bottom=Math.Min(a.Height,b.Height+y)-8;
        int width=(int)Math.Floor(right-left),height=(int)Math.Floor(bottom-top);
        if(width<64||height<64)throw new InvalidOperationException("Too little overlap for phase calibration. Use lower magnification or manual frame calibration.");
        var center=new Point2f((float)(left+(width-1)/2d),(float)(top+(height-1)/2d));
        var first=new Mat();var second=new Mat();
        try{Cv2.GetRectSubPix(a,new Size(width,height),center,first);Cv2.GetRectSubPix(b,new Size(width,height),new Point2f(center.X-(float)x,center.Y-(float)y),second);return(first,second);}
        catch{first.Dispose();second.Dispose();throw;}
    }
    private static double Correlation(Mat a,Mat b)
    {
        Cv2.MeanStdDev(a,out var ma,out var sa);Cv2.MeanStdDev(b,out var mb,out var sb);
        if(sa.Val0<1e-6||sb.Val0<1e-6)return -1;
        double result=(a.Dot(b)/a.Total()-ma.Val0*mb.Val0)/(sa.Val0*sb.Val0);
        return double.IsFinite(result)?Math.Clamp(result,-1,1):-1;
    }
    public static void ValidateFine(CalibrationMeasurement coarse,CalibrationMeasurement fine)
    {
        if(Math.Abs(fine.WidthMm/coarse.WidthMm-1)>.25 || Math.Abs(WrapAngle(fine.RotationDegrees-coarse.RotationDegrees))>3)
            throw new InvalidOperationException("The two measurements disagree. Check mechanical slack and image detail, then run calibration again.");
    }
    private static double Median(IEnumerable<double> values){var a=values.Order().ToArray();return a.Length%2==0?(a[a.Length/2-1]+a[a.Length/2])/2:a[a.Length/2];}
}
