using BoardScope;
using OpenCvSharp;
using Stitcher=BoardScope.Stitcher;

static class NccChecks
{
    static void Check(bool condition,string message){if(!condition)throw new Exception(message);Console.WriteLine("PASS "+message);}
    static Mat Board(int width,int height,bool faint=false)
    {
        var board=new Mat(height,width,MatType.CV_8UC1,Scalar.All(128));var random=new Random(729);
        for(int k=0;k<width*height/450;k++)Cv2.Circle(board,new(random.Next(8,width-8),random.Next(8,height-8)),random.Next(1,5),Scalar.All(128+(k%2==0?1:-1)*random.Next(faint?4:25,faint?10:100)),-1);
        Cv2.GaussianBlur(board,board,new Size(3,3),.6);return board;
    }
    static Mat Crop(Mat board,double x,double y,int w,int h){var image=new Mat();Cv2.GetRectSubPix(board,new(w,h),new((float)(x+(w-1)/2d),(float)(y+(h-1)/2d)),image);return image;}
    public static void Run()
    {
        using var board=Board(1800,1500);var options=new RegistrationOptions{Method="ncc"};
        Check(options.NccMinPeakGap==0,"NCC competing-peak rejection is off by default");
        foreach(var (dx,dy) in new[]{(9d,0d),(-9d,0d),(0d,7d),(0d,-7d),(8d,-6d),(3.4,-2.7)})
        foreach(int size in new[]{0,800})
        {
            var a=new Tile{Width=1000,Height=640};var b=new Tile{Width=1000,Height=640,X=320.3,Y=140.6};
            using var ia=Crop(board,100,100,1000,640);using var ib=Crop(board,100+b.X+dx,100+b.Y+dy,1000,640);
            var (d,link)=NccRegistrar.Match(0,1,a,b,ia,ib,options with{WorkingSize=size},100);
            var p=d.Report.Ncc!;
            Console.WriteLine($"NCC ({dx},{dy}) size {size}: {d.Report.Reason}, score {p?.Correlation:F3}, gap {p?.PeakGap:F3}, correction {p?.CorrectionX:F3},{p?.CorrectionY:F3}");
            Check(d.Report.Accepted&&link!=null&&double.Hypot(p!.CorrectionX-dx,p.CorrectionY-dy)<.6,$"NCC recovers signed subpixel offset ({dx},{dy}), resolution {size}");
            Check(d.FeaturesFirst.Length==0&&d.Matches.Count==0&&d.Report.Inliers==0&&d.TranslationConstraint!=null&&d.Report.Phase==null,"NCC diagnostics store a measured translation and NCC scores without phase scores or invented features");
        }
        using var firstImage=Crop(board,100,100,1000,640);using var secondImage=Crop(board,427,96,1000,640);
        var first=new Tile{Width=1000,Height=640};var second=new Tile{Width=1000,Height=640,X=320};
        foreach(bool highPass in new[]{false,true}){
            using var brightness=secondImage.Clone();brightness.ConvertTo(brightness,MatType.CV_8UC1,.8,20);
            Check(NccRegistrar.Match(0,1,first,second,firstImage,brightness,options with{NccHighPass=highPass},100).Diagnostics.Report.Accepted,$"zero-mean normalized correlation tolerates brightness gain and offset (high-pass={highPass})");
        }
        using var faint=Board(1800,1500,true);using var faintA=Crop(faint,100,100,1000,640);using var faintB=Crop(faint,430,95,1000,640);
        Check(NccRegistrar.Match(0,1,first,second,faintA,faintB,options,100).Diagnostics.Report.Accepted,"NCC recovers faint specks without feature detection");
        using var blank=new Mat(640,1000,MatType.CV_8UC1,Scalar.All(128));
        Check(!NccRegistrar.Match(0,1,first,second,blank,blank,options,100).Diagnostics.Report.Accepted,"NCC rejects flat overlaps instead of OpenCV's constant-template perfect score");
        using var unrelated=Crop(board,650,700,1000,640);
        Check(!NccRegistrar.Match(0,1,first,second,firstImage,unrelated,options,100).Diagnostics.Report.Accepted,"NCC rejects unrelated captures");
        using var beyond=Crop(board,432,100,1000,640);
        var bounded=NccRegistrar.Match(0,1,first,second,firstImage,beyond,options with{MaxShiftMm=.05,MaxRotationDegrees=.01,MaxScalePercent=.01,NccMinCorrelation=.1},100);
        Check(!bounded.Diagnostics.Report.Accepted&&bounded.Diagnostics.Report.Ncc!.AtSearchBoundary,"NCC rejects a peak at the circular stage search boundary rather than undercorrecting an out-of-range shift");
        using var periodic=new Mat(1000,1500,MatType.CV_8UC1,Scalar.All(128));
        for(int y=10;y<990;y+=20)for(int x=10;x<1490;x+=20)Cv2.Circle(periodic,new(x,y),3,Scalar.All(50),-1);
        using var pa=Crop(periodic,100,100,1000,640);using var pb=Crop(periodic,420,100,1000,640);
        var defaultPeriodic=NccRegistrar.Match(0,1,first,second,pa,pb,options,100);
        var strictPeriodic=NccRegistrar.Match(0,1,first,second,pa,pb,options with{NccMinPeakGap=.03},100);
        Check(defaultPeriodic.Diagnostics.Report.Accepted&&!strictPeriodic.Diagnostics.Report.Accepted&&strictPeriodic.Diagnostics.Report.Reason.Contains("Competing"),"optional NCC peak-gap check can reject repeated texture while remaining off by default");
        using var cancelled=new CancellationTokenSource();cancelled.Cancel();
        try{NccRegistrar.Match(0,1,first,second,firstImage,secondImage,options,100,cancelled.Token);throw new Exception("NCC cancel ignored");}catch(OperationCanceledException){Console.WriteLine("PASS NCC observes cancellation");}
        foreach(var invalid in new[]{options with{NccMinCorrelation=0},options with{NccMinCorrelation=double.NaN},options with{NccMinPeakGap=-.01},options with{NccMinPeakGap=2}})
            try{invalid.Validate();throw new Exception("invalid NCC setting accepted");}catch(ArgumentException){}

        const string id="ncc-grid-check";string folder=Files.ProjectPath(id);Directory.CreateDirectory(Path.Combine(folder,"captures"));
        var project=new ScanProject{Id=id,Settings=new(){FovWidth=6.4,FovHeight=4.8},Placement=new(){FovWidth=6.4,FovHeight=4.8},BlendMode="sharp"};
        var truth=new List<XY>();double angle=.35*Math.PI/180,sa=1.008*Math.Cos(angle),sb=1.008*Math.Sin(angle);
        for(int row=0;row<3;row++)for(int col=0;col<3;col++){
            int n=row*3+col;double x=col*320,y=row*240,actualX=sa*x-sb*y+(n%3-1)*1.5,actualY=sb*x+sa*y+(n%2==0?1:-1);
            string file=$"captures/tile-{n}.png";using var image=Crop(board,100+actualX,100+actualY,640,480);Cv2.ImWrite(Path.Combine(folder,file),image);
            truth.Add(new(actualX,actualY));project.Tiles.Add(new(){File=file,Width=640,Height=480,NominalX=x,NominalY=y,StageX=col*3.2,StageY=row*2.4,Row=row,Col=col});
        }
        new Stitcher().Run(project,(_,_,_,_)=>{},CancellationToken.None);
        string originalDzi=project.Dzi!;
        project.Registration=new(new(),DateTimeOffset.UtcNow,0,1,0,0,0,"registration-old-sift.json",[new(0,1,0,0,0,0,1,false,"Too few features")]);
        Files.Save(Path.Combine(folder,"project.json"),project);
        string metadata=File.ReadAllText(Path.Combine(folder,"project.json"));byte[] original=File.ReadAllBytes(Path.Combine(folder,project.Tiles[0].File));
        var preview=RegistrationDiagnostics.Recheck(id,0,1,new(project.Registration.ReportFile,options));
        Check(preview.Trial&&!preview.Report.Accepted&&preview.ReconstructionReport!.Accepted&&preview.ReconstructionReport.Method=="ncc"&&File.ReadAllText(Path.Combine(folder,"project.json"))==metadata,"NCC previews old SIFT-rejected pairs while preserving saved acceptance and metadata");
        new Stitcher().Run(project,(_,_,_,_)=>{},CancellationToken.None,options);
        var report=project.Registration!;var transforms=project.Tiles.Select(Similarity.For).ToArray();
        double maxError=Enumerable.Range(0,9).Max(i=>double.Hypot(transforms[i].X-transforms[0].X-(truth[i].X-truth[0].X),transforms[i].Y-transforms[0].Y-(truth[i].Y-truth[0].Y)));
        Console.WriteLine($"NCC grid: {report.AcceptedPairs} links, max relative error {maxError:F3}, shared scale {report.StageCorrection!.Scale:F5}, angle {report.StageCorrection.RotationDegrees:F4}");
        Check(report.Options.Method=="ncc"&&report.TranslationPairs>8&&report.FeaturePairs==0&&report.SupportedTiles==9&&maxError<1.2&&transforms.All(t=>t.A==1&&t.B==0),"whole-board NCC solves a loop-connected 3×3 graph while preserving tile pixel scale and orientation");
        Check(project.PreviousMosaic!.Dzi==originalDzi&&File.Exists(Path.Combine(folder,originalDzi))&&project.Tiles.All(t=>t.NominalX==t.Col*320&&t.NominalY==t.Row*240&&t.StageX==t.Col*3.2&&t.StageY==t.Row*2.4)&&original.SequenceEqual(File.ReadAllBytes(Path.Combine(folder,project.Tiles[0].File))),"NCC restitch preserves original captures, raw coordinates and previous pyramid");
        var pair=report.Pairs.First(p=>p.Accepted);var loaded=RegistrationDiagnostics.Load(id,pair.First,pair.Second,report.ReportFile);
        Check(loaded.Report.Ncc!=null&&loaded.Report.GlobalErrorPixels!=null&&loaded.TranslationConstraint!=null&&!loaded.Reconstructed,"saved NCC diagnostics restore scores, solved error and translation constraint");
        var phasePreview=RegistrationDiagnostics.Recheck(id,pair.First,pair.Second,new(report.ReportFile,options with{Method="phase"}));
        Check(phasePreview.Report.Method=="ncc"&&phasePreview.ReconstructionReport!.Method=="phase"&&phasePreview.ReconstructionReport.Accepted,"phase comparison preview works on an NCC-refined dataset");
        File.Delete(Path.Combine(folder,pair.DebugFile!));var reconstructed=RegistrationDiagnostics.Load(id,pair.First,pair.Second,report.ReportFile);
        Check(reconstructed.Reconstructed&&reconstructed.ReconstructionReport!.Ncc!=null&&reconstructed.Report.Method=="ncc","missing NCC observations reconstruct with the saved method rather than SIFT");
        string completed=File.ReadAllText(Path.Combine(folder,"project.json"));
        try{new Stitcher().Run(project,(_,_,_,_)=>{},cancelled.Token,options);throw new Exception("NCC cancel ignored");}catch(OperationCanceledException){}
        Check(completed==File.ReadAllText(Path.Combine(folder,"project.json")),"cancelled NCC restitch preserves completed saved project");
    }
}
