using BoardScope;
using OpenCvSharp;
using System.Text.Json;
using Stitcher=BoardScope.Stitcher;

static class EffectChecks
{
    static void Check(bool condition,string message){if(!condition)throw new Exception(message);Console.WriteLine("PASS "+message);}
    public static void Run()
    {
        var neutral=new ImageEffectTransform(new());var random=new Random(42);
        Check(Enumerable.Range(0,2000).All(_=>{byte r=(byte)random.Next(256),g=(byte)random.Next(256),b=(byte)random.Next(256);return neutral.Pixel(b,g,r)==new Vec3b(b,g,r);}),"neutral effects preserve every tested RGB byte exactly");
        Vec3b Pixel(ImageEffects e,byte value=64)=>new ImageEffectTransform(e).Pixel(value,value,value);
        Check(Pixel(new(){Brightness=50}).Item0==128&&Pixel(new(){Contrast=50}).Item0<64,"brightness is exposure-like and contrast pivots around middle gray");
        int Shadow(byte v)=>Pixel(new(){Shadows=50},v).Item0-v;
        int Highlight(byte v)=>v-Pixel(new(){Highlights=-50},v).Item0;
        Check(Shadow(32)>Shadow(224)&&Highlight(224)>Highlight(32),"shadows and highlights preferentially affect their respective tone regions");
        var gray=new ImageEffectTransform(new(){Saturation=-100}).Pixel(40,100,200);
        Check(gray.Item0==gray.Item1&&gray.Item1==gray.Item2,"minimum saturation produces neutral grayscale");
        var warm=Pixel(new(){Temperature=75},128);var magenta=Pixel(new(){Tint=75},128);
        Check(warm.Item2>warm.Item0&&magenta.Item0>magenta.Item1&&magenta.Item2>magenta.Item1,"temperature warms toward red and positive tint moves toward magenta");
        foreach(var invalid in new[]{new ImageEffects{Brightness=101},new ImageEffects{Tint=double.NaN},new ImageEffects{Highlights=double.PositiveInfinity}})
            try{invalid.Validate();throw new Exception("Invalid effect accepted");}catch(ArgumentException){}
        Console.WriteLine("PASS out-of-range and non-finite effects are rejected");
        var settings=new[]{new ImageEffects(),new(){Brightness=25,Contrast=30,Shadows=45,Highlights=-35,Saturation=20,Temperature=30,Tint=-20},new(){Brightness=-100,Contrast=100,Shadows=-100,Highlights=100,Saturation=-100,Temperature=-100,Tint=100},new(){Brightness=100,Contrast=-100,Shadows=100,Highlights=-100,Saturation=100,Temperature=100,Tint=-100}};
        var pixels=Enumerable.Range(0,128).Select(_=>new[]{random.Next(256),random.Next(256),random.Next(256),random.Next(256)}).ToArray();
        Files.Save(Path.Combine(Files.Root,"effect-pixels.json"),settings.Select(s=>new{settings=s,pixels=pixels.Select(rgba=>{var c=new ImageEffectTransform(s).Pixel((byte)rgba[2],(byte)rgba[1],(byte)rgba[0]);return new{input=rgba,output=new[]{(int)c.Item2,c.Item1,c.Item0,rgba[3]}};})}));

        string id="effects-map-check",folder=Files.ProjectPath(id);Directory.CreateDirectory(Path.Combine(folder,"captures"));
        using var image=new Mat(900,1400,MatType.CV_8UC3);
        for(int y=0;y<900;y+=25)for(int x=0;x<1400;x+=25)Cv2.Rectangle(image,new Rect(x,y,Math.Min(25,1400-x),Math.Min(25,900-y)),new Scalar(30+x/10,25+y/7,50+(x+y)/15),-1);
        Cv2.ImWrite(Path.Combine(folder,"captures/a.png"),image);
        var project=new ScanProject{Id=id,Name="Effects test",BlendMode="sharp",Settings=new(){FovWidth=14,FovHeight=9},Tiles=[new(){File="captures/a.png",Width=1400,Height=900}]};
        new Stitcher().Run(project,(_,_,_,_)=>{},CancellationToken.None);
        var entry=project.Stitches.Single();var source=entry.Maps.Single();string sourceRoot=Path.Combine(folder,Path.ChangeExtension(source.Dzi,null)+"_files");
        string alignment=File.ReadAllText(Path.Combine(folder,entry.AlignmentFile!));byte[] capture=File.ReadAllBytes(Path.Combine(folder,"captures/a.png"));
        var originals=Directory.GetFiles(sourceRoot,"*",SearchOption.AllDirectories).ToDictionary(f=>f,File.ReadAllBytes);
        using(var full=Cv2.ImDecode(MosaicEffects.Preview(id,entry.Id,source.Id),ImreadModes.Color))Check(Math.Max(full.Width,full.Height)<=1026,"whole-board preview is bounded to about one megapixel");
        using(var cropped=Cv2.ImDecode(MosaicEffects.Preview(id,entry.Id,source.Id,50,60,200,120),ImreadModes.Color))
        using(var top=Cv2.ImRead(Path.Combine(sourceRoot,"11","0_0.jpg")))
            Check(cropped.Width==200&&cropped.Height==120&&cropped.At<Vec3b>(50,50)==top.At<Vec3b>(110,100),"current-view preview crops the actual selected map at native resolution when small enough");
        string metadata=File.ReadAllText(Path.Combine(folder,"project.json"));
        try{MosaicEffects.Preview(id,entry.Id,source.Id,-1,0,100,100);throw new Exception("Invalid crop accepted");}catch(ArgumentException){}
        Check(File.ReadAllText(Path.Combine(folder,"project.json"))==metadata,"preview reads do not change project metadata");
        var effects=settings[1];MosaicEffects.Apply(project,new(entry.Id,source.Id,effects),(_,_,_,_)=>{});
        var applied=Files.Load(id);var adjusted=applied.Stitches.Single().Maps.Last();string adjustedRoot=Path.Combine(folder,Path.ChangeExtension(adjusted.Dzi,null)+"_files");
        Check(adjusted.Effects==effects&&adjusted.EffectSourceMapId==source.Id&&applied.ActiveMapId==adjusted.Id&&applied.Stitches.Count==1&&entry.Maps.Count==2,"Apply creates a map under the same alignment and persists the recipe/source relationship");
        using(var before=Cv2.ImRead(Path.Combine(sourceRoot,"11","0_0.jpg")))
        using(var after=Cv2.ImRead(Path.Combine(adjustedRoot,"11","0_0.jpg")))
        {
            var c=before.At<Vec3b>(100,100);var expected=new ImageEffectTransform(effects).Pixel(c.Item0,c.Item1,c.Item2);var actual=after.At<Vec3b>(100,100);
            Check(Math.Abs(actual.Item0-expected.Item0)<=3&&Math.Abs(actual.Item1-expected.Item1)<=3&&Math.Abs(actual.Item2-expected.Item2)<=3,"full-resolution tiles contain the same effect transform as the preview, within JPEG rounding");
        }
        Check(Directory.GetDirectories(adjustedRoot).Length==12&&adjusted.Width==source.Width&&adjusted.Height==source.Height&&File.ReadAllText(Path.Combine(folder,entry.AlignmentFile!))==alignment&&capture.SequenceEqual(File.ReadAllBytes(Path.Combine(folder,"captures/a.png")))&&originals.All(k=>k.Value.SequenceEqual(File.ReadAllBytes(k.Key))),"all zoom levels are rebuilt while geometry, original captures, alignment and previous map bytes remain intact");
        using(var low=Cv2.ImRead(Path.Combine(adjustedRoot,"8","0_0.jpg")))
        using(var oldLow=Cv2.ImRead(Path.Combine(sourceRoot,"8","0_0.jpg")))Check(Cv2.Norm(low,oldLow,NormTypes.L1)>1000,"lower zoom levels also contain the applied effects");
        MosaicEffects.Apply(applied,new(entry.Id,source.Id,effects),(_,_,_,_)=>{});
        var repeated=Files.Load(id).Stitches.Single().Maps.Last();
        Check(File.ReadAllBytes(Path.Combine(adjustedRoot,"11","0_0.jpg")).SequenceEqual(File.ReadAllBytes(Path.Combine(folder,Path.ChangeExtension(repeated.Dzi,null)+"_files","11","0_0.jpg"))),"re-editing from the original source does not compound effects or JPEG generations");
        var beforeCancel=File.ReadAllText(Path.Combine(folder,"project.json"));var roots=Directory.GetDirectories(folder,"mosaic-effects-*_files").Order().ToArray();
        using var cancellation=new CancellationTokenSource();
        try{MosaicEffects.Apply(Files.Load(id),new(entry.Id,source.Id,effects),(phase,_,_,_)=>{if(phase=="pyramid")cancellation.Cancel();},cancellation.Token);throw new Exception("Cancellation ignored");}catch(OperationCanceledException){}
        Check(File.ReadAllText(Path.Combine(folder,"project.json"))==beforeCancel&&Directory.GetDirectories(folder,"mosaic-effects-*_files").Order().SequenceEqual(roots),"cancellation preserves the active map and removes incomplete generated artifacts");
        string broken=Path.Combine(sourceRoot,"11","0_0.jpg");File.Move(broken,broken+".saved");
        try
        {
            try{MosaicEffects.Apply(Files.Load(id),new(entry.Id,source.Id,effects),(_,_,_,_)=>{});throw new Exception("Broken map accepted");}catch(AggregateException e)when(e.InnerExceptions.Any(x=>x is IOException)){}
            Check(File.ReadAllText(Path.Combine(folder,"project.json"))==beforeCancel&&Directory.GetDirectories(folder,"mosaic-effects-*_files").Order().SequenceEqual(roots),"a missing full-resolution source tile fails cleanly without publishing a partial map");
        }
        finally{File.Move(broken+".saved",broken);}
        MosaicEffects.Apply(Files.Load(id),new(entry.Id,source.Id,new()),(_,_,_,_)=>{});var cleared=Files.Load(id).Stitches.Single().Maps.Last();
        Check(File.ReadAllBytes(Path.Combine(folder,Path.ChangeExtension(cleared.Dzi,null)+"_files","11","0_0.jpg")).SequenceEqual(File.ReadAllBytes(broken)),"clearing effects preserves full-resolution source JPEG bytes exactly");
        StitchCatalog.Delete(id,entry.Id,source.Id);var standalone=Files.Load(id);
        using(var preview=Cv2.ImDecode(MosaicEffects.Preview(id,entry.Id,adjusted.Id),ImreadModes.Color))Check(!preview.Empty()&&standalone.Stitches.Single().Maps.Any(m=>m.Id==adjusted.Id),"adjusted maps remain independently viewable after deleting their source map");
    }
}
