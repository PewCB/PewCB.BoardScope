using BoardScope;
using OpenCvSharp;

static class NativeMatchingChecks
{
    public static void Run()
    {
        const string id="native-matching-check";string folder=Files.ProjectPath(id);Directory.CreateDirectory(Path.Combine(folder,"captures"));
        using var board=new Mat(960,2200,MatType.CV_8UC3,Scalar.All(30));var random=new Random(88);
        for(int k=0;k<1200;k++)Cv2.Circle(board,new(random.Next(10,2190),random.Next(10,950)),random.Next(2,6),new Scalar(random.Next(80,240),random.Next(80,240),random.Next(80,240)),random.Next(1,3));
        using(var first=new Mat(board,new Rect(0,0,1800,960)))Cv2.ImWrite(Path.Combine(folder,"captures/a.png"),first);
        using(var second=new Mat(board,new Rect(400,0,1800,960)))Cv2.ImWrite(Path.Combine(folder,"captures/b.png"),second);
        var p=new ScanProject {Id=id,Placement=new(){FovWidth=18,FovHeight=9.6},Tiles=[new(){File="captures/a.png",Width=1800,Height=960},new(){File="captures/b.png",Width=1800,Height=960,X=400,Col=1}]};
        var options=new RegistrationOptions();
        var report=new SimilarityRegistrar().Run(p,folder,options,(_,_,_,_)=>{},CancellationToken.None);
        if(report.AcceptedPairs!=1)throw new Exception("Native-resolution pixel overlap did not register.");
        var files=Directory.GetFiles(Path.Combine(folder,"registration-cache"),"*-0-6000-*.bin");
        if(files.Length!=2)throw new Exception("Native matching reused reduced-resolution feature caches.");
        foreach(string file in files)
        {
            using var reader=new BinaryReader(File.OpenRead(file));int count=reader.ReadInt32();double factor=reader.ReadDouble();
            if(count<12||factor!=1)throw new Exception("Full-resolution images were reduced before feature detection.");
        }
        Console.WriteLine("PASS images larger than 1600 px match at native resolution and cache features with factor 1");
    }
}
