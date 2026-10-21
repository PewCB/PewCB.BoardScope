using BoardScope;
using OpenCvSharp;
using System.Security.Cryptography;
using Stitcher=BoardScope.Stitcher;

static class LightBackgroundChecks
{
    static void Check(bool condition,string message){if(!condition)throw new Exception(message);Console.WriteLine("PASS "+message);}
    static double Field(int b,double x,double y)=>b switch{
        0=>140+24*x-12*y+7*x*y-14*x*x+5*Math.Pow(y,4),
        1=>165-20*x+18*y+9*y*y+4*x*x*y,
        _=>185+15*x+12*y-10*x*x+6*x*Math.Pow(y,3)};
    static Mat Reference(int width=256,int height=192,double reflectance=1,bool dust=false)
    {
        var image=new Mat(height,width,MatType.CV_8UC3);var random=new Random(19);
        for(int y=0;y<height;y++)for(int x=0;x<width;x++){
            var v=new Vec3b();for(int b=0;b<3;b++)v[b]=(byte)Math.Clamp(Math.Round(Field(b,2*(x+.5)/width-1,2*(y+.5)/height-1)*reflectance+(dust?random.NextDouble()*2-1:0)),0,255);image.Set(y,x,v);
        }
        if(dust)Cv2.Circle(image,new Point(width/3,height/2),3,Scalar.All(35),-1);return image;
    }
    static double Spread(Mat image)
    {
        Cv2.MeanStdDev(image,out Scalar mean,out Scalar std);return Math.Max(std.Val0,Math.Max(std.Val1,std.Val2));
    }
    public static void Run()
    {
        string previous=Files.Root;Files.Root=Path.Combine(previous,"light-check");Directory.CreateDirectory(Files.Root);
        try{RunCore();}finally{Files.Root=previous;}
    }
    static void RunCore()
    {
        using var reference=Reference(dust:true);var profile=LightBackground.Fit(reference,"test camera","test-device");
        double error=0;for(int y=0;y<=10;y++)for(int x=0;x<=10;x++)for(int b=0;b<3;b++)error=Math.Max(error,Math.Abs(profile.Value(b,x/5d-1,y/5d-1)-Field(b,x/5d-1,y/5d-1)));
        Check(profile.Coefficients.All(c=>c.Length==15)&&error<1.5,"fourth-order per-channel fit recovers cross terms and edge falloff despite noise and a dust speck");
        using var clean=Reference();using var dim=Reference(reflectance:.45);
        foreach(int rotation in new[]{0,90,180,270})foreach(bool flipX in new[]{false,true})foreach(bool flipY in new[]{false,true}){
            var settings=new ScanSettings{Rotation=rotation,FlipX=flipX,FlipY=flipY};using var corrected=Stitcher.Orient(dim,settings);using var correction=new LightCorrection(profile,settings);correction.Apply(corrected);
            var mean=Cv2.Mean(corrected);Check(Spread(corrected)<.85&&Enumerable.Range(0,3).All(b=>Math.Abs(mean[b]-profile.Mean[b]*.45)<1),$"light correction is uniform and preserves average channel levels at rotation {rotation}, mirrors {flipX}/{flipY}");
        }
        using(var mismatch=new Mat(32,32,MatType.CV_8UC3))using(var correction=new LightCorrection(profile,new())){
            try{correction.Apply(mismatch);throw new Exception("Mismatched correction accepted");}catch(InvalidOperationException){}
        }
        foreach(byte value in new byte[]{0,255})using(var invalid=new Mat(64,64,MatType.CV_8UC3,Scalar.All(value))){
            try{LightBackground.Fit(invalid);throw new Exception("Invalid white reference accepted");}catch(ArgumentException){}
        }
        Check(true,"clipped, dark and size-mismatched references fail explicitly");
        var state=LightBackgroundStore.Capture(clean,"test camera","test-device");profile=state.Profile!;
        Check(state.Enabled&&LightBackgroundStore.Load().Profile?.Id==profile.Id&&File.Exists(Path.Combine(Files.Root,"light-backgrounds",profile.Id,"reference.png"))&&File.Exists(Path.Combine(Files.Root,"light-backgrounds",profile.Id,"model.json")),"reference PNG and analytic model persist independently of app lifetime");
        foreach(string kind in new[]{"reference","fit","corrected"})using(var preview=Cv2.ImDecode(LightBackgroundStore.Preview(profile.Id,kind),ImreadModes.Color))Check(!preview.Empty()&&preview.Width<=640&&(kind!="corrected"||Spread(preview)<1),"background preview works: "+kind);

        string id="lighting-mosaic",folder=Files.ProjectPath(id);Directory.CreateDirectory(Path.Combine(folder,"captures"));
        var p=new ScanProject{Id=id,Name="Lighting check",Settings=new(){FovWidth=2.56,FovHeight=1.92,Overlap=25},BlendMode="sharp"};
        for(int i=0;i<2;i++){string file=$"captures/{i}.png";Cv2.ImWrite(Path.Combine(folder,file),dim);p.Tiles.Add(new(){File=file,Width=256,Height=192,NominalX=192*i,Col=i});}
        string captureHash=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(folder,p.Tiles[0].File))));
        LightBackgroundStore.Enable(false);new Stitcher().Run(p,(_,_,_,_)=>{},CancellationToken.None);
        var entry=p.Stitches.Single();var original=entry.Maps.Single();var positions=p.Tiles.Select(t=>(t.X,t.Y,t.StageX,t.StageY)).ToArray();
        string alignmentText=File.ReadAllText(Path.Combine(folder,entry.AlignmentFile!));
        using(var before=Cv2.ImDecode(MosaicEffects.Preview(id,entry.Id,original.Id),ImreadModes.Color))Check(Spread(before)>5,"uncorrected test map contains the repeating tile illumination gradient");
        LightBackgroundStore.Enable(true);new Stitcher().Reblend(p,(_,_,_,_)=>{},CancellationToken.None);var adjusted=entry.Maps.Last();
        using(var after=Cv2.ImDecode(MosaicEffects.Preview(id,entry.Id,adjusted.Id),ImreadModes.Color))Check(Spread(after)<1.3&&Enumerable.Range(0,3).All(b=>Math.Abs(Cv2.Mean(after)[b]-profile.Mean[b]*.45)<2),"reblending removes repeating tile gradients at native mosaic resolution without swapping color channels");
        Check(adjusted.LightBackground?.Id==profile.Id&&entry.Maps.Count==2&&positions.SequenceEqual(p.Tiles.Select(t=>(t.X,t.Y,t.StageX,t.StageY)))&&File.ReadAllText(Path.Combine(folder,entry.AlignmentFile!))==alignmentText,"reblend records its calibration and preserves saved alignment, gross positions and the original map");
        Check(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(folder,p.Tiles[0].File)))).Equals(captureHash),"original capture bytes are unchanged");
        using(var correction=LightCorrection.For(p))using(var expected=LightCorrection.Read(Path.Combine(folder,p.Tiles[0].File),ImreadModes.Grayscale,correction))using(var cache=new TranslationRegistrar.ImageCache(folder,correction))Check(Cv2.Norm(expected,cache.Get(p.Tiles[0]),NormTypes.INF)==0,"pixel matching and seam costs use corrected per-channel data before grayscale conversion");
        foreach(var tile in p.Tiles){string root=Path.Combine(folder,Path.ChangeExtension(tile.SourceDzi,null)+"_files");using var tileImage=Cv2.ImRead(Path.Combine(root,"8","0_0.jpg"));Check(Spread(tileImage)<1.3&&Enumerable.Range(0,3).All(b=>Math.Abs(Cv2.Mean(tileImage)[b]-profile.Mean[b]*.45)<2),"individual tile pyramid displays the same colors and flat-field correction as the mosaic");}
        var viewed=StitchCatalog.View(Files.Load(id),entry.Id,original.Id);Check(viewed.LightBackground==null&&viewed.Tiles.All(t=>!t.SourceDzi!.Contains("light-")),"loading the original map restores the uncorrected tile views");
        viewed=StitchCatalog.View(Files.Load(id),entry.Id,adjusted.Id);Check(viewed.LightBackground?.Id==profile.Id&&viewed.Tiles.All(t=>t.SourceDzi!.Contains(profile.Id)),"loading an adjusted map restores its exact calibration and tile views");
        MosaicEffects.Apply(Files.Load(id),new(entry.Id,adjusted.Id,new(){Brightness=10}),(_,_,_,_)=>{});
        Check(Files.Load(id).Stitches.Single().Maps.Last().LightBackground?.Id==profile.Id,"image effects preserve the lighting calibration of their source map");

        // A later reference must not alter existing corrected maps or source views.
        using var other=Reference(reflectance:.8);LightBackgroundStore.Capture(other,"test camera","test-device");
        viewed=StitchCatalog.View(Files.Load(id),entry.Id,adjusted.Id);Check(viewed.LightBackground?.Id==profile.Id,"replacing the active reference leaves historical maps tied to their original calibration");
        LightBackgroundStore.Enable(false);new Stitcher().Reblend(p,(_,_,_,_)=>{},CancellationToken.None);Check(p.LightBackground==null&&p.Stitches.Single().Maps.Last().LightBackground==null,"disabling correction creates a new uncorrected map without affecting earlier versions");
        Console.WriteLine("Light background evidence: "+Files.Root);
    }
}
