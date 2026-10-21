using BoardScope;
using OpenCvSharp;
using System.Text.Json;
using Stitcher=BoardScope.Stitcher;

static class ParallaxChecks
{
    static void Check(bool value,string message){if(!value)throw new Exception(message);Console.WriteLine("PASS "+message);}
    static Mat Texture(int seed)
    {
        var image=new Mat(1400,1700,MatType.CV_8UC1,Scalar.All(130));var random=new Random(seed);
        for(int k=0;k<14000;k++)Cv2.Circle(image,new(random.Next(5,1695),random.Next(5,1395)),random.Next(1,6),Scalar.All(random.Next(25,235)),-1);
        Cv2.GaussianBlur(image,image,new(3,3),.65);return image;
    }
    static Mat Crop(Mat image,double x,double y){var result=new Mat();Cv2.GetRectSubPix(image,new(960,720),new((float)(x+479.5),(float)(y+359.5)),result);return result;}
    public static void Run()
    {
        var options=new RegistrationOptions{Method="parallax",PhaseMinResponse=.12,NccMinCorrelation=.45};options.Validate();
        using var board=Texture(82);using var raised=Texture(904);
        string id="parallax-blur-check",folder=Files.ProjectPath(id);Directory.CreateDirectory(Path.Combine(folder,"captures"));
        var actual=new[]{new XY(0,0),new XY(404,2),new XY(3,363)};var nominal=new[]{new XY(0,0),new XY(400,0),new XY(0,360)};
        var project=new ScanProject{Id=id,Settings=new(){FovWidth=9.6,FovHeight=7.2},Placement=new(){FovWidth=9.6,FovHeight=7.2},BlendMode="content"};
        for(int i=0;i<3;i++)
        {
            using var image=Crop(board,100+actual[i].X,100+actual[i].Y);
            using var component=Crop(raised,100+actual[i].X+(i==1?13:0),100+actual[i].Y);
            Cv2.GaussianBlur(component,component,new(0,0),i==1?2.1:1.2);
            int rows=Math.Max(0,(int)(420-actual[i].Y));using(var src=new Mat(component,new Rect(0,0,960,rows)))using(var dst=new Mat(image,new Rect(0,0,960,rows)))src.CopyTo(dst);
            string file=$"captures/{i}.png";Cv2.ImWrite(Path.Combine(folder,file),image);
            project.Tiles.Add(new(){File=file,Width=960,Height=720,NominalX=nominal[i].X,NominalY=nominal[i].Y,StageX=nominal[i].X/100,StageY=nominal[i].Y/100});
        }
        new Stitcher().Run(project,(_,_,_,_)=>{},CancellationToken.None);string originalMap=project.Dzi!;
        var originals=project.Tiles.Select(t=>File.ReadAllBytes(Path.Combine(folder,t.File))).ToArray();
        new Stitcher().Run(project,(_,_,_,message)=>{if(message.Contains("alignment ready"))Console.WriteLine(message);},CancellationToken.None,options);
        var report=project.Registration!;
        foreach(var p in report.Pairs)Console.WriteLine($"Pair {p.First}/{p.Second}: {p.Accepted}, {p.Parallax!.Hypotheses.Count} hypotheses, {p.Parallax.GlobalRetries} comparisons, {p.Parallax.GlobalChanges} changes, error {p.GlobalErrorPixels}; "+string.Join("; ",p.Parallax.Hypotheses.Select(h=>$"{h.DeltaX:F2},{h.DeltaY:F2} conf{h.Confidence:F2} patches{h.Patches} err{h.GlobalErrorPixels:F2}")));
        Check(report.Options.Method=="parallax"&&report.SupportedTiles==3&&report.AcceptedPairs==3,"parallax-aware strategy supports all synthetic tiles despite blurred raised components");
        Check(report.Pairs.Any(p=>p.Parallax!.Hypotheses.Count>1)&&report.Pairs.Any(p=>p.Parallax!.GlobalChanges>0),"independent graph paths switch a locally preferred raised-surface offset to the PCB-plane hypothesis");
        var transforms=project.Tiles.Select(Similarity.For).ToArray();
        Check(Enumerable.Range(1,2).All(i=>double.Hypot(transforms[i].X-transforms[0].X-actual[i].X,transforms[i].Y-transforms[0].Y-actual[i].Y)<2)&&transforms.All(t=>t.A==1&&t.B==0),"global hypothesis selection recovers the PCB plane without changing tile scale or orientation");
        Check(report.Pairs.All(p=>p.GlobalErrorPixels<=8)&&project.Stitches.Last().Method=="parallax"&&project.Stitches.Last().Maps.Last().BlendMode=="content"&&File.Exists(Path.Combine(folder,originalMap)),"accepted offsets agree with the final layout and the new alignment/map preserve earlier maps");
        var pair=report.Pairs.First();var loaded=RegistrationDiagnostics.Load(id,pair.First,pair.Second,report.ReportFile);
        Check(!loaded.Reconstructed&&loaded.PatchObservations!.Count>0&&loaded.Matches.Any(m=>m.Status=="retained")&&loaded.Matches.Any(m=>m.Status=="other-offset"),"saved diagnostics expose retained patch measurements and alternative offsets");
        File.Delete(Path.Combine(folder,pair.DebugFile!));var reconstructed=RegistrationDiagnostics.Load(id,pair.First,pair.Second,report.ReportFile);
        Check(reconstructed.Reconstructed&&reconstructed.ReconstructionReport!.Reason.Contains("saved selected offset")&&reconstructed.Matches.Where(m=>m.Status=="retained").All(m=>Math.Abs(m.First.X-m.Second.X-404)<3),"missing diagnostics reconstruct the globally selected offset rather than the locally preferred wrong surface");
        string metadata=File.ReadAllText(Path.Combine(folder,"project.json"));
        var preview=RegistrationDiagnostics.Recheck(id,0,1,new(report.ReportFile,options));
        Check(preview.Trial&&preview.ReconstructionReport!.Parallax!=null&&File.ReadAllText(Path.Combine(folder,"project.json"))==metadata,"parallax pair previews are explicitly local and leave saved metadata unchanged");
        Check(project.Tiles.Select((t,i)=>File.ReadAllBytes(Path.Combine(folder,t.File)).SequenceEqual(originals[i])).All(x=>x),"original captures remain byte-identical");
        var bridge=new ScanProject{Id=id,Placement=project.Placement,Tiles=project.Tiles.Take(2).Select(t=>new Tile{File=t.File,Width=t.Width,Height=t.Height,X=t.BaseX??0,Y=t.BaseY??0}).ToList()};
        var bridgeReport=new ParallaxRegistrar().Run(bridge,folder,options,(_,_,_,_)=>{},CancellationToken.None);
        Check(bridgeReport.Pairs.Single().Parallax!.GlobalRetries==0,"a bridge pair cannot use its own offset as independent global confirmation");
        using var cancellation=new CancellationTokenSource();cancellation.Cancel();
        try{RegistrationDiagnostics.Recheck(id,0,1,new(report.ReportFile,options),cancellation.Token);throw new Exception("Ignored cancellation");}catch(OperationCanceledException){Check(true,"parallax matching honors cancellation");}
        foreach(var tile in project.Tiles){using var flat=new Mat(720,960,MatType.CV_8UC1,Scalar.All(128));Cv2.ImWrite(Path.Combine(folder,tile.File),flat);}
        var blank=RegistrationDiagnostics.Recheck(id,0,1,new(report.ReportFile,options));
        Check(!blank.ReconstructionReport!.Accepted&&blank.ReconstructionReport.Parallax!.Hypotheses.Count==0,"flat overlaps remain rejected rather than manufacturing a neighbor");
        var defaults=new AppDefaults{Scan=new(){StitchMethod="parallax",StitchBlendMode="content",StitchOptions=options}};
        var restored=JsonSerializer.Deserialize<AppDefaults>(JsonSerializer.Serialize(defaults,Files.Json),Files.Json)!;restored.Validate();
        Check(restored.Scan.AutomaticRefinement()!.Method=="parallax"&&restored.Scan.StitchBlendMode=="content","parallax matching and content-aware sharp seams persist as scan defaults");
        string fallbackId="parallax-fallback-check",fallbackFolder=Files.ProjectPath(fallbackId);Directory.CreateDirectory(Path.Combine(fallbackFolder,"captures"));
        using(var a=Crop(board,0,0))Cv2.ImWrite(Path.Combine(fallbackFolder,"captures/a.png"),a);
        using(var b=Crop(board,783.4,542.7))Cv2.ImWrite(Path.Combine(fallbackFolder,"captures/b.png"),b);
        var fallback=new ScanProject{Id=fallbackId,Placement=project.Placement,Tiles=[new(){File="captures/a.png",Width=960,Height=720},new(){File="captures/b.png",Width=960,Height=720,X=780,Y=540}]};
        var fallbackReport=new ParallaxRegistrar().Run(fallback,fallbackFolder,options,(_,_,_,_)=>{},CancellationToken.None);
        Check(fallbackReport.AcceptedPairs==1&&fallbackReport.Pairs.Single().Attempts!.Count>=5&&fallbackReport.Pairs.Single().Parallax!.Hypotheses.Any(h=>h.Patches==0),"a narrow single-region overlap tries whole-overlap alternatives and retains successful fallback candidates");
        ContentBlend();
    }
    static void ContentBlend()
    {
        string id="content-seam-check",folder=Files.ProjectPath(id);Directory.CreateDirectory(Path.Combine(folder,"captures"));
        using var a=new Mat(640,640,MatType.CV_8UC3,new Scalar(20,20,230));using var b=new Mat(640,640,MatType.CV_8UC3,new Scalar(230,20,20));
        Cv2.ImWrite(Path.Combine(folder,"captures/a.png"),a);Cv2.ImWrite(Path.Combine(folder,"captures/b.png"),b);
        var project=new ScanProject{Id=id,BlendMode="content",Settings=new(){FovWidth=6.4,FovHeight=6.4},Tiles=[new(){File="captures/a.png",Width=640,Height=640},new(){File="captures/b.png",Width=640,Height=640,NominalX=320}]};
        new Stitcher().Run(project,(_,_,_,_)=>{},CancellationToken.None);
        int level=(int)Math.Ceiling(Math.Log2(Math.Max(project.Width,project.Height)));string root=Path.Combine(folder,Path.GetFileNameWithoutExtension(project.Dzi!) + "_files",level.ToString());
        foreach(string file in Directory.GetFiles(root,"*.jpg"))
        {
            using var image=Cv2.ImRead(file);
                int height=image.Height,width=image.Width;
                for(int y=10;y<height-10;y+=13)for(int x=10;x<width-10;x+=13){var color=image.At<Vec3b>(y,x);CheckPixel(color);}
        }
        Console.WriteLine("PASS content-aware sharp maps have complete coverage, one-source colors and consistent seams across output blocks");
        static void CheckPixel(Vec3b c){if(Math.Max(c.Item0,c.Item2)<200||Math.Min(c.Item0,c.Item2)>45||c.Item1>45)throw new Exception("Content seam mixed sources or left a hole");}
    }
}
