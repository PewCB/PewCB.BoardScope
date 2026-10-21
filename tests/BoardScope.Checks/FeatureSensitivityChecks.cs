using BoardScope;
using OpenCvSharp;

public static class FeatureSensitivityChecks
{
    private static void Check(bool condition,string message){if(!condition)throw new Exception(message);Console.WriteLine("PASS "+message);}
    public static void Run()
    {
        string id="faint-speck-check",folder=Files.ProjectPath(id);Directory.CreateDirectory(Path.Combine(folder,"captures"));
        using var board=new Mat(620,1050,MatType.CV_8UC1,Scalar.All(128));var random=new Random(91);
        for(int k=0;k<1200;k++)Cv2.Circle(board,new(random.Next(10,1040),random.Next(10,610)),random.Next(2,6),Scalar.All(128+(k%2==0?1:-1)*random.Next(4,10)),-1);
        Cv2.GaussianBlur(board,board,new Size(3,3),.7);
        var first=new Tile{File="captures/a.png",Width=640,Height=480,BaseX=0,BaseY=0};
        var second=new Tile{File="captures/b.png",Width=640,Height=480,BaseX=300,BaseY=0,NominalX=300};
        using(var crop=new Mat(board,new Rect(0,0,640,480)))Cv2.ImWrite(Path.Combine(folder,first.File),crop);
        using(var crop=new Mat(board,new Rect(303,2,640,480)))Cv2.ImWrite(Path.Combine(folder,second.File),crop);
        var options=new RegistrationOptions();
        var project=new ScanProject{Id=id,Settings=new(){FovWidth=6.4,FovHeight=4.8},Placement=new(){FovWidth=6.4,FovHeight=4.8},Tiles=[first,second],Registration=new(options,DateTimeOffset.UtcNow,0,1,0,0,0,"registration-test.json",[new(0,1,0,0,0,0,1,false,"Too few features")])};
        Files.Save(Path.Combine(folder,"project.json"),project);
        string metadata=File.ReadAllText(Path.Combine(folder,"project.json"));byte[] original=File.ReadAllBytes(Path.Combine(folder,first.File));
        PairDiagnostics Preview(RegistrationOptions detector)=>RegistrationDiagnostics.Recheck(id,0,1,new(project.Registration.ReportFile,detector));
        var normal=Preview(options);var sensitive=Preview(options with {ContrastThreshold=.003});
        var boosted=Preview(options with {ContrastThreshold=.01,EnhanceLocalContrast=true});
        Console.WriteLine($"Faint specks: normal {normal.TotalFeaturesFirst}/{normal.TotalFeaturesSecond}, sensitive {sensitive.TotalFeaturesFirst}/{sensitive.TotalFeaturesSecond}, boosted {boosted.TotalFeaturesFirst}/{boosted.TotalFeaturesSecond}; matches {sensitive.ReconstructionReport!.Inliers}/{boosted.ReconstructionReport!.Inliers}");
        Check(sensitive.TotalFeaturesFirst>normal.TotalFeaturesFirst+40&&sensitive.TotalFeaturesSecond>normal.TotalFeaturesSecond+40,"lower detector contrast threshold recovers faint specks absent at normal sensitivity");
        Check(sensitive.ReconstructionReport!.Accepted&&boosted.ReconstructionReport!.Accepted&&boosted.TotalFeaturesFirst>normal.TotalFeaturesFirst+40,"sensitive and locally enhanced faint-speck pairs pass the unchanged correspondence checks");
        Check(sensitive.Trial&&sensitive.AnalysisOptions!.ContrastThreshold==.003&&boosted.AnalysisOptions!.EnhanceLocalContrast&&!sensitive.Report.Accepted,"preview distinguishes its new acceptance and settings from the saved rejected neighbor");
        Check(File.ReadAllText(Path.Combine(folder,"project.json"))==metadata&&File.ReadAllBytes(Path.Combine(folder,first.File)).SequenceEqual(original),"sensitivity previews preserve scan metadata, placement and original image bytes");
        var cacheFiles=Directory.GetFiles(Path.Combine(folder,"registration-cache"),"*.bin");
        Check(cacheFiles.Length==6&&cacheFiles.Count(f=>Path.GetFileName(f).StartsWith("sift-v1-"))==2,"normal, sensitive and enhanced detectors use separate caches while preserving legacy cache compatibility");
        var times=cacheFiles.ToDictionary(f=>f,File.GetLastWriteTimeUtc);var repeated=Preview(options with {ContrastThreshold=.003});
        Check(repeated.TotalFeaturesFirst==sensitive.TotalFeaturesFirst&&times.All(pair=>File.GetLastWriteTimeUtc(pair.Key)==pair.Value),"repeating a sensitivity preview reuses its detector cache");
        foreach(double invalid in new[]{0d,double.NaN,.3})try{Preview(options with {ContrastThreshold=invalid});throw new Exception("invalid detector threshold accepted");}catch(ArgumentException){}
        Check(new RegistrationOptions().ContrastThreshold==.04&&!new RegistrationOptions().EnhanceLocalContrast,"legacy detection behavior remains the default and invalid thresholds are rejected");
        try{RegistrationDiagnostics.Recheck(id,0,1,new("stale.json",options));throw new Exception("stale trial accepted");}catch(InvalidOperationException){Console.WriteLine("PASS sensitivity preview rejects a stale refinement version");}
    }
}
