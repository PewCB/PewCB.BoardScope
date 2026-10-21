using BoardScope;
using OpenCvSharp;
using Stitcher = BoardScope.Stitcher;

static class BlendChecks
{
    static void Check(bool condition,string message)
    {
        if(!condition)throw new Exception(message);Console.WriteLine("PASS "+message);
    }
    public static void Run()
    {
        string id="sharp-seam-check",folder=Files.ProjectPath(id);Directory.CreateDirectory(Path.Combine(folder,"captures"));
        using(var black=new Mat(320,448,MatType.CV_8UC3,Scalar.All(0)))Cv2.ImWrite(Path.Combine(folder,"captures/a.png"),black);
        using(var white=new Mat(320,448,MatType.CV_8UC3,Scalar.All(240)))Cv2.ImWrite(Path.Combine(folder,"captures/b.png"),white);
        double angle=1.5*Math.PI/180;
        var a=new Tile {File="captures/a.png",Width=448,Height=320,X=0,Y=0,StageX=5,StageY=8,NominalX=17,NominalY=29};
        var b=new Tile {File="captures/b.png",Width=448,Height=320,X=200,Y=0,BaseX=201,BaseY=2,StageX=7,StageY=8,NominalX=219,NominalY=31,
            Registration=new(new(Math.Cos(angle)*1.01,Math.Sin(angle)*1.01,200,-2),3,4,2,1,"refined")};
        var report=new RegistrationReport(new(),DateTimeOffset.UtcNow,1,0,2,0,1,"existing-report.json",[]);
        var p=new ScanProject {Id=id,Tiles=[a,b],Settings=new(){FovWidth=4.48,FovHeight=3.2},BlendMode="sharp",Registration=report,StageMosaic=new("previous.dzi",648,320,0,0)};
        var metadata=p.Tiles.Select(t=>(t.X,t.Y,t.BaseX,t.BaseY,t.StageX,t.StageY,t.NominalX,t.NominalY,t.Registration)).ToArray();
        var stitcher=new Stitcher();stitcher.Reblend(p,(_,_,_,_)=>{},CancellationToken.None);
        Check(p.Tiles.Select(t=>(t.X,t.Y,t.BaseX,t.BaseY,t.StageX,t.StageY,t.NominalX,t.NominalY,t.Registration)).SequenceEqual(metadata)&&System.Text.Json.JsonSerializer.Serialize(p.Registration,Files.Json)==System.Text.Json.JsonSerializer.Serialize(report,Files.Json)&&p.StageMosaic!.Dzi=="previous.dzi","reblending restores saved similarity corrections, raw coordinates and the stage comparison");
        int top=(int)Math.Ceiling(Math.Log2(Math.Max(p.Width,p.Height)));
        string root=Path.Combine(folder,Path.GetFileNameWithoutExtension(p.Dzi!)+"_files",top.ToString());
        using var image=Cv2.ImRead(Path.Combine(root,"0_0.jpg"));
        var ca=Similarity.For(a).Apply(a.Width/2d,a.Height/2d);var cb=Similarity.For(b).Apply(b.Width/2d,b.Height/2d);
        int checkedPixels=0;
        for(int y=50;y<270;y+=11)for(int x=260;x<430;x+=7)
        {
            double difference=(x-ca.X)*(x-ca.X)+(y-ca.Y)*(y-ca.Y)-(x-cb.X)*(x-cb.X)-(y-cb.Y)*(y-cb.Y);
            // Skip JPEG blocks around the seam; JPEG itself may introduce ringing.
            if(Math.Abs(difference)/double.Hypot(2*(cb.X-ca.X),2*(cb.Y-ca.Y))<16)continue;
            byte expected=(byte)(difference<0?0:240);
            var color=image.At<Vec3b>(y-(int)p.OriginY,x-(int)p.OriginX);
            if(Math.Abs(color.Item0-expected)>3||Math.Abs(color.Item1-expected)>3||Math.Abs(color.Item2-expected)>3)throw new Exception("Sharp seams mixed overlapping tile pixels.");
            checkedPixels++;
        }
        Check(checkedPixels>300,"rotated/scaled tiles use a straight nearest-centre seam, with black pixels retained instead of mixed");
        string sharpDzi=p.Dzi!;p.BlendMode="feather";stitcher.Reblend(p,(_,_,_,_)=>{},CancellationToken.None);
        top=(int)Math.Ceiling(Math.Log2(Math.Max(p.Width,p.Height)));
        using var mixed=Cv2.ImRead(Path.Combine(folder,Path.GetFileNameWithoutExtension(p.Dzi!) + "_files",top.ToString(),"0_0.jpg"));
        byte middle=mixed.At<Vec3b>(160-(int)p.OriginY,320-(int)p.OriginX).Item0;
        Check(middle>30&&middle<210&&p.Dzi!=sharpDzi&&File.Exists(Path.Combine(folder,sharpDzi)),"feather mode still mixes overlaps and reblending writes a new pyramid");
        try {p.BlendMode="unknown";stitcher.Reblend(p,(_,_,_,_)=>{},CancellationToken.None);throw new Exception("Invalid seam mode accepted.");}catch(ArgumentException){Console.WriteLine("PASS invalid seam mode is rejected before rendering");}
    }
}
