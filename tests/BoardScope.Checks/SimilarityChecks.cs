using BoardScope;
using OpenCvSharp;

public static class SimilarityChecks
{
    private static void Check(bool condition,string message){if(!condition)throw new Exception(message);Console.WriteLine("PASS "+message);}
    private static Point2d Inverse(Similarity t,double x,double y)=>new(x-t.X,y-t.Y);
    public static void Run()
    {
        var tiles=(from row in Enumerable.Range(0,5) from col in Enumerable.Range(0,6)
            select new Tile{Width=448,Height=320,X=col*250,Y=row*180,Row=row,Col=col}).ToList();
        double pivotX=tiles.Average(t=>t.X+t.Width/2d),pivotY=tiles.Average(t=>t.Y+t.Height/2d);
        const double scale=1.012,angle=1.3;
        double a=scale*Math.Cos(angle*Math.PI/180),b=scale*Math.Sin(angle*Math.PI/180);
        var truth=tiles.Select(t=>{
            double x=t.X+t.Width/2d-pivotX,y=t.Y+t.Height/2d-pivotY;
            return new Similarity(1,0,t.X+(a-1)*x-b*y+(t.Row%2==0?1.5:-1.5),t.Y+b*x+(a-1)*y+(t.Col%2==0?1:-1));
        }).ToArray();
        tiles.Add(new(){Width=448,Height=320,X=3000,Y=3000});
        var links=new List<FeatureLink>();
        for(int i=0;i<truth.Length;i++)for(int j=i+1;j<truth.Length;j++)
        {
            double left=Math.Max(truth[i].X,truth[j].X),top=Math.Max(truth[i].Y,truth[j].Y);
            double right=Math.Min(truth[i].X+448,truth[j].X+448),bottom=Math.Min(truth[i].Y+320,truth[j].Y+320);
            for(double y=top+12;y<bottom-12;y+=25)for(double x=left+12;x<right-12;x+=25)
                links.Add(new(i,j,Inverse(truth[i],x,y),Inverse(truth[j],x,y)));
        }
        var options=new RegistrationOptions();var solved=SimilaritySolver.SolveLayout(tiles,links,options,100);var result=solved.Tiles;
        Console.WriteLine($"Shared calibration: {solved.StageCorrection.Scale:F8} / {solved.StageCorrection.RotationDegrees:F8} degrees; residual {links.Average(l=>SimilaritySolver.Error(result[l.First].Transform,result[l.Second].Transform,l)):F5}");
        Check(Math.Abs(solved.StageCorrection.Scale-scale)<.001&&Math.Abs(solved.StageCorrection.RotationDegrees-angle)<.05,"one shared stage scale and camera angle recover systematic calibration error across the board");
        Check(result.All(t=>t.Transform.A==1&&t.Transform.B==0),"all source tiles preserve exactly the same pixel scale and orientation");
        Check(links.Average(l=>SimilaritySolver.Error(result[l.First].Transform,result[l.Second].Transform,l))<.3,"shared calibration plus translation-only tiles keeps known feature residual below 0.3 px");
        Check(result[0].Status=="refined"&&double.Hypot(result[0].DeltaX,result[0].DeltaY)>.1,"first tile participates without an arbitrary fixed anchor");
        var unsupported=result[^1];double ux=tiles[^1].X+224-solved.StageCorrection.PivotX,uy=tiles[^1].Y+160-solved.StageCorrection.PivotY;
        double sa=solved.StageCorrection.Scale*Math.Cos(solved.StageCorrection.RotationDegrees*Math.PI/180),sb=solved.StageCorrection.Scale*Math.Sin(solved.StageCorrection.RotationDegrees*Math.PI/180);
        Check(unsupported.Status=="shared-stage"&&unsupported.MechanicalDeltaX==0&&unsupported.MechanicalDeltaY==0&&Math.Abs(unsupported.Transform.X-(tiles[^1].X+(sa-1)*ux-sb*uy))<1e-8,"unsupported tiles inherit shared calibration without invented mechanical corrections");
        var reversed=SimilaritySolver.SolveLayout(tiles.AsEnumerable().Reverse().ToArray(),links.Select(l=>l with {First=tiles.Count-1-l.First,Second=tiles.Count-1-l.Second}).ToArray(),options,100);
        Check(result.Zip(reversed.Tiles.Reverse()).All(pair=>Math.Abs(pair.First.Transform.X-pair.Second.Transform.X)<1e-5&&Math.Abs(pair.First.Transform.Y-pair.Second.Transform.Y)<1e-5)&&Math.Abs(solved.StageCorrection.Scale-reversed.StageCorrection.Scale)<1e-8,"tile order does not change shared calibration or select an anchor");
        var limited=SimilaritySolver.SolveLayout(tiles,links,options with {MaxRotationDegrees=.2,MaxScalePercent=.1,MaxShiftMm=.02},100);
        Check(Math.Abs(limited.StageCorrection.RotationDegrees)<=.200001&&Math.Abs(limited.StageCorrection.Scale-1)<=.001001&&limited.Tiles.All(t=>double.Hypot(t.MechanicalDeltaX!.Value,t.MechanicalDeltaY!.Value)<=2.00001),"shared angle/scale and individual mechanical displacement obey separate limits");
        var noisy=links.Concat(Enumerable.Range(0,5).Select(i=>new FeatureLink(0,1,new(280+i*20,100),new(50+i*20,190)))).ToArray();
        var robust=SimilaritySolver.SolveLayout(tiles,noisy,options,100);
        Check(Math.Abs(robust.StageCorrection.RotationDegrees-solved.StageCorrection.RotationDegrees)<.05&&SimilaritySolver.Median(links.Select(l=>SimilaritySolver.Error(robust.Tiles[l.First].Transform,robust.Tiles[l.Second].Transform,l)).ToArray())<.3,"robust weighting limits false feature correspondences in the shared model");
        var empty=SimilaritySolver.SolveLayout(tiles,[],options,100);
        Check(empty.StageCorrection.Scale==1&&empty.StageCorrection.RotationDegrees==0&&empty.Tiles.Zip(tiles).All(p=>p.First.Transform.X==p.Second.X&&p.First.Transform.Y==p.Second.Y),"no evidence retains the complete recorded stage layout");
        using var canceled=new CancellationTokenSource();canceled.Cancel();
        try{SimilaritySolver.SolveLayout(tiles,links,options,100,canceled.Token);throw new Exception("cancellation ignored");}catch(OperationCanceledException){Console.WriteLine("PASS shared solver observes cancellation");}
        options.Validate();(options with {WorkingSize=3840}).Validate();
        Check(options.WorkingSize==0&&options.ImageFactor(3840,2160)==1&&options.MaxFeatures==6000,"matching continues to use native camera resolution and 6000 features");

        string id="similarity-images",folder=Files.ProjectPath(id);Directory.CreateDirectory(Path.Combine(folder,"captures"));
        using var board=new Mat(500,800,MatType.CV_8UC3,Scalar.All(40));var random=new Random(43);
        for(int k=0;k<1000;k++){int x=random.Next(8,792),y=random.Next(8,492);Cv2.Circle(board,new(x,y),random.Next(2,8),new Scalar(random.Next(70,240),random.Next(70,240),random.Next(70,240)),random.Next(1,3));}
        for(int y=40;y<480;y+=60)for(int x=20;x<740;x+=75)Cv2.PutText(board,random.Next(10,99).ToString(),new(x,y),HersheyFonts.HersheySimplex,.6,Scalar.All(220),1);
        var p0=new Tile{File="captures/a.png",Width=448,Height=320,StageX=0,StageY=0};var p1=new Tile{File="captures/b.png",Width=448,Height=320,Col=1,StageX=2.5,StageY=0,NominalX=250,X=250};
        using(var first=new Mat(board,new Rect(0,0,448,320)))Cv2.ImWrite(Path.Combine(folder,p0.File),first);
        using(var second=new Mat(board,new Rect(255,3,448,320)))Cv2.ImWrite(Path.Combine(folder,p1.File),second);
        var project=new ScanProject{Id=id,Reference=new(0,0),Settings=new(){FovWidth=4.48,FovHeight=3.2},Tiles=[p0,p1],Planned=2};
        var raw=project.Tiles.Select(t=>(t.StageX,t.StageY,t.NominalX,t.NominalY)).ToArray();
        var stitcher=new BoardScope.Stitcher();stitcher.Run(project,(_,_,_,_)=>{},CancellationToken.None);string original=project.Dzi!;
        stitcher.Run(project,(_,_,_,_)=>{},CancellationToken.None,options);
        Check(project.Registration?.AcceptedPairs==1&&project.Registration.SupportedTiles==2,"native SIFT/RANSAC accepts a real pixel overlap from a fixed camera");
        Check(project.Registration!.FeaturePairs==project.Registration.Pairs.Single().Inliers&&project.Registration.FeaturePairs>80,"every reliable feature pair enters the joint solve, exceeding the former 80-match cap");
        Check(p0.Registration!.Transform.A==1&&p1.Registration!.Transform.A==1&&p0.Registration.Transform.B==0&&p1.Registration.Transform.B==0&&Math.Abs(p1.X-p0.X-255)<.3&&Math.Abs(p1.Y-p0.Y-3)<.3,"image registration recovers translation without resizing or rotating either capture");
        Check(project.Dzi!=original&&project.StageMosaic?.Dzi==original&&project.Registration?.StageCorrection!=null,"refined rendering preserves stage mosaic and exports shared calibration diagnostics");
        Check(project.Tiles.Select(t=>(t.StageX,t.StageY,t.NominalX,t.NominalY)).SequenceEqual(raw)&&File.Exists(Path.Combine(folder,project.Registration!.ReportFile)),"refinement preserves raw metadata and writes a versioned report");
        var pairReport=project.Registration!.Pairs.Single();
        string originalMetadata=File.ReadAllText(Path.Combine(folder,"project.json"));
        var debug=RegistrationDiagnostics.Load(id,1,0,project.Registration.ReportFile);
        Check(!debug.Reconstructed&&debug.FeaturesFirst.Length>0&&debug.FeaturesSecond.Length>0&&debug.Matches.Count(m=>m.Status=="retained")==project.Registration.FeaturePairs&&debug.Report.GlobalErrorPixels<.3,"saved pair diagnostics preserve all retained native feature coordinates and the solved residual");
        Check(debug.Matches.Where(m=>m.Status=="retained").Average(m=>double.Hypot(p0.Registration!.Transform.Apply(m.First.X,m.First.Y).X-p1.Registration!.Transform.Apply(m.Second.X,m.Second.Y).X,p0.Registration.Transform.Apply(m.First.X,m.First.Y).Y-p1.Registration.Transform.Apply(m.Second.X,m.Second.Y).Y))<.3,"diagnostic endpoints reproduce the joint solve in the displayed image layout");
        project.Registration=project.Registration with {Pairs=[pairReport with {Accepted=false,Reason="Disagrees with the global layout"}]};
        Files.Save(Path.Combine(folder,"project.json"),project);
        Check(!RegistrationDiagnostics.Load(id,0,1,project.Registration.ReportFile).Report.Accepted,"pair diagnostics use final global rejection rather than an earlier accepted match");
        project.Registration=project.Registration with {Pairs=[pairReport with {DebugFile=null}]};
        Files.Save(Path.Combine(folder,"project.json"),project);
        string legacyMetadata=File.ReadAllText(Path.Combine(folder,"project.json"));
        var reconstructed=RegistrationDiagnostics.Load(id,0,1,project.Registration.ReportFile);
        Check(reconstructed.Reconstructed&&reconstructed.ReconstructionReport?.Accepted==true&&reconstructed.Matches.Count>80&&File.ReadAllText(Path.Combine(folder,"project.json"))==legacyMetadata,"older scans reconstruct and cache feature observations without changing saved metadata or stitch positions");
        Check(RegistrationDiagnostics.Load(id,0,1,project.Registration.ReportFile).Reconstructed,"reconstructed diagnostics are reusable from the lazy cache");
        try{RegistrationDiagnostics.Load(id,0,1,"registration-old.json");throw new Exception("stale version accepted");}catch(InvalidOperationException){Console.WriteLine("PASS diagnostics reject a stale refinement version");}
        try{RegistrationDiagnostics.Load(id,0,2,null);throw new Exception("invalid pair accepted");}catch(ArgumentException){Console.WriteLine("PASS diagnostics reject invalid tile indices");}
        project.Registration=project.Registration with {Pairs=[pairReport with {DebugFile="../outside.json"}]};Files.Save(Path.Combine(folder,"project.json"),project);
        try{RegistrationDiagnostics.Load(id,0,1,null);throw new Exception("outside path accepted");}catch(ArgumentException){Console.WriteLine("PASS diagnostics reject paths outside the scan folder");}
        File.WriteAllText(Path.Combine(folder,"project.json"),originalMetadata);
        // A different magnification must not silently re-enable per-tile warping.
        using(var matrix=new Mat(2,3,MatType.CV_64F))using(var changed=new Mat())
        {
            matrix.Set(0,0,1/1.01);matrix.Set(0,1,0d);matrix.Set(0,2,-255/1.01);
            matrix.Set(1,0,0d);matrix.Set(1,1,1/1.01);matrix.Set(1,2,-3/1.01);
            Cv2.WarpAffine(board,changed,matrix,new Size(448,320));Cv2.ImWrite(Path.Combine(folder,p1.File),changed);
        }
        try{stitcher.Run(project,(_,_,_,_)=>{},CancellationToken.None,options);throw new Exception("magnification change accepted");}
        catch(InvalidOperationException e){Check(e.Message.Contains("No reliable translation matches"),"registration rejects captures with differing physical magnification");}
        Console.WriteLine("Shared calibration checks complete.");
    }
}
