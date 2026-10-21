using System.Runtime.InteropServices;
using OpenCvSharp;
using OpenCvSharp.Features2D;

namespace BoardScope;

public sealed partial class SimilarityRegistrar
{
    private sealed record Features(Point2f[] Points,Mat Descriptors,double Factor):IDisposable {public void Dispose()=>Descriptors.Dispose();}
    private sealed class FeatureCache(string folder,RegistrationOptions options,int capacity=24,LightCorrection? correction=null):IDisposable
    {
        private readonly Dictionary<string,(Features Set,long Used)> _sets=[];
        private long _clock;
        public Features Get(Tile tile)
        {
            if(_sets.TryGetValue(tile.File,out var saved)){_sets[tile.File]=(saved.Set,++_clock);return saved.Set;}
            string source=Path.Combine(folder,tile.File);var info=new FileInfo(source);
            string cacheRoot=Path.Combine(folder,"registration-cache");Directory.CreateDirectory(cacheRoot);
            // Preserve legacy caches only for the exact legacy detector settings.
            string detector=options.ContrastThreshold==.04&&!options.EnhanceLocalContrast?"sift-v1":"sift-v2-"+options.FeatureKey();
            if(correction!=null)detector+="-"+correction.Key;
            string path=Path.Combine(cacheRoot,$"{detector}-{Path.GetFileNameWithoutExtension(tile.File)}-{options.WorkingSize}-{options.MaxFeatures}-{info.Length}-{info.LastWriteTimeUtc.Ticks}.bin");
            Features set;
            if(File.Exists(path))
            {
                using var reader=new BinaryReader(File.OpenRead(path));int count=reader.ReadInt32();double factor=reader.ReadDouble();
                if(count<0||count>options.MaxFeatures*2)throw new IOException("Invalid registration feature cache.");
                var points=new Point2f[count];for(int i=0;i<count;i++)points[i]=new(reader.ReadSingle(),reader.ReadSingle());
                var descriptors=new Mat(count,128,MatType.CV_32F);var values=new float[count*128];for(int i=0;i<values.Length;i++)values[i]=reader.ReadSingle();
                if(values.Length>0)Marshal.Copy(values,0,descriptors.Data,values.Length);set=new(points,descriptors,factor);
            }
            else
            {
                using var raw=LightCorrection.Read(source,ImreadModes.Grayscale,correction);
                if(raw.Empty()||raw.Width!=tile.Width||raw.Height!=tile.Height)throw new IOException("Source capture dimensions changed: "+tile.File);
                double factor=options.ImageFactor(raw.Width,raw.Height);
                using var small=new Mat();
                if(factor<1)Cv2.Resize(raw,small,new Size((int)Math.Round(raw.Width*factor),(int)Math.Round(raw.Height*factor)),0,0,InterpolationFlags.Area);
                using var boosted=new Mat();
                var input=factor<1?small:raw;
                if(options.EnhanceLocalContrast){using var clahe=Cv2.CreateCLAHE(2,new Size(8,8));clahe.Apply(input,boosted);}
                using var sift=SIFT.Create(options.MaxFeatures,3,options.ContrastThreshold);var descriptors=new Mat();
                sift.DetectAndCompute(options.EnhanceLocalContrast?boosted:input,null,out var keys,descriptors);
                set=new(keys.Select(k=>new Point2f((float)(k.Pt.X/factor),(float)(k.Pt.Y/factor))).ToArray(),descriptors,factor);
                string temp=path+".tmp";
                using(var writer=new BinaryWriter(File.Create(temp)))
                {
                    writer.Write(set.Points.Length);writer.Write(factor);foreach(var point in set.Points){writer.Write(point.X);writer.Write(point.Y);}
                    var values=new float[set.Points.Length*128];if(values.Length>0)Marshal.Copy(descriptors.Data,values,0,values.Length);foreach(float value in values)writer.Write(value);
                }
                File.Move(temp,path,true);
            }
            while(_sets.Count>=capacity){var oldest=_sets.MinBy(x=>x.Value.Used);oldest.Value.Set.Dispose();_sets.Remove(oldest.Key);}
            _sets[tile.File]=(set,++_clock);return set;
        }
        public void Dispose(){foreach(var value in _sets.Values)value.Set.Dispose();}
    }
    public RegistrationReport Run(ScanProject p,string folder,RegistrationOptions options,Action<string,int,int,string> progress,CancellationToken ct)
    {
        if(options.Method=="parallax")return new ParallaxRegistrar().Run(p,folder,options,progress,ct);
        options.Validate();var candidates=new List<(int I,int J,Rect2d Overlap)>();
        for(int i=0;i<p.Tiles.Count;i++)for(int j=i+1;j<p.Tiles.Count;j++)
        {
            var a=p.Tiles[i];var b=p.Tiles[j];double x=Math.Max(a.X,b.X),y=Math.Max(a.Y,b.Y),w=Math.Min(a.X+a.Width,b.X+b.Width)-x,h=Math.Min(a.Y+a.Height,b.Y+b.Height)-y;
            if(w>=64&&h>=64&&w*h>=Math.Min((double)a.Width*a.Height,(double)b.Width*b.Height)*.04)candidates.Add((i,j,new(x,y,w,h)));
        }
        if(candidates.Count==0)throw new InvalidOperationException("The stage layout has no overlapping neighbors. Check frame width and axis mapping.");
        double pixelsPerMm=p.Tiles[0].Width/p.Placement!.FovWidth,margin=options.MaxShiftMm*pixelsPerMm;
        var reports=new List<PairReport>();var links=new List<FeatureLink>();
        string version=Guid.NewGuid().ToString("N")[..8],debugFolder="registration-"+version+"-debug";
        Directory.CreateDirectory(Path.Combine(folder,debugFolder));
        using var correction=LightCorrection.For(p);
        using(var cache=options.Method=="hybrid"?null:new FeatureCache(folder,options,correction:correction))
        using(var hybrid=options.Method=="hybrid"?new HybridMatcher(folder,options,pixelsPerMm,correction):null)
        {
            if(cache!=null)for(int i=0;i<p.Tiles.Count;i++){ct.ThrowIfCancellationRequested();cache.Get(p.Tiles[i]);progress("features",i+1,p.Tiles.Count,options.WorkingSize==0?"Finding full-resolution features · cached for subsequent restitches":"Finding visual features · cached for subsequent restitches");}
            for(int c=0;c<candidates.Count;c++)
            {
                ct.ThrowIfCancellationRequested();var candidate=candidates[c];var a=p.Tiles[candidate.I];var b=p.Tiles[candidate.J];
                var result=hybrid!=null?hybrid.Match(candidate.I,candidate.J,a,b,ct,label=>progress("matching",c,candidates.Count,$"Hybrid pair {candidate.I+1} ↔ {candidate.J+1} · {label}")):
                    Match(candidate.I,candidate.J,a,b,cache!.Get(a),cache.Get(b),candidate.Overlap,margin,options);
                string debugFile=$"{debugFolder}/pair-{candidate.I}-{candidate.J}.json";
                var pairReport=result.Report with {DebugFile=debugFile};
                Files.Save(Path.Combine(folder,debugFile),result.Diagnostics with {Report=pairReport});
                reports.Add(pairReport);links.AddRange(result.Links);
                progress("matching",c+1,candidates.Count,$"{RegistrationOptions.MethodName(options.Method)} overlaps · {reports.Count(r=>r.Accepted)} reliable pairs / {c+1} checked");
            }
        }
        if(links.Count==0)throw new InvalidOperationException("No reliable translation matches found. Original mosaic kept. Verify the frame scale, overlap and unchanged camera magnification/orientation.");
        var diagnosticLinks=links.ToArray();
        progress("optimizing",0,2,"Solving shared stage calibration and individual tile positions");
        var solution=SimilaritySolver.SolveLayout(p.Tiles,links,options,pixelsPerMm,ct,(pass,message)=>progress("optimizing",pass,5,message));
        var resultTiles=solution.Tiles;
        double threshold=Math.Max(8,6/options.ImageFactor(p.Tiles[0].Width,p.Tiles[0].Height));
        var inconsistent=links.GroupBy(l=>(l.First,l.Second)).Where(group=>SimilaritySolver.Median(group.Select(l=>SimilaritySolver.Error(resultTiles[l.First].Transform,resultTiles[l.Second].Transform,l)).ToArray())>threshold).Select(group=>group.Key).ToHashSet();
        if(inconsistent.Count>0)
        {
            links.RemoveAll(l=>inconsistent.Contains((l.First,l.Second)));
            reports=reports.Select(r=>inconsistent.Contains((r.First,r.Second))?r with {Accepted=false,Reason="Disagrees with the global layout"}:r).ToList();
            if(links.Count==0)throw new InvalidOperationException("All image matches disagree with the stage-constrained layout. Original mosaic kept. Check scale or relax correction limits.");
            solution=SimilaritySolver.SolveLayout(p.Tiles,links,options,pixelsPerMm,ct,(pass,message)=>progress("optimizing",pass,5,"Discarded inconsistent pairs · "+message));
            resultTiles=solution.Tiles;
        }
        for(int i=0;i<p.Tiles.Count;i++){p.Tiles[i].Registration=resultTiles[i];p.Tiles[i].X=resultTiles[i].Transform.X;p.Tiles[i].Y=resultTiles[i].Transform.Y;}
        reports=reports.Select(r=>{
            var pairLinks=diagnosticLinks.Where(l=>l.First==r.First&&l.Second==r.Second).ToArray();
            return r with {GlobalErrorPixels=pairLinks.Length==0?null:SimilaritySolver.Median(pairLinks.Select(l=>SimilaritySolver.Error(resultTiles[l.First].Transform,resultTiles[l.Second].Transform,l)).ToArray())};
        }).ToList();
        double median=SimilaritySolver.Median(links.Select(l=>SimilaritySolver.Error(resultTiles[l.First].Transform,resultTiles[l.Second].Transform,l)).ToArray());
        string reportFile="registration-"+version+".json";
        var featurePairs=reports.Where(r=>r.Accepted&&r.Method=="sift").Select(r=>(r.First,r.Second)).ToHashSet();
        var report=new RegistrationReport(options,DateTimeOffset.UtcNow,reports.Count(r=>r.Accepted),reports.Count(r=>!r.Accepted),resultTiles.Count(t=>t.Links>0),resultTiles.Count(t=>t.Status=="limit-reached"),median,reportFile,reports){StageCorrection=solution.StageCorrection,FeaturePairs=links.Count(l=>featurePairs.Contains((l.First,l.Second))),TranslationPairs=links.Count(l=>!featurePairs.Contains((l.First,l.Second)))};
        Files.Save(Path.Combine(folder,reportFile),new {report,tiles=p.Tiles.Select((t,i)=>new{sequence=i+1,t.File,t.StageX,t.StageY,t.BaseX,t.BaseY,t.Registration})});
        progress("optimizing",2,2,$"Registration ready · shared stage correction {solution.StageCorrection.RotationDegrees:F3}° / {solution.StageCorrection.Scale:F5}× · {report.SupportedTiles}/{p.Tiles.Count} tiles supported · median error {median:F2} px");
        return report;
    }
    public PairDiagnostics InspectPair(ScanProject p,string folder,int first,int second,CancellationToken ct=default,RegistrationOptions? trialOptions=null)
    {
        if(first<0||second<=first||second>=p.Tiles.Count)throw new ArgumentException("Select a valid pair of different tiles.");
        var report=p.Registration?.Pairs.SingleOrDefault(r=>r.First==first&&r.Second==second)??throw new ArgumentException("This pair was not checked by the saved refinement.");
        Tile Baseline(Tile t)=>new(){File=t.File,Width=t.Width,Height=t.Height,X=t.BaseX??t.NominalX,Y=t.BaseY??t.NominalY};
        var a=Baseline(p.Tiles[first]);var b=Baseline(p.Tiles[second]);
        double x=Math.Max(a.X,b.X),y=Math.Max(a.Y,b.Y),w=Math.Min(a.X+a.Width,b.X+b.Width)-x,h=Math.Min(a.Y+a.Height,b.Y+b.Height)-y;
        var options=trialOptions??p.Registration!.Options;options.Validate();
        if(options.Method=="parallax")return new ParallaxRegistrar().InspectPair(p,folder,first,second,ct,trialOptions);
        using var correction=LightCorrection.For(p);
        if(options.Method=="hybrid")
        {
            using var hybrid=new HybridMatcher(folder,options,a.Width/(p.Placement?.FovWidth??p.Settings.FovWidth),correction);
            var matched=hybrid.Match(first,second,a,b,ct);
            return matched.Diagnostics with {Report=report,Reconstructed=true,ReconstructionReport=matched.Report,Trial=trialOptions!=null};
        }
        using var cache=new FeatureCache(folder,options,correction:correction);
        ct.ThrowIfCancellationRequested();var fa=cache.Get(a);var fb=cache.Get(b);ct.ThrowIfCancellationRequested();
        var result=Match(first,second,a,b,fa,fb,new(x,y,w,h),options.MaxShiftMm*a.Width/(p.Placement?.FovWidth??p.Settings.FovWidth),options);
        ct.ThrowIfCancellationRequested();
        return result.Diagnostics with {Report=report,Reconstructed=true,ReconstructionReport=result.Report,Trial=trialOptions!=null};
    }
    private static (PairReport Report,List<FeatureLink> Links,PairDiagnostics Diagnostics) Match(int i,int j,Tile a,Tile b,Features fa,Features fb,Rect2d overlap,double margin,RegistrationOptions options)
    {
        double sharedAllowance=double.Hypot(b.X+b.Width/2d-a.X-a.Width/2d,b.Y+b.Height/2d-a.Y-a.Height/2d)*(options.MaxScalePercent/100+options.MaxRotationDegrees*Math.PI/180);
        double searchMargin=margin+sharedAllowance;
        bool Inside(Point2f p,Tile t)=>p.X+t.X>=overlap.X-searchMargin&&p.X+t.X<=overlap.Right+searchMargin&&p.Y+t.Y>=overlap.Y-searchMargin&&p.Y+t.Y<=overlap.Bottom+searchMargin;
        int[] ia=Enumerable.Range(0,fa.Points.Length).Where(k=>Inside(fa.Points[k],a)).ToArray(),ib=Enumerable.Range(0,fb.Points.Length).Where(k=>Inside(fb.Points[k],b)).ToArray();
        var observations=new List<DebugFeatureMatch>();
        PairDiagnostics Diagnostics(PairReport report)=>new(i,j,report,new(overlap.X,overlap.Y,overlap.Width,overlap.Height,searchMargin),fa.Points.Length,fb.Points.Length,ia.Select(k=>new XY(fa.Points[k].X,fa.Points[k].Y)).ToArray(),ib.Select(k=>new XY(fb.Points[k].X,fb.Points[k].Y)).ToArray(),observations){AnalysisOptions=options};
        (PairReport,List<FeatureLink>,PairDiagnostics) Reject(string reason,int matches=0,int inliers=0,double error=0,double angle=0,double scale=1){var report=new PairReport(i,j,matches,inliers,error,angle,scale,false,reason);return(report,[],Diagnostics(report));}
        if(ia.Length<12||ib.Length<12)return Reject("Too few features in predicted overlap");
        using var da=Select(fa.Descriptors,ia);using var db=Select(fb.Descriptors,ib);using var matcher=new BFMatcher(NormTypes.L2);
        var forward=matcher.KnnMatch(da,db,2);var backward=matcher.KnnMatch(db,da,2);
        var reverse=backward.Where(m=>m.Length==2&&m[0].Distance<.75*m[1].Distance).ToDictionary(m=>m[0].QueryIdx,m=>m[0].TrainIdx);
        var points=new List<(Point2d P,Point2d Q,int DebugIndex)>();
        double gate=2*margin+sharedAllowance;
        foreach(var matches in forward)
        {
            if(matches.Length<2||matches[0].Distance>=.75*matches[1].Distance)continue;var m=matches[0];
            if(!reverse.TryGetValue(m.TrainIdx,out int back)||back!=m.QueryIdx)continue;
            var pa=fa.Points[ia[m.QueryIdx]];var pb=fb.Points[ib[m.TrainIdx]];
            int index=observations.Count;
            bool gated=double.Hypot(pa.X+a.X-pb.X-b.X,pa.Y+a.Y-pb.Y-b.Y)>gate;
            observations.Add(new(new(pa.X,pa.Y),new(pb.X,pb.Y),gated?"stage-gate":"candidate"));
            if(gated)continue;
            points.Add((new(pa.X,pa.Y),new(pb.X,pb.Y),index));
        }
        if(points.Count<12)return Reject("Too few unambiguous feature matches",points.Count);
        double threshold=3/Math.Min(fa.Factor,fb.Factor);
        using var src=InputArray.Create(points.Select(p=>p.P).ToArray());using var dst=InputArray.Create(points.Select(p=>p.Q).ToArray());using var mask=new Mat();
        using var matrix=Cv2.EstimateAffinePartial2D(src,dst,mask,RobustEstimationAlgorithms.RANSAC,threshold,3000,.995,15);
        if(matrix==null||matrix.Empty())return Reject("Could not fit translation/rotation/scale",points.Count);
        var fit=new Similarity(matrix.At<double>(0,0),matrix.At<double>(1,0),matrix.At<double>(0,2),matrix.At<double>(1,2));
        for(int k=0;k<points.Count;k++)observations[points[k].DebugIndex]=observations[points[k].DebugIndex] with {Status=mask.At<byte>(k)!=0?"ransac-inlier":"ransac-outlier"};
        var inliers=points.Where((point,k)=>mask.At<byte>(k)!=0).ToArray();
        double error=SimilaritySolver.Median(inliers.Select(point=>{var q=fit.Apply(point.P.X,point.P.Y);return double.Hypot(q.X-point.Q.X,q.Y-point.Q.Y);}).ToArray());
        if(inliers.Length<12||inliers.Length<points.Count*.3)return Reject("Insufficient RANSAC agreement",points.Count,inliers.Length,error,fit.Degrees,fit.Scale);
        // The affine fit only finds outliers and detects incompatible captures.
        // Fixed-camera captures must agree with a single relative translation.
        if(Math.Abs(fit.Degrees)>.2||Math.Abs(fit.Scale-1)>.003)return Reject("Camera orientation or magnification differs between captures",points.Count,inliers.Length,error,fit.Degrees,fit.Scale);
        double tx=SimilaritySolver.Median(inliers.Select(p=>p.Q.X-p.P.X).ToArray()),ty=SimilaritySolver.Median(inliers.Select(p=>p.Q.Y-p.P.Y).ToArray());
        foreach(var point in inliers)observations[point.DebugIndex]=observations[point.DebugIndex] with {Status=double.Hypot(point.Q.X-point.P.X-tx,point.Q.Y-point.P.Y-ty)<=threshold?"translation-inlier":"translation-outlier"};
        inliers=inliers.Where(p=>double.Hypot(p.Q.X-p.P.X-tx,p.Q.Y-p.P.Y-ty)<=threshold).ToArray();
        if(inliers.Length<12||inliers.Length<points.Count*.3)return Reject("Insufficient agreement with fixed scale and orientation",points.Count,inliers.Length,error,fit.Degrees,fit.Scale);
        error=SimilaritySolver.Median(inliers.Select(p=>double.Hypot(p.Q.X-p.P.X-tx,p.Q.Y-p.P.Y-ty)).ToArray());
        if(double.Hypot(tx+b.X-a.X,ty+b.Y-a.Y)>gate)return Reject("Pair displacement exceeds stage tolerance",points.Count,inliers.Length,error,fit.Degrees,fit.Scale);
        double sx=inliers.Max(m=>m.P.X)-inliers.Min(m=>m.P.X),sy=inliers.Max(m=>m.P.Y)-inliers.Min(m=>m.P.Y);
        if(sx<Math.Min(overlap.Width,a.Width)*.2||sy<Math.Min(overlap.Height,a.Height)*.2)return Reject("Matches are concentrated in a small area",points.Count,inliers.Length,error,fit.Degrees,fit.Scale);
        // Keep every reliable match in the global equations. Balance textured
        // regions by weight instead of truncating their feature observations.
        double weight=1/Math.Max(1,error*error);
        foreach(var point in inliers)observations[point.DebugIndex]=observations[point.DebugIndex] with {Status="retained"};
        var retained=inliers.GroupBy(p=>((int)(p.P.X/a.Width*6),(int)(p.P.Y/a.Height*6)))
            .SelectMany(group=>group.Select(p=>new FeatureLink(i,j,p.P,p.Q,weight*Math.Min(1,8d/group.Count())))).ToList();
        var accepted=new PairReport(i,j,points.Count,inliers.Length,error,fit.Degrees,fit.Scale,true,"Accepted");
        return(accepted,retained,Diagnostics(accepted));
    }
    private static unsafe Mat Select(Mat descriptors,int[] rows)
    {
        var result=new Mat(rows.Length,descriptors.Cols,descriptors.Type());
        long bytes=descriptors.Cols*descriptors.ElemSize();
        for(int i=0;i<rows.Length;i++)Buffer.MemoryCopy(descriptors.Ptr(rows[i]).ToPointer(),result.Ptr(i).ToPointer(),bytes,bytes);
        return result;
    }
}
