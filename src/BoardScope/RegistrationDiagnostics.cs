using System.Text.Json;

namespace BoardScope;

public static class RegistrationDiagnostics
{
    public static PairDiagnostics Recheck(string id,int first,int second,PairRecheckRequest request,CancellationToken ct=default)
    {
        ArgumentNullException.ThrowIfNull(request.Options);
        request.Options.Validate();
        var project=Files.Load(id);
        if(request.StitchId!=null)StitchCatalog.View(project,request.StitchId);
        UseAlignmentLighting(project);
        if(project.Registration?.ReportFile!=request.Version)throw new InvalidOperationException("This scan has a newer refinement. Reopen it to inspect its current matches.");
        if(first>second)(first,second)=(second,first);
        return request.Options.Method is "phase" or "ncc"?new TranslationRegistrar().InspectPair(project,Files.ProjectPath(id),first,second,ct,request.Options):new SimilarityRegistrar().InspectPair(project,Files.ProjectPath(id),first,second,ct,request.Options);
    }
    public static PairDiagnostics Load(string id,int first,int second,string? version,CancellationToken ct=default,string? stitchId=null)
    {
        var project=Files.Load(id);
        if(stitchId!=null)StitchCatalog.View(project,stitchId);
        UseAlignmentLighting(project);
        var registration=project.Registration??throw new InvalidOperationException("Refine this scan before inspecting image matches.");
        if(version!=null&&registration.ReportFile!=version)throw new InvalidOperationException("This scan has a newer refinement. Reopen it to inspect its current matches.");
        if(first>second)(first,second)=(second,first);
        if(first<0||second<=first||second>=project.Tiles.Count)throw new ArgumentException("Select a valid pair of different tiles.");
        var report=registration.Pairs.SingleOrDefault(r=>r.First==first&&r.Second==second)??throw new ArgumentException("This pair was not checked by the saved refinement.");
        string folder=Path.GetFullPath(Files.ProjectPath(id));
        if(report.DebugFile!=null)
        {
            string path=Path.GetFullPath(Path.Combine(folder,report.DebugFile));
            if(!path.StartsWith(folder+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new ArgumentException("Invalid pair diagnostics path.");
            if(File.Exists(path))return Read(path,first,second) with {Report=report};
        }
        // Old scans get lazily reconstructed diagnostics, without changing their
        // saved positions, report, source images, or mosaic. Keep these separate.
        string cacheFolder=Path.Combine(folder,"match-debug-cache");
        if(!registration.ReportFile.EndsWith(".json",StringComparison.Ordinal)||Path.GetFileName(registration.ReportFile)!=registration.ReportFile)throw new ArgumentException("Invalid registration report name.");
        string key=Path.GetFileNameWithoutExtension(registration.ReportFile);
        if(!key.All(c=>char.IsAsciiLetterOrDigit(c)||c=='-'))throw new ArgumentException("Invalid registration report name.");
        string cachePath=Path.Combine(cacheFolder,$"{key}-v4-{registration.Options.Method}-{registration.Options.FeatureKey()}-{registration.Options.WorkingSize}-{registration.Options.MaxFeatures}-{first}-{second}.json");
        if(File.Exists(cachePath))return Read(cachePath,first,second) with {Report=report};
        ct.ThrowIfCancellationRequested();
        var diagnostics=registration.Options.Method is "phase" or "ncc"?new TranslationRegistrar().InspectPair(project,folder,first,second,ct):new SimilarityRegistrar().InspectPair(project,folder,first,second,ct);
        ct.ThrowIfCancellationRequested();
        Directory.CreateDirectory(cacheFolder);Files.Save(cachePath,diagnostics);
        return diagnostics;
    }
    private static void UseAlignmentLighting(ScanProject p)
    {
        if(p.ActiveStitchId is {} id&&StitchCatalog.Find(p,id).AlignmentFile!=null)p.LightBackground=StitchCatalog.ReadAlignment(p,StitchCatalog.Find(p,id)).LightBackground;
    }
    private static PairDiagnostics Read(string path,int first,int second)
    {
        var diagnostics=JsonSerializer.Deserialize<PairDiagnostics>(File.ReadAllText(path),Files.Json)??throw new IOException("Pair diagnostics are empty.");
        if(diagnostics.First!=first||diagnostics.Second!=second)throw new IOException("Pair diagnostics do not match the requested tiles.");
        return diagnostics;
    }
}

public sealed partial class ScanEngine
{
    public Task<PairDiagnostics> InspectPairAsync(string id,int first,int second,string? version,CancellationToken ct,string? stitchId=null)
        => EditAsync(()=>Task.Run(()=>RegistrationDiagnostics.Load(id,first,second,version,ct,stitchId),ct));
    public Task<PairDiagnostics> RecheckPairAsync(string id,int first,int second,PairRecheckRequest request,CancellationToken ct)
        => EditAsync(()=>Task.Run(()=>RegistrationDiagnostics.Recheck(id,first,second,request,ct),ct));
}
