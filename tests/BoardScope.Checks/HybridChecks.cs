using BoardScope;
using OpenCvSharp;
using System.Text.Json;
using Stitcher=BoardScope.Stitcher;

static class HybridChecks
{
    static void Check(bool value,string message){if(!value)throw new Exception(message);Console.WriteLine("PASS "+message);}
    static Mat Board(bool faint=false)
    {
        var board=new Mat(1000,1300,MatType.CV_8UC1,Scalar.All(128));var random=new Random(91);
        for(int k=0;k<3200;k++)Cv2.Circle(board,new(random.Next(10,1290),random.Next(10,990)),random.Next(2,6),Scalar.All(128+(k%2==0?1:-1)*random.Next(faint?4:30,faint?10:110)),-1);
        Cv2.GaussianBlur(board,board,new Size(3,3),.7);return board;
    }
    static Mat Crop(Mat image,double x,double y){var crop=new Mat();Cv2.GetRectSubPix(image,new Size(640,480),new Point2f((float)(x+319.5),(float)(y+239.5)),crop);return crop;}
    static ScanProject Pair(string id,Mat board)
    {
        string folder=Files.ProjectPath(id);Directory.CreateDirectory(Path.Combine(folder,"captures"));
        var p=new ScanProject{Id=id,Settings=new(){FovWidth=6.4,FovHeight=4.8},Placement=new(){FovWidth=6.4,FovHeight=4.8},Tiles=[
            new(){File="captures/a.png",Width=640,Height=480,BaseX=0,BaseY=0},
            new(){File="captures/b.png",Width=640,Height=480,BaseX=300,BaseY=0,NominalX=300}],
            Registration=new(new(),DateTimeOffset.UtcNow,0,1,0,0,0,"registration-fixture.json",[new(0,1,0,0,0,0,1,false,"Original rejected pair")])};
        using(var a=Crop(board,100,100))Cv2.ImWrite(Path.Combine(folder,p.Tiles[0].File),a);
        using(var b=Crop(board,403.4,102.7))Cv2.ImWrite(Path.Combine(folder,p.Tiles[1].File),b);
        Files.Save(Path.Combine(folder,"project.json"),p);return p;
    }
    static PairDiagnostics Preview(ScanProject p,RegistrationOptions options,CancellationToken ct=default)=>RegistrationDiagnostics.Recheck(p.Id,0,1,new(p.Registration!.ReportFile,options),ct);
    public static void Run()
    {
        var options=new RegistrationOptions{Method="hybrid"};options.Validate();
        using var board=Board();var p=Pair("hybrid-pair-check",board);string folder=Files.ProjectPath(p.Id);
        string metadata=File.ReadAllText(Path.Combine(folder,"project.json"));byte[] original=File.ReadAllBytes(Path.Combine(folder,p.Tiles[0].File));
        var first=Preview(p,options);var report=first.ReconstructionReport!;
        Check(report.Accepted&&report.Method=="phase"&&report.Attempts!.Count==1&&first.TranslationConstraint!=null&&!Directory.Exists(Path.Combine(folder,"registration-cache")),"hybrid stops at a reliable phase match without extracting any SIFT features");
        var ncc=Preview(p,options with{PhaseMinCorrelation=1});var nccReport=ncc.ReconstructionReport!;
        Check(nccReport.Accepted&&nccReport.Method=="ncc"&&nccReport.Attempts!.Count==2&&!nccReport.Attempts[0].Report.Accepted&&ncc.AnalysisOptions!.Method=="ncc","hybrid retries a phase-rejected pair with NCC and preserves the failed attempt");
        Check(Math.Abs(nccReport.Ncc!.DeltaX-303.4)<.5&&Math.Abs(nccReport.Ncc.DeltaY-2.7)<.5,"fallback uses the measured native translation with the correct image direction");
        using var faint=Board(true);var weak=Pair("hybrid-faint-check",faint);
        var strict=options with{PhaseMinCorrelation=1,NccMinCorrelation=1};var recovered=Preview(weak,strict);var faintReport=recovered.ReconstructionReport!;
        Console.WriteLine($"Hybrid faint pair: {faintReport.Method}, {faintReport.Attempts!.Count} attempts, {faintReport.Inliers} inliers; {faintReport.Reason}");
        Check(faintReport.Accepted&&faintReport.Method=="sift"&&faintReport.Attempts.Count>=6&&recovered.AnalysisOptions!.ContrastThreshold<.04&&recovered.Matches.Any(m=>m.Status=="retained"),"hybrid recovers faint features with increased sensitivity when the pixel matchers fail their confidence gates");
        Check(faintReport.Attempts.All(a=>a.Options.MaxShiftMm==strict.MaxShiftMm&&a.Options.MaxScalePercent==strict.MaxScalePercent&&a.Options.MaxRotationDegrees==strict.MaxRotationDegrees&&a.Options.PhaseMinCorrelation==1&&a.Options.NccMinCorrelation==1),"hybrid retries preserve geometric limits and configured acceptance thresholds");
        using var flat=new Mat(1000,1300,MatType.CV_8UC1,Scalar.All(128));var blank=Pair("hybrid-flat-check",flat);var failed=Preview(blank,options).ReconstructionReport!;
        Check(!failed.Accepted&&failed.Attempts!.Count==7&&failed.Attempts.All(a=>!a.Report.Accepted)&&failed.Reason.Contains("No hybrid attempt"),"unmatchable flat pairs remain rejected after a bounded seven-attempt cascade");
        Check(!first.Report.Accepted&&first.Trial&&File.ReadAllText(Path.Combine(folder,"project.json"))==metadata&&original.SequenceEqual(File.ReadAllBytes(Path.Combine(folder,p.Tiles[0].File))),"hybrid previews preserve existing neighbor status, metadata and original captures");
        using var cancelled=new CancellationTokenSource();cancelled.Cancel();
        try{Preview(p,options,cancelled.Token);throw new Exception("Hybrid ignored cancellation.");}catch(OperationCanceledException){Check(true,"hybrid cancellation aborts the cascade");}

        const string id="hybrid-layout-check";folder=Files.ProjectPath(id);Directory.CreateDirectory(Path.Combine(folder,"captures"));
        var project=new ScanProject{Id=id,Settings=new(){FovWidth=6.4,FovHeight=4.8},Placement=new(){FovWidth=6.4,FovHeight=4.8},BlendMode="sharp"};
        var actual=new[]{new XY(0,0),new XY(323.4,2.7),new XY(2.2,242.35)};var nominal=new[]{new XY(0,0),new XY(320,0),new XY(0,240)};
        for(int i=0;i<3;i++){
            string file=$"captures/tile-{i}.png";using var image=Crop(board,100+actual[i].X,100+actual[i].Y);Cv2.ImWrite(Path.Combine(folder,file),image);
            project.Tiles.Add(new(){File=file,Width=640,Height=480,NominalX=nominal[i].X,NominalY=nominal[i].Y,StageX=nominal[i].X/100,StageY=nominal[i].Y/100});
        }
        new Stitcher().Run(project,(_,_,_,_)=>{},CancellationToken.None);string stageDzi=project.Dzi!;
        var qualities=new List<double>();
        for(int i=0;i<3;i++)for(int j=i+1;j<3;j++){
            var a=project.Tiles[i];var b=project.Tiles[j];using var ia=Cv2.ImRead(Path.Combine(folder,a.File),ImreadModes.Grayscale);using var ib=Cv2.ImRead(Path.Combine(folder,b.File),ImreadModes.Grayscale);
            var high=PhaseRegistrar.Match(i,j,a,b,ia,ib,options with{Method="phase"},100).Diagnostics.Report;
            var raw=PhaseRegistrar.Match(i,j,a,b,ia,ib,options with{Method="phase",PhaseHighPass=false},100).Diagnostics.Report;
            qualities.Add(Math.Max(high.Phase?.Correlation??0,raw.Phase?.Correlation??0));
        }
        var mixed=options with{PhaseMinCorrelation=(qualities.Min()+qualities.Max())/2,NccMinCorrelation=1};
        new Stitcher().Run(project,(_,_,_,_)=>{},CancellationToken.None,mixed);
        var registration=project.Registration!;var methods=registration.Pairs.Where(r=>r.Accepted).Select(r=>r.Method).Distinct().ToArray();
        Console.WriteLine($"Hybrid layout: methods {string.Join(',',methods)}, {registration.AcceptedPairs} accepted pairs, feature observations {registration.FeaturePairs}, pixel constraints {registration.TranslationPairs}");
        Check(registration.Options.Method=="hybrid"&&registration.SupportedTiles==3&&registration.FeaturePairs>0&&registration.TranslationPairs>0,"one global solve combines feature observations and pixel translations from different successful methods");
        var transforms=project.Tiles.Select(Similarity.For).ToArray();
        Check(Enumerable.Range(1,2).All(i=>double.Hypot(transforms[i].X-transforms[0].X-(actual[i].X-actual[0].X),transforms[i].Y-transforms[0].Y-(actual[i].Y-actual[0].Y))<1.2)&&transforms.All(t=>t.A==1&&t.B==0),"mixed hybrid constraints recover relative tile positions while preserving physical pixel scale and orientation");
        Check(project.PreviousMosaic!.Dzi==stageDzi&&File.Exists(Path.Combine(folder,stageDzi))&&project.Stitches.Last().Method=="hybrid","hybrid produces its own saved alignment and map while preserving older mosaics");
        var pair=registration.Pairs.First(r=>r.Accepted);var loaded=RegistrationDiagnostics.Load(id,pair.First,pair.Second,registration.ReportFile);
        Check(!loaded.Reconstructed&&loaded.Report.Attempts!.Count==pair.Attempts!.Count&&loaded.AnalysisOptions!.Method==pair.Method&&loaded.Report.GlobalErrorPixels!=null,"saved hybrid diagnostics restore every attempt, the actual winning method and global error");
        File.Delete(Path.Combine(folder,pair.DebugFile!));var reconstructed=RegistrationDiagnostics.Load(id,pair.First,pair.Second,registration.ReportFile);
        Check(reconstructed.Reconstructed&&reconstructed.ReconstructionReport!.Attempts!.Count>0,"missing hybrid diagnostics reconstruct through the same cascade");
        var defaults=new AppDefaults{Scan=new(){StitchMethod="hybrid",StitchOptions=options}};var restored=JsonSerializer.Deserialize<AppDefaults>(JsonSerializer.Serialize(defaults,Files.Json),Files.Json)!;restored.Validate();
        Check(restored.Scan.AutomaticRefinement()!.Method=="hybrid","hybrid can be saved as the default stitching method after scanning");
    }
}
