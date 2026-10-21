using BoardScope;
using OpenCvSharp;

static class AutomaticCalibrationChecks
{
    private static void Check(bool condition,string message){if(!condition)throw new Exception(message);Console.WriteLine("PASS "+message);}
    public static void Run()
    {
        foreach(double angle in new[]{-35.0,12.5,95.0,178.0})foreach(bool swapped in new[]{false,true})foreach(int xs in new[]{-1,1})foreach(int ys in new[]{-1,1})
        {
            var s=new ScanSettings {FovWidth=14,FovHeight=9,StageRotationDegrees=angle,SwapStageAxes=swapped,StageXSign=xs,StageYSign=ys,MaxTravelX=100,MaxTravelY=100};
            var stage=Planner.GridToStage(s,3.4,2.1);var pixels=PlacementSettings.From(s).Pixels(stage.X,stage.Y,1400,900);
            if(Math.Abs(pixels.X-340)>1e-8 || Math.Abs(pixels.Y-210)>1e-8)throw new Exception("Calibrated angle mapping does not round-trip.");
            var plan=Planner.Create(s);var first=plan.Moves[0];var next=plan.Moves[1];var advance=PlacementSettings.From(s).Pixels(next.StageDx-first.StageDx,next.StageDy-first.StageDy,1400,900);
            if(Math.Abs(advance.X-50)>1e-8 || Math.Abs(advance.Y)>1e-8)throw new Exception("Row preload ignores calibrated angle.");
        }
        Console.WriteLine("PASS calibrated angle round-trips planning and placement, including row preloads, across all axis signs and swaps");
        try{AutomaticCalibration.Target(new(0,0),new ScanSettings {MaxTravelX=.1},.5);throw new Exception("Travel limit accepted.");}catch(ArgumentException){}
        var target=AutomaticCalibration.Target(new(1,2),new ScanSettings {StageRotationDegrees=30},7);
        Check(Math.Abs(double.Hypot(target.X-1,target.Y-2)-7)<1e-9,"second-pair distance remains a half frame when correcting both stage axes");
        using var firstImage=new Mat(900,1400,MatType.CV_8UC3,new Scalar(24,30,35));var random=new Random(772);
        for(int i=0;i<1800;i++){var center=new Point(random.Next(10,1390),random.Next(10,890));Cv2.Circle(firstImage,center,random.Next(2,8),new Scalar(random.Next(70,250),random.Next(70,250),random.Next(70,250)),random.Next(1,3));}
        static Mat Shift(Mat source,double x,double y,double scale=1)
        {
            using var transform=new Mat(2,3,MatType.CV_64F);transform.Set(0,0,scale);transform.Set(0,1,0.0);transform.Set(0,2,-x);transform.Set(1,0,0.0);transform.Set(1,1,scale);transform.Set(1,2,-y);
            var shifted=new Mat();Cv2.WarpAffine(source,shifted,transform,source.Size());return shifted;
        }
        const double trueAngle=12,ppm=100;
        using var coarseImage=Shift(firstImage,50*Math.Cos(trueAngle*Math.PI/180),50*Math.Sin(trueAngle*Math.PI/180));
        var coarse=AutomaticCalibration.Match(firstImage,coarseImage,new(0,0),new(.5,0),1,0,CancellationToken.None);
        Check(Math.Abs(coarse.WidthMm-14)<.08 && Math.Abs(coarse.RotationDegrees-trueAngle)<.3,"0.5 mm native-resolution phase pair recovers frame size and camera/stage angle");
        double distance=coarse.WidthMm/2;
        var fineSettings=new ScanSettings {StageRotationDegrees=coarse.RotationDegrees};var move=Planner.GridToStage(fineSettings,distance,0);
        double residualAngle=(trueAngle-coarse.RotationDegrees)*Math.PI/180;
        using var fineImage=Shift(firstImage,distance*ppm*Math.Cos(residualAngle),distance*ppm*Math.Sin(residualAngle));
        var fine=AutomaticCalibration.Match(firstImage,fineImage,new(0,0),move,1,coarse.RotationDegrees,CancellationToken.None,new XY(700,0));
        AutomaticCalibration.ValidateFine(coarse,fine);
        Check(Math.Abs(fine.WidthMm-14)<.02 && Math.Abs(fine.RotationDegrees-trueAngle)<.05 && fine.VerifiedRegions>=3 && fine.PhaseResponse>.1 && fine.Correlation>.3,"half-overlap phase pair refines scale and angle using the full measured diagonal stage displacement");
        using var reverse=Shift(firstImage,-700,0);
        var backwards=AutomaticCalibration.Match(firstImage,reverse,new(0,0),new(-7,0),-1,trueAngle,CancellationToken.None);
        Check(Math.Abs(backwards.WidthMm-14)<.02 && Math.Abs(backwards.RotationDegrees-trueAngle)<.05,"negative calibration direction preserves the angle convention");
        using var largerShift=Shift(firstImage,728.35,-2.6);
        var beyondHalf=AutomaticCalibration.Match(firstImage,largerShift,new(0,0),new(7.2835,0),1,0,CancellationToken.None,new XY(700,0));
        Console.WriteLine($"Phase calibration beyond half: offset {beyondHalf.OffsetX:F6}, {beyondHalf.OffsetY:F6}; width {beyondHalf.WidthMm:F6}; response {beyondHalf.PhaseResponse:F4}; correlation {beyondHalf.Correlation:F4}");
        Check(Math.Abs(beyondHalf.WidthMm-14)<.02&&Math.Abs(beyondHalf.OffsetX-728.35)<.35&&Math.Abs(beyondHalf.OffsetY+2.6)<.35,"predicted-overlap phase matching resolves subpixel shifts beyond half a frame without FFT direction wraparound");
        using var faint=firstImage.Clone();Cv2.ConvertScaleAbs(firstImage,faint,.035,120);
        using var faintShift=Shift(faint,53.4,-3.7);
        var faintMatch=AutomaticCalibration.Match(faint,faintShift,new(0,0),new(double.Hypot(53.4,3.7)/100,0),1,0,CancellationToken.None);
        Console.WriteLine($"Faint phase calibration: offset {faintMatch.OffsetX:F6}, {faintMatch.OffsetY:F6}; width {faintMatch.WidthMm:F6}; response {faintMatch.PhaseResponse:F4}; correlation {faintMatch.Correlation:F4}");
        Check(Math.Abs(faintMatch.WidthMm-14)<.1&&faintMatch.VerifiedRegions>=3,"initial phase calibration measures faint specks without detecting feature points");
        double faintAngle=Math.Atan2(-3.7,53.4)*180/Math.PI,faintDistance=faintMatch.WidthMm/2,faintResidual=(faintAngle-faintMatch.RotationDegrees)*Math.PI/180;
        using var faintFineImage=Shift(faint,faintDistance*100*Math.Cos(faintResidual),faintDistance*100*Math.Sin(faintResidual));
        var faintMove=Planner.GridToStage(new ScanSettings{StageRotationDegrees=faintMatch.RotationDegrees},faintDistance,0);
        var faintFine=AutomaticCalibration.Match(faint,faintFineImage,new(0,0),faintMove,1,faintMatch.RotationDegrees,CancellationToken.None,new XY(700,0));
        AutomaticCalibration.ValidateFine(faintMatch,faintFine);
        Check(Math.Abs(faintFine.WidthMm-14)<.02&&Math.Abs(faintFine.RotationDegrees-faintAngle)<.05,"half-overlap phase pair refines faint-speck calibration to final scale and angle accuracy");
        using var wrongDirection=Shift(firstImage,700,0);
        var blindHalf=AutomaticCalibration.Match(firstImage,wrongDirection,new(0,0),new(7,0),1,0,CancellationToken.None);
        Check(Math.Abs(blindHalf.OffsetX-700)<.35&&Math.Abs(backwards.OffsetX+700)<.35,"linear phase search resolves both exact half-frame directions");
        using var flat=new Mat(firstImage.Size(),MatType.CV_8UC3,Scalar.All(90));
        try{AutomaticCalibration.Match(flat,flat,new(0,0),new(.5,0),1,0,CancellationToken.None);throw new Exception("Featureless pair accepted.");}catch(InvalidOperationException){}
        using var changed=Shift(firstImage,50,0,.985);
        try{AutomaticCalibration.Match(firstImage,changed,new(0,0),new(.5,0),1,0,CancellationToken.None);throw new Exception("Magnification change accepted.");}catch(InvalidOperationException){}
        using var cancelled=new CancellationTokenSource();cancelled.Cancel();
        try{AutomaticCalibration.Match(firstImage,coarseImage,new(0,0),new(.5,0),1,0,cancelled.Token);throw new Exception("Cancelled match ran.");}catch(OperationCanceledException){}
        try{AutomaticCalibration.ValidateFine(coarse,fine with {WidthMm=coarse.WidthMm*2});throw new Exception("Disagreement accepted.");}catch(InvalidOperationException){}
        Check(true,"automatic phase calibration rejects textureless pairs, magnification changes, cancellation and inconsistent estimates");
    }
}
