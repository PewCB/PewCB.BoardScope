using OpenCvSharp;

namespace BoardScope;

// Multiple depth surfaces may suggest different translations. Retain coherent
// local hypotheses, then select with evidence from OTHER pairs. Tile pixels
// retain their captured geometry; no homography or local warp is applied.
public sealed class ParallaxRegistrar
{
    private sealed record Hypothesis(string Label,FeatureLink Link,int[] Samples,int Patches,double Confidence,double Spread);
    private sealed class Pair(int first,int second,DebugOverlap overlap)
    {
        public int First=first,Second=second,PatchCount,Selected=-1,Retries,Changes;
        public bool Expanded,Accepted;
        public DebugOverlap Overlap=overlap;
        public List<PatchObservation> Samples=[];
        public List<Hypothesis> Hypotheses=[];
        public List<PairAttempt> Attempts=[];
    }
    private sealed class Matcher(string folder,RegistrationOptions options,double ppm,LightCorrection? correction=null):IDisposable
    {
        private readonly TranslationRegistrar.ImageCache _images=new(folder,correction);
        private readonly SimilarityRegistrar.HybridMatcher _hybrid=new(folder,options,ppm,correction);
        public Pair Match(int i,int j,Tile a,Tile b,CancellationToken ct)
        {
            double x=Math.Max(a.X,b.X),y=Math.Max(a.Y,b.Y),w=Math.Min(a.X+a.Width,b.X+b.Width)-x,h=Math.Min(a.Y+a.Height,b.Y+b.Height)-y;
            double gate=2*options.MaxShiftMm*ppm+double.Hypot(b.X+b.Width/2d-a.X-a.Width/2d,b.Y+b.Height/2d-a.Y-a.Height/2d)*(options.MaxScalePercent/100+options.MaxRotationDegrees*Math.PI/180);
            var pair=new Pair(i,j,new(x,y,w,h,gate));
            if(w<64||h<64)return pair;
            var imageA=_images.Get(a);var imageB=_images.Get(b);
            int nx=w>=192?3:1,ny=h>=192?3:1;
            int pw=Math.Min(768,(int)Math.Floor(w-4)/(nx==1?1:2)),ph=Math.Min(768,(int)Math.Floor(h-4)/(ny==1?1:2));
            double baseFactor=options.ImageFactor(a.Width,a.Height);
            for(int row=0;row<ny;row++)for(int col=0;col<nx;col++)
            {
                ct.ThrowIfCancellationRequested();int id=pair.PatchCount++;
                double cx=x+pw/2d+1+(nx==1?0:col*(w-pw-2)/(nx-1)),cy=y+ph/2d+1+(ny==1?0:row*(h-ph-2)/(ny-1));
                using var rawA=new Mat();using var rawB=new Mat();
                Cv2.GetRectSubPix(imageA,new(pw,ph),new((float)(cx-a.X),(float)(cy-a.Y)),rawA);
                Cv2.GetRectSubPix(imageB,new(pw,ph),new((float)(cx-b.X),(float)(cy-b.Y)),rawB);
                foreach(double requested in new[]{baseFactor,baseFactor*.5,baseFactor*.25})
                {
                    int rw=2*((int)Math.Round(pw*requested)/2),rh=2*((int)Math.Round(ph*requested)/2);
                    if(rw<48||rh<48)continue;
                    using var pa=new Mat();using var pb=new Mat();
                    Cv2.Resize(rawA,pa,new(rw,rh),0,0,InterpolationFlags.Area);Cv2.Resize(rawB,pb,new(rw,rh),0,0,InterpolationFlags.Area);
                    double fx=rw/(double)pw,fy=rh/(double)ph;
                    // Pixel matchers operate in the patch's pixel units. Use the
                    // full-image gate, including shared calibration allowance.
                    var local=options with{WorkingSize=0,MaxShiftMm=Math.Clamp(gate/(2*ppm),.005,5)};
                    var ta=new Tile{Width=rw,Height=rh};var tb=new Tile{Width=rw,Height=rh};
                    foreach(string method in new[]{"phase","ncc"})
                    {
                        ct.ThrowIfCancellationRequested();var settings=local with{Method=method};
                        var result=method=="phase"?PhaseRegistrar.Match(i,j,ta,tb,pa,pb,settings,ppm*Math.Min(fx,fy),ct):NccRegistrar.Match(i,j,ta,tb,pa,pb,settings,ppm*Math.Min(fx,fy),ct);
                        if(result.Link is not {} link)continue;
                        double sx=(link.Q.X-link.P.X)/fx,sy=(link.Q.Y-link.P.Y)/fy;
                        if(double.Hypot(sx,sy)>gate)continue;
                        double confidence=method=="phase"?Math.Clamp(result.Diagnostics.Report.Phase!.Response,0,1)*Math.Max(0,result.Diagnostics.Report.Phase.Correlation??0):Math.Max(0,result.Diagnostics.Report.Ncc!.Correlation);
                        pair.Samples.Add(new(id,Math.Min(fx,fy),method,new(cx-a.X,cy-a.Y),new(cx-b.X+sx,cy-b.Y+sy),confidence));
                    }
                }
            }
            Cluster(pair,a,b,options);
            if(pair.Hypotheses.Count==0)Expand(pair,a,b,ct);
            pair.Selected=pair.Hypotheses.Count>0?0:-1;return pair;
        }
        public void Expand(Pair pair,Tile a,Tile b,CancellationToken ct)
        {
            if(pair.Expanded)return;pair.Expanded=true;
            var result=_hybrid.Match(pair.First,pair.Second,a,b,ct,collect:(label,settings,diagnostics,links)=>
            {
                double dx=SimilaritySolver.Median(links.Select(l=>l.P.X-l.Q.X).ToArray()),dy=SimilaritySolver.Median(links.Select(l=>l.P.Y-l.Q.Y).ToArray());
                // Avoid a textured SIFT pair outweighing an entire patch graph.
                double quality=diagnostics.Report.Method switch{"phase"=>Math.Clamp(diagnostics.Report.Phase!.Response,0,1)*Math.Max(0,diagnostics.Report.Phase.Correlation??0),"ncc"=>diagnostics.Report.Ncc!.Correlation,_=>.7/(1+diagnostics.Report.ErrorPixels)};
                var p=new Point2d(pair.Overlap.X+pair.Overlap.Width/2-a.X,pair.Overlap.Y+pair.Overlap.Height/2-a.Y);
                if(pair.Hypotheses.Any(h=>double.Hypot(h.Link.P.X-h.Link.Q.X-dx,h.Link.P.Y-h.Link.Q.Y-dy)<1.5))return;
                pair.Hypotheses.Add(new("Whole overlap · "+label,new(pair.First,pair.Second,p,new(p.X-dx,p.Y-dy),16*Math.Clamp(quality,.05,1)),[],0,Math.Clamp(quality*.8,0,1),diagnostics.Report.ErrorPixels));
            });
            pair.Attempts.AddRange(result.Report.Attempts??[]);
            pair.Hypotheses=pair.Hypotheses.OrderByDescending(h=>h.Confidence).Take(10).ToList();
        }
        public void Dispose(){_images.Dispose();_hybrid.Dispose();}
    }
    private static void Cluster(Pair pair,Tile a,Tile b,RegistrationOptions options)
    {
        var remaining=Enumerable.Range(0,pair.Samples.Count).ToList();double radius=3/options.ImageFactor(a.Width,a.Height);
        XY Delta(int k){var s=pair.Samples[k];return new(s.First.X-s.Second.X,s.First.Y-s.Second.Y);}
        while(remaining.Count>0)
        {
            // Independent patches vote once, even if several pyramid levels or
            // methods agree. Seed neighborhoods prevent transitive mode merging.
            var group=remaining.Select(seed=>remaining.Where(k=>double.Hypot(Delta(k).X-Delta(seed).X,Delta(k).Y-Delta(seed).Y)<=radius).GroupBy(k=>pair.Samples[k].Patch).Select(g=>g.MaxBy(k=>pair.Samples[k].Confidence)).ToArray())
                .OrderByDescending(g=>g.Length).ThenByDescending(g=>g.Sum(k=>pair.Samples[k].Confidence)).First();
            double dx=SimilaritySolver.Median(group.Select(k=>Delta(k).X).ToArray()),dy=SimilaritySolver.Median(group.Select(k=>Delta(k).Y).ToArray());
            var all=remaining.Where(k=>double.Hypot(Delta(k).X-dx,Delta(k).Y-dy)<=radius).ToArray();
            remaining.RemoveAll(k=>all.Contains(k)||group.Contains(k));
            if(group.Length<2)continue;
            double spread=SimilaritySolver.Median(group.Select(k=>double.Hypot(Delta(k).X-dx,Delta(k).Y-dy)).ToArray());
            double quality=group.Average(k=>pair.Samples[k].Confidence)*Math.Min(1,group.Length/4d)/(1+spread/2);
            var center=new Point2d(pair.Overlap.X+pair.Overlap.Width/2-a.X,pair.Overlap.Y+pair.Overlap.Height/2-a.Y);
            pair.Hypotheses.Add(new($"Local patches · {group.Length} agreeing regions",new(pair.First,pair.Second,center,new(center.X-dx,center.Y-dy),32*Math.Clamp(quality,.05,1)),all,group.Length,quality,spread));
        }
        pair.Hypotheses=pair.Hypotheses.OrderByDescending(h=>h.Confidence).Take(6).ToList();
    }
    private static bool ConnectedWithout(Pair omit,IReadOnlyList<Pair> pairs,int count)
    {
        var seen=new bool[count];var queue=new Queue<int>();queue.Enqueue(omit.First);seen[omit.First]=true;
        while(queue.TryDequeue(out int current))foreach(var p in pairs)
        {
            if(p==omit||p.Selected<0)continue;
            int next=p.First==current?p.Second:p.Second==current?p.First:-1;
            if(next<0||seen[next])continue;if(next==omit.Second)return true;seen[next]=true;queue.Enqueue(next);
        }
        return false;
    }
    private static List<FeatureLink> Links(IEnumerable<Pair> pairs)=>pairs.Where(p=>p.Selected>=0).Select(p=>p.Hypotheses[p.Selected].Link).ToList();
    private static double Error(Hypothesis h,RegistrationSolution solution)=>SimilaritySolver.Error(solution.Tiles[h.Link.First].Transform,solution.Tiles[h.Link.Second].Transform,h.Link);
    private static PairDiagnostics Diagnostics(Pair pair,RegistrationOptions options,RegistrationSolution? solution=null)
    {
        var chosen=pair.Selected>=0?pair.Hypotheses[pair.Selected]:null;
        var report=new PairReport(pair.First,pair.Second,pair.Samples.Select(s=>s.Patch).Distinct().Count(),chosen?.Patches??0,chosen?.Spread??0,0,1,pair.Accepted,
            chosen==null?"No coherent patch or whole-overlap hypothesis":pair.Accepted?"Accepted parallax-aware translation":"Disagrees with the global layout")
        {Method="parallax",AttemptLabel=chosen?.Label,Attempts=pair.Attempts,GlobalErrorPixels=chosen!=null&&solution!=null?Error(chosen,solution):null,
            Parallax=new(pair.PatchCount,pair.Samples.Select(s=>s.Patch).Distinct().Count(),pair.Selected>=0?pair.Selected:null,pair.Retries,pair.Hypotheses.Select((h,k)=>new OffsetHypothesis(k,h.Label,h.Link.P.X-h.Link.Q.X,h.Link.P.Y-h.Link.Q.Y,h.Patches,h.Confidence,h.Spread,solution==null?null:Error(h,solution))).ToList()){GlobalChanges=pair.Changes}};
        var selected=chosen?.Samples.ToHashSet()??[];
        var observations=pair.Samples.Select((s,k)=>new DebugFeatureMatch(s.First,s.Second,selected.Contains(k)?"retained":"other-offset")).ToList();
        if(chosen!=null&&chosen.Samples.Length==0)observations.Add(new(new(chosen.Link.P.X,chosen.Link.P.Y),new(chosen.Link.Q.X,chosen.Link.Q.Y),"retained"));
        return new(pair.First,pair.Second,report,pair.Overlap,0,0,[],[],observations){AnalysisOptions=options,PatchObservations=pair.Samples};
    }
    public RegistrationReport Run(ScanProject p,string folder,RegistrationOptions options,Action<string,int,int,string> progress,CancellationToken ct)
    {
        options.Validate();double ppm=p.Tiles[0].Width/p.Placement!.FovWidth;
        var candidates=new List<(int I,int J)>();
        for(int i=0;i<p.Tiles.Count;i++)for(int j=i+1;j<p.Tiles.Count;j++)
        {
            var a=p.Tiles[i];var b=p.Tiles[j];double w=Math.Min(a.X+a.Width,b.X+b.Width)-Math.Max(a.X,b.X),h=Math.Min(a.Y+a.Height,b.Y+b.Height)-Math.Max(a.Y,b.Y);
            if(w>=64&&h>=64&&w*h>=Math.Min((double)a.Width*a.Height,(double)b.Width*b.Height)*.04)candidates.Add((i,j));
        }
        if(candidates.Count==0)throw new InvalidOperationException("The stage layout has no overlapping neighbors. Check frame width and axis mapping.");
        using var correction=LightCorrection.For(p);
        var pairs=new List<Pair>();using var matcher=new Matcher(folder,options,ppm,correction);
        foreach(var (i,j) in candidates)
        {
            ct.ThrowIfCancellationRequested();pairs.Add(matcher.Match(i,j,p.Tiles[i],p.Tiles[j],ct));
            progress("matching",pairs.Count,candidates.Count,$"Parallax-aware local patches · {pairs.Count(x=>x.Selected>=0)} matched / {pairs.Count} checked");
        }
        if(Links(pairs).Count==0)throw new InvalidOperationException("No coherent parallax-aware matches found. Original mosaic kept.");
        double threshold=Math.Max(8,6/options.ImageFactor(p.Tiles[0].Width,p.Tiles[0].Height));
        RegistrationSolution Solve(IEnumerable<Pair> set)=>SimilaritySolver.SolveLayout(p.Tiles,Links(set),options,ppm,ct);
        var solution=Solve(pairs);
        for(int pass=0;pass<2;pass++)
        {
            int changes=0,done=0;
            foreach(var pair in pairs.OrderByDescending(x=>x.Hypotheses.Count>1).ThenBy(x=>x.First).ThenBy(x=>x.Second))
            {
                ct.ThrowIfCancellationRequested();progress("optimizing",++done,pairs.Count,$"Comparing offsets against other neighbors · pass {pass+1}/2");
                if(pair.Selected<0||!ConnectedWithout(pair,pairs,p.Tiles.Count))continue;
                if(pair.Hypotheses.Count==1&&Error(pair.Hypotheses[0],solution)<=2.5)continue;
                var independent=Solve(pairs.Where(x=>x!=pair));pair.Retries++;
                var old=pair.Hypotheses[pair.Selected];
                if(Error(old,independent)>2.5)
                {
                    matcher.Expand(pair,p.Tiles[pair.First],p.Tiles[pair.Second],ct);
                    pair.Selected=pair.Hypotheses.IndexOf(old);
                    if(pair.Selected<0){pair.Hypotheses.Add(old);pair.Selected=pair.Hypotheses.Count-1;}
                }
                double Score(Hypothesis h)=>Math.Pow(Error(h,independent),2)+2*(1-h.Confidence);
                int best=Enumerable.Range(0,pair.Hypotheses.Count).MinBy(k=>Score(pair.Hypotheses[k]));
                if(best!=pair.Selected&&Error(pair.Hypotheses[best],independent)<=threshold&&Score(pair.Hypotheses[best])+.25<Score(old)){pair.Selected=best;pair.Changes++;changes++;}
            }
            solution=Solve(pairs);if(changes==0)break;
        }
        // Revalidate after every removal: no pair is labelled accepted using a
        // layout that changed after it was checked.
        var rejected=new HashSet<Pair>();
        while(true)
        {
            ct.ThrowIfCancellationRequested();var bad=pairs.Where(x=>x.Selected>=0&&!rejected.Contains(x)&&Error(x.Hypotheses[x.Selected],solution)>threshold).ToArray();
            if(bad.Length==0)break;foreach(var pair in bad)rejected.Add(pair);
            var retained=pairs.Where(x=>!rejected.Contains(x)).ToList();
            if(Links(retained).Count==0)throw new InvalidOperationException("All parallax hypotheses disagree with the stage-constrained layout. Original mosaic kept.");
            solution=Solve(retained);
        }
        string version=Guid.NewGuid().ToString("N")[..8],debugFolder="registration-"+version+"-debug";Directory.CreateDirectory(Path.Combine(folder,debugFolder));
        var reports=new List<PairReport>();
        foreach(var pair in pairs)
        {
            ct.ThrowIfCancellationRequested();pair.Accepted=pair.Selected>=0&&!rejected.Contains(pair);var d=Diagnostics(pair,options,solution);
            string debugFile=$"{debugFolder}/pair-{pair.First}-{pair.Second}.json";var report=d.Report with{DebugFile=debugFile};reports.Add(report);Files.Save(Path.Combine(folder,debugFile),d with{Report=report});
        }
        for(int i=0;i<p.Tiles.Count;i++){p.Tiles[i].Registration=solution.Tiles[i];p.Tiles[i].X=solution.Tiles[i].Transform.X;p.Tiles[i].Y=solution.Tiles[i].Transform.Y;}
        var links=Links(pairs.Where(x=>x.Accepted));double median=SimilaritySolver.Median(links.Select(l=>SimilaritySolver.Error(solution.Tiles[l.First].Transform,solution.Tiles[l.Second].Transform,l)).ToArray());
        string reportFile="registration-"+version+".json";
        var registration=new RegistrationReport(options,DateTimeOffset.UtcNow,reports.Count(r=>r.Accepted),reports.Count(r=>!r.Accepted),solution.Tiles.Count(t=>t.Links>0),solution.Tiles.Count(t=>t.Status=="limit-reached"),median,reportFile,reports){StageCorrection=solution.StageCorrection,FeaturePairs=0,TranslationPairs=links.Count};
        Files.Save(Path.Combine(folder,reportFile),new{report=registration,tiles=p.Tiles.Select((t,i)=>new{sequence=i+1,t.File,t.StageX,t.StageY,t.BaseX,t.BaseY,t.Registration})});
        progress("optimizing",1,1,$"Parallax-aware alignment ready · {registration.SupportedTiles}/{p.Tiles.Count} supported · median error {median:F2} px");return registration;
    }
    public PairDiagnostics InspectPair(ScanProject p,string folder,int first,int second,CancellationToken ct=default,RegistrationOptions? trialOptions=null)
    {
        if(first<0||second<=first||second>=p.Tiles.Count)throw new ArgumentException("Select a valid pair of different tiles.");
        var saved=p.Registration?.Pairs.SingleOrDefault(r=>r.First==first&&r.Second==second)??throw new ArgumentException("This pair was not checked by the saved refinement.");
        var options=trialOptions??p.Registration!.Options;options.Validate();
        Tile Baseline(Tile t)=>new(){File=t.File,Width=t.Width,Height=t.Height,X=t.BaseX??t.NominalX,Y=t.BaseY??t.NominalY};
        using var correction=LightCorrection.For(p);
        var a=Baseline(p.Tiles[first]);var b=Baseline(p.Tiles[second]);using var matcher=new Matcher(folder,options,a.Width/(p.Placement?.FovWidth??p.Settings.FovWidth),correction);
        var pair=matcher.Match(first,second,a,b,ct);pair.Accepted=pair.Selected>=0;
        bool restored=false;
        if(trialOptions==null&&saved.Parallax?.SelectedHypothesis is int selected)
        {
            var target=saved.Parallax.Hypotheses.SingleOrDefault(h=>h.Id==selected);
            if(target!=null)
            {
                int Find()=>pair.Hypotheses.FindIndex(h=>double.Hypot(h.Link.P.X-h.Link.Q.X-target.DeltaX,h.Link.P.Y-h.Link.Q.Y-target.DeltaY)<1.5);
                int found=Find();if(found<0){matcher.Expand(pair,a,b,ct);found=Find();}
                pair.Selected=found;pair.Accepted=found>=0&&saved.Accepted;restored=found>=0;
            }
        }
        var d=Diagnostics(pair,options);return d with{Report=saved,Reconstructed=true,Trial=trialOptions!=null,ReconstructionReport=d.Report with{Reason=restored?"Reconstructed the saved selected offset; global selection was not rerun":d.Report.Reason+" · local preview; global neighbor selection requires a restitch"}};
    }
}
