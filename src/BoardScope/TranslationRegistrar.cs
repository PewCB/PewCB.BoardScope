using OpenCvSharp;

namespace BoardScope;

public sealed class TranslationRegistrar
{
    // Only a few decoded source images are held, independent of the scan size.
    internal sealed class ImageCache(string folder,LightCorrection? correction=null):IDisposable
    {
        private readonly Dictionary<string,(Mat Image,long Used)> _images=[];
        private long _clock;
        public Mat Get(Tile tile)
        {
            if(_images.TryGetValue(tile.File,out var saved)){_images[tile.File]=(saved.Image,++_clock);return saved.Image;}
            var image=LightCorrection.Read(Path.Combine(folder,tile.File),ImreadModes.Grayscale,correction);
            if(image.Empty()||image.Width!=tile.Width||image.Height!=tile.Height){image.Dispose();throw new IOException("Source capture dimensions changed: "+tile.File);}
            while(_images.Count>=4){var oldest=_images.MinBy(p=>p.Value.Used);oldest.Value.Image.Dispose();_images.Remove(oldest.Key);}
            _images[tile.File]=(image,++_clock);return image;
        }
        public void Dispose(){foreach(var image in _images.Values)image.Image.Dispose();}
    }
    public RegistrationReport Run(ScanProject p,string folder,RegistrationOptions options,Action<string,int,int,string> progress,CancellationToken ct)
    {
        options.Validate();string name=RegistrationOptions.MethodName(options.Method);var candidates=new List<(int I,int J)>();
        for(int i=0;i<p.Tiles.Count;i++)for(int j=i+1;j<p.Tiles.Count;j++){
            var r=Overlap(p.Tiles[i],p.Tiles[j]);
            if(r.Width>=64&&r.Height>=64&&r.Width*r.Height>=Math.Min((double)p.Tiles[i].Width*p.Tiles[i].Height,(double)p.Tiles[j].Width*p.Tiles[j].Height)*.04)candidates.Add((i,j));
        }
        if(candidates.Count==0)throw new InvalidOperationException("The stage layout has no overlapping neighbors. Check frame width and axis mapping.");
        string version=Guid.NewGuid().ToString("N")[..8],debugFolder="registration-"+version+"-debug";Directory.CreateDirectory(Path.Combine(folder,debugFolder));
        var reports=new List<PairReport>();var links=new List<FeatureLink>();double pixelsPerMm=p.Tiles[0].Width/p.Placement!.FovWidth;
        using var correction=LightCorrection.For(p);
        using(var cache=new ImageCache(folder,correction))for(int c=0;c<candidates.Count;c++){
            ct.ThrowIfCancellationRequested();var (i,j)=candidates[c];
            var result=Match(i,j,p.Tiles[i],p.Tiles[j],cache.Get(p.Tiles[i]),cache.Get(p.Tiles[j]),options,pixelsPerMm,ct);
            string debugFile=$"{debugFolder}/pair-{i}-{j}.json";var report=result.Diagnostics.Report with {DebugFile=debugFile};
            Files.Save(Path.Combine(folder,debugFile),result.Diagnostics with {Report=report});reports.Add(report);
            if(result.Link!=null)links.Add(result.Link);
            progress("matching",c+1,candidates.Count,$"{name} of overlap strips · {reports.Count(r=>r.Accepted)} reliable pairs / {c+1} checked");
        }
        if(links.Count==0)throw new InvalidOperationException($"No reliable {name} pairs found. Original mosaic kept. Verify overlap, stage mapping and correlation thresholds.");
        var observations=links.ToArray();
        var solution=SimilaritySolver.SolveLayout(p.Tiles,links,options,pixelsPerMm,ct,(pass,message)=>progress("optimizing",pass,5,name+" constraints · "+message));
        double threshold=Math.Max(8,6/options.ImageFactor(p.Tiles[0].Width,p.Tiles[0].Height));
        var rejected=links.Where(l=>SimilaritySolver.Error(solution.Tiles[l.First].Transform,solution.Tiles[l.Second].Transform,l)>threshold).Select(l=>(l.First,l.Second)).ToHashSet();
        if(rejected.Count>0){
            links.RemoveAll(l=>rejected.Contains((l.First,l.Second)));
            reports=reports.Select(r=>rejected.Contains((r.First,r.Second))?r with {Accepted=false,Reason="Disagrees with the global layout"}:r).ToList();
            if(links.Count==0)throw new InvalidOperationException($"All {name} pairs disagree with the stage-constrained layout. Original mosaic kept.");
            solution=SimilaritySolver.SolveLayout(p.Tiles,links,options,pixelsPerMm,ct,(pass,message)=>progress("optimizing",pass,5,$"Discarded inconsistent {name} pairs · "+message));
        }
        var byPair=observations.ToDictionary(l=>(l.First,l.Second));
        reports=reports.Select(r=>r with {GlobalErrorPixels=byPair.TryGetValue((r.First,r.Second),out var l)?SimilaritySolver.Error(solution.Tiles[l.First].Transform,solution.Tiles[l.Second].Transform,l):null}).ToList();
        for(int i=0;i<p.Tiles.Count;i++){var tile=p.Tiles[i];tile.Registration=solution.Tiles[i];tile.X=tile.Registration.Transform.X;tile.Y=tile.Registration.Transform.Y;}
        double error=SimilaritySolver.Median(links.Select(l=>SimilaritySolver.Error(solution.Tiles[l.First].Transform,solution.Tiles[l.Second].Transform,l)).ToArray());
        string reportFile="registration-"+version+".json";
        var registration=new RegistrationReport(options,DateTimeOffset.UtcNow,reports.Count(r=>r.Accepted),reports.Count(r=>!r.Accepted),solution.Tiles.Count(t=>t.Links>0),solution.Tiles.Count(t=>t.Status=="limit-reached"),error,reportFile,reports){StageCorrection=solution.StageCorrection,FeaturePairs=0,TranslationPairs=links.Count};
        Files.Save(Path.Combine(folder,reportFile),new {report=registration,tiles=p.Tiles.Select((t,i)=>new{sequence=i+1,t.File,t.StageX,t.StageY,t.BaseX,t.BaseY,t.Registration})});
        progress("optimizing",5,5,$"{name} registration ready · {registration.SupportedTiles}/{p.Tiles.Count} supported · {registration.AcceptedPairs} pairs · median constraint error {error:F2} px");
        return registration;
    }
    public PairDiagnostics InspectPair(ScanProject p,string folder,int first,int second,CancellationToken ct=default,RegistrationOptions? trialOptions=null)
    {
        if(first<0||second<=first||second>=p.Tiles.Count)throw new ArgumentException("Select a valid pair of different tiles.");
        var saved=p.Registration?.Pairs.SingleOrDefault(r=>r.First==first&&r.Second==second)??throw new ArgumentException("This pair was not checked by the saved refinement.");
        var options=trialOptions??p.Registration!.Options;options.Validate();
        Tile Baseline(Tile t)=>new(){File=t.File,Width=t.Width,Height=t.Height,X=t.BaseX??t.NominalX,Y=t.BaseY??t.NominalY};
        using var correction=LightCorrection.For(p);
        var a=Baseline(p.Tiles[first]);var b=Baseline(p.Tiles[second]);using var cache=new ImageCache(folder,correction);
        var result=Match(first,second,a,b,cache.Get(a),cache.Get(b),options,a.Width/(p.Placement?.FovWidth??p.Settings.FovWidth),ct);
        return result.Diagnostics with {Report=saved,Reconstructed=true,ReconstructionReport=result.Diagnostics.Report,Trial=trialOptions!=null};
    }
    private static Rect2d Overlap(Tile a,Tile b){double x=Math.Max(a.X,b.X),y=Math.Max(a.Y,b.Y);return new(x,y,Math.Min(a.X+a.Width,b.X+b.Width)-x,Math.Min(a.Y+a.Height,b.Y+b.Height)-y);}
    private static (PairDiagnostics Diagnostics,FeatureLink? Link) Match(int i,int j,Tile a,Tile b,Mat imageA,Mat imageB,RegistrationOptions options,double pixelsPerMm,CancellationToken ct) => options.Method=="ncc"?NccRegistrar.Match(i,j,a,b,imageA,imageB,options,pixelsPerMm,ct):PhaseRegistrar.Match(i,j,a,b,imageA,imageB,options,pixelsPerMm,ct);
}
