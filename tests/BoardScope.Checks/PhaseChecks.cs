using BoardScope;
using OpenCvSharp;
using Stitcher=BoardScope.Stitcher;

static class PhaseChecks
{
    static void Check(bool condition,string message){if(!condition)throw new Exception(message);Console.WriteLine("PASS "+message);}
    static Mat Board(int width,int height,bool faint=false)
    {
        var board=new Mat(height,width,MatType.CV_8UC1,Scalar.All(128));var random=new Random(731);
        for(int k=0;k<width*height/450;k++)Cv2.Circle(board,new(random.Next(8,width-8),random.Next(8,height-8)),random.Next(1,5),Scalar.All(128+(k%2==0?1:-1)*random.Next(faint?4:25,faint?10:100)),-1);
        Cv2.GaussianBlur(board,board,new Size(3,3),.6);return board;
    }
    static Mat Crop(Mat board,double x,double y,int w,int h){var image=new Mat();Cv2.GetRectSubPix(board,new(w,h),new((float)(x+(w-1)/2d),(float)(y+(h-1)/2d)),image);return image;}
    public static void Run()
    {
        using var board=Board(1800,1500);var options=new RegistrationOptions{Method="phase"};
        foreach(var (dx,dy) in new[]{(9d,0d),(-9d,0d),(0d,7d),(0d,-7d),(8d,-6d),(3.4,-2.7)})
        foreach(int size in new[]{0,800})
        {
            var a=new Tile{Width=1000,Height=640};var b=new Tile{Width=1000,Height=640,X=320.3,Y=140.6};
            using var ia=Crop(board,100,100,1000,640);using var ib=Crop(board,100+b.X+dx,100+b.Y+dy,1000,640);
            var (d,link)=PhaseRegistrar.Match(0,1,a,b,ia,ib,options with{WorkingSize=size},100);
            var p=d.Report.Phase!;
            Console.WriteLine($"Phase ({dx},{dy}) size {size}: {d.Report.Reason}, response {p?.Response:F3}, NCC {p?.Correlation:F3}, correction {p?.CorrectionX:F3},{p?.CorrectionY:F3}");
            Check(d.Report.Accepted&&link!=null&&double.Hypot(p!.CorrectionX-dx,p.CorrectionY-dy)<.7,$"phase offset has correct sign and subpixel accuracy ({dx},{dy}), resolution {size}");
            Check(d.FeaturesFirst.Length==0&&d.Matches.Count==0&&d.Report.Inliers==0&&d.TranslationConstraint!=null,"correlation diagnostics distinguish a translation constraint from detected features");
        }
        using var faint=Board(1800,1500,true);using var faintA=Crop(faint,100,100,1000,640);using var faintB=Crop(faint,430,95,1000,640);
        var first=new Tile{Width=1000,Height=640};var second=new Tile{Width=1000,Height=640,X=320};
        var faintResult=PhaseRegistrar.Match(0,1,first,second,faintA,faintB,options,100);
        Check(faintResult.Diagnostics.Report.Accepted&&Math.Abs(faintResult.Diagnostics.Report.Phase!.CorrectionX-10)<.6,"phase correlation recovers a faint speck overlap without feature detection");
        using var brightA=Crop(board,100,100,1000,640);using var brightB=Crop(board,427,96,1000,640);
        brightB.ConvertTo(brightB,MatType.CV_8UC1,.8,20);
        Check(PhaseRegistrar.Match(0,1,first,second,brightA,brightB,options,100).Diagnostics.Report.Accepted,"phase verification tolerates brightness gain and offset");
        using var blank=new Mat(640,1000,MatType.CV_8UC1,Scalar.All(128));
        Check(!PhaseRegistrar.Match(0,1,first,second,blank,blank,options,100).Diagnostics.Report.Accepted,"flat overlaps are rejected instead of providing false constraints");
        using var unrelated=Board(1800,1500);using var other=Crop(unrelated,600,700,1000,640);
        Check(!PhaseRegistrar.Match(0,1,first,second,brightA,other,options,100).Diagnostics.Report.Accepted,"unrelated overlap is rejected");
        using var far=Crop(board,500,100,1000,640);
        Check(PhaseRegistrar.Match(0,1,first,second,brightA,far,options with{MaxRotationDegrees=.01,MaxScalePercent=.01,MaxShiftMm=.05},100).Diagnostics.Report.Reason.Contains("stage tolerance"),"phase offsets beyond the stage tolerance are rejected");
        using var cancelled=new CancellationTokenSource();cancelled.Cancel();
        try{PhaseRegistrar.Match(0,1,first,second,brightA,brightB,options,100,cancelled.Token);throw new Exception("cancel ignored");}catch(OperationCanceledException){Console.WriteLine("PASS phase matching observes cancellation");}
        foreach(var invalid in new[]{options with{Method="unknown"},options with{PhaseMinResponse=0},options with{PhaseMinCorrelation=double.NaN}})
            try{invalid.Validate();throw new Exception("invalid phase setting accepted");}catch(ArgumentException){}

        const string id="phase-grid-check";string folder=Files.ProjectPath(id);Directory.CreateDirectory(Path.Combine(folder,"captures"));
        var project=new ScanProject{Id=id,Settings=new(){FovWidth=6.4,FovHeight=4.8},Placement=new(){FovWidth=6.4,FovHeight=4.8},BlendMode="sharp"};
        var truth=new List<XY>();double angle=.35*Math.PI/180,sa=1.008*Math.Cos(angle),sb=1.008*Math.Sin(angle);
        for(int row=0;row<3;row++)for(int col=0;col<3;col++){
            int n=row*3+col;double x=col*320,y=row*240,actualX=sa*x-sb*y+(n%3-1)*1.5,actualY=sb*x+sa*y+(n%2==0?1:-1);
            string file=$"captures/tile-{n}.png";using var image=Crop(board,100+actualX,100+actualY,640,480);Cv2.ImWrite(Path.Combine(folder,file),image);
            truth.Add(new(actualX,actualY));project.Tiles.Add(new(){File=file,Width=640,Height=480,NominalX=x,NominalY=y,StageX=col*3.2,StageY=row*2.4,Row=row,Col=col});
        }
        new Stitcher().Run(project,(_,_,_,_)=>{},CancellationToken.None);
        string originalDzi=project.Dzi!;
        var savedSift=new RegistrationReport(new(),DateTimeOffset.UtcNow,0,1,0,0,0,"registration-old-sift.json",[new(0,1,0,0,0,0,1,false,"Too few features")]);
        project.Registration=savedSift;Files.Save(Path.Combine(folder,"project.json"),project);
        string originalMetadata=File.ReadAllText(Path.Combine(folder,"project.json"));byte[] originalImage=File.ReadAllBytes(Path.Combine(folder,project.Tiles[0].File));
        var preview=RegistrationDiagnostics.Recheck(id,0,1,new(savedSift.ReportFile,options));
        Check(preview.Trial&&!preview.Report.Accepted&&preview.ReconstructionReport!.Accepted&&preview.ReconstructionReport.Method=="phase","phase preview works on an older feature-rejected pair and keeps its saved verdict");
        Check(originalMetadata==File.ReadAllText(Path.Combine(folder,"project.json")),"phase preview preserves saved project metadata and alignment");
        new Stitcher().Run(project,(_,_,_,_)=>{},CancellationToken.None,options);
        var report=project.Registration!;
        Check(report.Options.Method=="phase"&&report.TranslationPairs>8&&report.FeaturePairs==0&&report.SupportedTiles==9,"phase restitch connects a 3×3 graph with loop constraints and a bounded source cache");
        var transform=project.Tiles.Select(Similarity.For).ToArray();
        double maxError=Enumerable.Range(0,9).Max(i=>double.Hypot(transform[i].X-transform[0].X-(truth[i].X-truth[0].X),transform[i].Y-transform[0].Y-(truth[i].Y-truth[0].Y)));
        Console.WriteLine($"Phase grid: {report.AcceptedPairs} links, max relative error {maxError:F3}, shared scale {report.StageCorrection!.Scale:F5}, angle {report.StageCorrection.RotationDegrees:F4}");
        Check(maxError<1.2&&transform.All(t=>t.A==1&&t.B==0),"whole-board phase solve recovers relative placement while preserving source pixel orientation and scale");
        Check(project.PreviousMosaic!.Dzi==originalDzi&&project.PreviousMosaic.Label=="Previous feature matching stitch"&&File.Exists(Path.Combine(folder,originalDzi)),"restitch retains the previous mosaic for visual comparison");
        Check(project.Tiles.All(t=>t.NominalX==t.Col*320&&t.NominalY==t.Row*240&&t.StageX==t.Col*3.2&&t.StageY==t.Row*2.4)&&originalImage.SequenceEqual(File.ReadAllBytes(Path.Combine(folder,project.Tiles[0].File))),"phase restitch preserves raw coordinates and original captures");
        var pair=report.Pairs.First(p=>p.Accepted);var diagnostics=RegistrationDiagnostics.Load(id,pair.First,pair.Second,report.ReportFile);
        Check(diagnostics.Report.Phase!=null&&diagnostics.Report.GlobalErrorPixels!=null&&diagnostics.TranslationConstraint!=null&&diagnostics.Matches.Count==0,"saved phase diagnostics restore scores, solved residual and measured translation");
        string completed=File.ReadAllText(Path.Combine(folder,"project.json"));
        try{new Stitcher().Run(project,(_,_,_,_)=>{},cancelled.Token,options);throw new Exception("cancel ignored");}catch(OperationCanceledException){}
        Check(completed==File.ReadAllText(Path.Combine(folder,"project.json")),"cancelled phase restitch does not replace the completed saved project");
    }
}
