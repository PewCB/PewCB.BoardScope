using OpenCvSharp;

namespace BoardScope;

public sealed partial class SimilarityRegistrar
{
    // Pixel methods are cheap and often recover weak PCB texture. Feature sets
    // are loaded lazily only for pairs that fail both pixel methods/preprocessors.
    internal sealed class HybridMatcher(string folder,RegistrationOptions options,double pixelsPerMm,LightCorrection? correction=null):IDisposable
    {
        private readonly TranslationRegistrar.ImageCache _images=new(folder,correction);
        private readonly Dictionary<string,FeatureCache> _features=[];
        private static IEnumerable<(string Label,RegistrationOptions Options)> Attempts(RegistrationOptions o)
        {
            yield return("Phase · configured illumination",o with{Method="phase"});
            yield return("NCC · configured illumination",o with{Method="ncc"});
            yield return("Phase · alternate illumination",o with{Method="phase",PhaseHighPass=!o.PhaseHighPass});
            yield return("NCC · alternate illumination",o with{Method="ncc",NccHighPass=!o.NccHighPass});
            yield return("SIFT · configured sensitivity",o with{Method="sift"});
            yield return("SIFT · sensitive",o with{Method="sift",ContrastThreshold=Math.Min(o.ContrastThreshold,.01),MaxFeatures=Math.Max(o.MaxFeatures,10000)});
            yield return("SIFT · faint detail, local contrast, full resolution",o with{Method="sift",ContrastThreshold=Math.Min(o.ContrastThreshold,.003),MaxFeatures=Math.Max(o.MaxFeatures,12000),EnhanceLocalContrast=true,WorkingSize=0});
        }
        public (PairReport Report,List<FeatureLink> Links,PairDiagnostics Diagnostics) Match(int i,int j,Tile a,Tile b,CancellationToken ct,Action<string>? progress=null,Action<string,RegistrationOptions,PairDiagnostics,List<FeatureLink>>? collect=null)
        {
            var attempts=new List<PairAttempt>();var seen=new HashSet<string>();
            (PairReport Report,List<FeatureLink> Links,PairDiagnostics Diagnostics) result=default;
            foreach(var (label,settings) in Attempts(options))
            {
                string detector=$"{settings.FeatureKey()}-{settings.WorkingSize}-{settings.MaxFeatures}";
                string key=settings.Method switch{"sift"=>"sift-"+detector,"phase"=>"phase-"+settings.PhaseHighPass,_=>"ncc-"+settings.NccHighPass};
                if(!seen.Add(key))continue;
                ct.ThrowIfCancellationRequested();progress?.Invoke(label);
                if(settings.Method=="sift")
                {
                    if(!_features.TryGetValue(detector,out var cache))_features[detector]=cache=new FeatureCache(folder,settings,4,correction);
                    var fa=cache.Get(a);ct.ThrowIfCancellationRequested();var fb=cache.Get(b);ct.ThrowIfCancellationRequested();
                    double x=Math.Max(a.X,b.X),y=Math.Max(a.Y,b.Y);
                    var overlap=new Rect2d(x,y,Math.Min(a.X+a.Width,b.X+b.Width)-x,Math.Min(a.Y+a.Height,b.Y+b.Height)-y);
                    result=SimilarityRegistrar.Match(i,j,a,b,fa,fb,overlap,settings.MaxShiftMm*pixelsPerMm,settings);
                }
                else
                {
                    var matched=settings.Method=="phase"?PhaseRegistrar.Match(i,j,a,b,_images.Get(a),_images.Get(b),settings,pixelsPerMm,ct):
                        NccRegistrar.Match(i,j,a,b,_images.Get(a),_images.Get(b),settings,pixelsPerMm,ct);
                    result=(matched.Diagnostics.Report,matched.Link==null?[]:[matched.Link],matched.Diagnostics);
                }
                ct.ThrowIfCancellationRequested();attempts.Add(new(label,settings,result.Report));
                if(result.Report.Accepted){collect?.Invoke(label,settings,result.Diagnostics,result.Links);if(collect==null)break;}
            }
            var (lastReport,links,diagnostics)=result;
            if(lastReport==null||links==null||diagnostics==null)throw new InvalidOperationException("No hybrid matching attempts were configured.");
            var report=lastReport with{AttemptLabel=attempts[^1].Label,Attempts=attempts};
            if(!report.Accepted)report=report with{Reason=$"No hybrid attempt accepted this pair ({attempts.Count} tried). Last: {report.Reason}"};
            return(report,links,diagnostics with{Report=report});
        }
        public void Dispose(){_images.Dispose();foreach(var cache in _features.Values)cache.Dispose();}
    }
}
