using BoardScope;
using OpenCvSharp;
using System.Text.Json;
using Stitcher=BoardScope.Stitcher;

static class StitchHistoryChecks
{
    static void Check(bool condition,string message){if(!condition)throw new Exception(message);Console.WriteLine("PASS "+message);}
    static string Json(object value)=>JsonSerializer.Serialize(value,Files.Json);
    public static void Run()
    {
        const string id="history-check";string folder=Files.ProjectPath(id);Directory.CreateDirectory(Path.Combine(folder,"captures"));
        using var board=new Mat(500,1000,MatType.CV_8UC1,Scalar.All(128));var random=new Random(952);
        for(int i=0;i<1300;i++)Cv2.Circle(board,new(random.Next(6,994),random.Next(6,494)),random.Next(1,4),Scalar.All(random.Next(20,230)),-1);
        var p=new ScanProject{Id=id,Name="History check",Settings=new(){FovWidth=6.4,FovHeight=4.8},Placement=new(){FovWidth=6.4,FovHeight=4.8},BlendMode="sharp",Tiles=[new(){File="captures/a.png",Width=640,Height=480,StageX=10,StageY=5},new(){File="captures/b.png",Width=640,Height=480,StageX=13.2,StageY=5,NominalX=320,Col=1}]};
        using(var a=new Mat(board,new Rect(0,0,640,480)))Cv2.ImWrite(Path.Combine(folder,p.Tiles[0].File),a);
        using(var b=new Mat(board,new Rect(325,3,640,480)))Cv2.ImWrite(Path.Combine(folder,p.Tiles[1].File),b);
        byte[] source=File.ReadAllBytes(Path.Combine(folder,p.Tiles[0].File));
        var stitcher=new Stitcher();stitcher.Run(p,(_,_,_,_)=>{},CancellationToken.None);
        var stage=p.Stitches.Single();var stageMap=stage.Maps.Single();string stageAlignment=File.ReadAllText(Path.Combine(folder,stage.AlignmentFile!));
        stitcher.Run(p,(_,_,_,_)=>{},CancellationToken.None,new(){Method="ncc"});
        var ncc=p.Stitches.Last();var nccMap=ncc.Maps.Single();string nccAlignment=File.ReadAllText(Path.Combine(folder,ncc.AlignmentFile!));
        stitcher.Run(p,(_,_,_,_)=>{},CancellationToken.None,new(){Method="phase"});
        var phase=p.Stitches.Last();
        Check(p.Stitches.Count==3&&p.Stitches.Select(s=>s.Method).SequenceEqual(new[]{"stage","ncc","phase"})&&p.Stitches.All(s=>s.Maps.Count==1&&s.Created.Offset==TimeSpan.Zero),"completed methods produce separate timestamped alignments and their own rendered maps");
        string currentMetadata=File.ReadAllText(Path.Combine(folder,"project.json"));string latestId=p.ActiveStitchId!;
        var historical=StitchCatalog.View(Files.Load(id),ncc.Id,nccMap.Id);
        Check(historical.ActiveStitchId==ncc.Id&&historical.ActiveMapId==nccMap.Id&&historical.Registration!.Options.Method=="ncc"&&historical.Dzi==nccMap.Dzi&&File.ReadAllText(Path.Combine(folder,"project.json"))==currentMetadata,"loading an old map applies its own positions and diagnostics without changing the project's latest result");
        var pair=historical.Registration!.Pairs.First(r=>r.Accepted);
        var diagnostics=RegistrationDiagnostics.Load(id,pair.First,pair.Second,historical.Registration.ReportFile,stitchId:ncc.Id);
        Check(diagnostics.Report.Method=="ncc"&&diagnostics.Report.Ncc!=null,"historical pair inspection loads the chosen alignment's observations instead of the latest method");
        var preview=RegistrationDiagnostics.Recheck(id,pair.First,pair.Second,new(historical.Registration.ReportFile,new(){Method="phase"},ncc.Id));
        Check(preview.Report.Method=="ncc"&&preview.ReconstructionReport!.Method=="phase"&&File.ReadAllText(Path.Combine(folder,"project.json"))==currentMetadata,"historical pair previews preserve all saved alignments and current project metadata");
        var originalTransforms=historical.Tiles.Select(Similarity.For).ToArray();historical.BlendMode="feather";
        stitcher.Reblend(historical,(_,_,_,_)=>{},CancellationToken.None);
        var updated=Files.Load(id);var changedNcc=updated.Stitches.Single(s=>s.Id==ncc.Id);
        Check(updated.Stitches.Count==3&&changedNcc.Maps.Count==2&&changedNcc.Maps.Last().BlendMode=="feather"&&updated.ActiveStitchId==ncc.Id&&updated.ActiveMapId==changedNcc.Maps.Last().Id&&updated.Tiles.Select(Similarity.For).SequenceEqual(originalTransforms),"reblending an older alignment adds a map to that stitch and keeps its exact transforms");
        Check(File.ReadAllText(Path.Combine(folder,stage.AlignmentFile!))==stageAlignment&&File.ReadAllText(Path.Combine(folder,ncc.AlignmentFile!))==nccAlignment&&File.Exists(Path.Combine(folder,nccMap.Dzi))&&source.SequenceEqual(File.ReadAllBytes(Path.Combine(folder,p.Tiles[0].File))),"historical reblending preserves snapshot bytes, prior maps and original captures");
        using var cancelled=new CancellationTokenSource();cancelled.Cancel();string completed=File.ReadAllText(Path.Combine(folder,"project.json"));
        try{stitcher.Reblend(StitchCatalog.View(Files.Load(id),stage.Id),(_,_,_,_)=>{},cancelled.Token);throw new Exception("cancel ignored");}catch(OperationCanceledException){}
        Check(completed==File.ReadAllText(Path.Combine(folder,"project.json")),"cancelled historical rendering does not publish a map or change the current result");
        var deletedMap=changedNcc.Maps.Last();var afterMapDelete=StitchCatalog.Delete(id,ncc.Id,deletedMap.Id);
        Check(afterMapDelete.Stitches.Single(s=>s.Id==ncc.Id).Maps.Count==1&&afterMapDelete.ActiveMapId==nccMap.Id&&!File.Exists(Path.Combine(folder,deletedMap.Dzi))&&!Directory.Exists(Path.Combine(folder,Path.ChangeExtension(deletedMap.Dzi,null)+"_files"))&&File.Exists(Path.Combine(folder,ncc.AlignmentFile!)),"deleting a viewed map removes its pyramid and falls back to another map of the same alignment");
        var malicious=Files.Load(id);var bad=malicious.Stitches.Single(s=>s.Id==ncc.Id);bad.Maps.Add(new("bad",DateTimeOffset.UtcNow,"sharp","captures/a.png",640,480,0,0));Files.Save(Path.Combine(folder,"project.json"),malicious);
        string forged=File.ReadAllText(Path.Combine(folder,"project.json"));
        try{StitchCatalog.Delete(id,ncc.Id,"bad");throw new Exception("unsafe path accepted");}catch(ArgumentException){}
        Check(forged==File.ReadAllText(Path.Combine(folder,"project.json"))&&source.SequenceEqual(File.ReadAllBytes(Path.Combine(folder,p.Tiles[0].File))),"map deletion rejects capture paths before changing metadata or deleting anything");
        bad.Maps.RemoveAll(m=>m.Id=="bad");Files.Save(Path.Combine(folder,"project.json"),malicious);
        string report=ncc.ReportFile!,debug=Path.ChangeExtension(report,null)+"-debug";
        var remaining=StitchCatalog.Delete(id,ncc.Id);
        Check(remaining.Stitches.Count==2&&remaining.ActiveStitchId!=ncc.Id&&!Directory.Exists(Path.Combine(folder,"stitches",ncc.Id))&&!File.Exists(Path.Combine(folder,nccMap.Dzi))&&!File.Exists(Path.Combine(folder,report))&&!Directory.Exists(Path.Combine(folder,debug))&&File.Exists(Path.Combine(folder,stageMap.Dzi))&&Directory.Exists(Path.Combine(folder,"tile-views")),"deleting a stitch removes its alignment, maps and diagnostics while retaining other stitches and shared source pyramids");
        StitchCatalog.Delete(id,phase.Id);var empty=StitchCatalog.Delete(id,stage.Id);
        Check(empty.Stitches.Count==0&&empty.Dzi==null&&empty.Registration==null&&empty.ActiveStitchId==null&&empty.Tiles.Count==2&&source.SequenceEqual(File.ReadAllBytes(Path.Combine(folder,p.Tiles[0].File))),"deleting the last stitch leaves the captured dataset available for another restitch");
        Check(StitchCatalog.Initialize(id).Stitches.Count==0,"initializing again does not resurrect deleted stitches");

        // Recover a legacy artifact set only in a fresh temporary project.
        const string legacyId="history-legacy-check";string legacyFolder=Files.ProjectPath(legacyId);Directory.CreateDirectory(Path.Combine(legacyFolder,"captures"));
        var legacy=JsonSerializer.Deserialize<ScanProject>(Json(p),Files.Json)!;legacy.Id=legacyId;legacy.Stitches=[];legacy.StitchCatalogVersion=0;legacy.ActiveStitchId=legacy.ActiveMapId=null;
        foreach(var t in legacy.Tiles)File.Copy(Path.Combine(folder,t.File),Path.Combine(legacyFolder,t.File));
        // Regenerate a genuine old-style report/map before removing its catalog.
        legacy.Dzi=legacy.PreviousMosaic?.Dzi;legacy.Registration=null;legacy.Dzi=null;legacy.PreviousMosaic=legacy.StageMosaic=null;
        stitcher.Run(legacy,(_,_,_,_)=>{},CancellationToken.None,new(){Method="phase"});
        stitcher.Run(legacy,(_,_,_,_)=>{},CancellationToken.None,new(){Method="ncc"});
        var legacyPrevious=legacy.PreviousMosaic;legacy.Stitches=[];legacy.StitchCatalogVersion=0;legacy.ActiveStitchId=legacy.ActiveMapId=null;Files.Save(Path.Combine(legacyFolder,"project.json"),legacy);
        var adopted=StitchCatalog.Initialize(legacyId);
        Check(adopted.Stitches.Any(s=>s.Method=="ncc"&&s.AlignmentFile!=null&&s.Maps.Count==1)&&adopted.Stitches.Any(s=>s.Method=="phase"&&s.AlignmentFile!=null&&s.Maps.Count==0)&&adopted.Stitches.Any(s=>s.Method=="legacy"&&s.AlignmentFile==null&&s.Maps.Any(m=>m.Dzi==legacyPrevious!.Dzi)),"legacy adoption preserves the current exact alignment, recovers older report transforms and labels unassociated maps honestly");
        Check(StitchCatalog.Initialize(legacyId).Stitches.Count==adopted.Stitches.Count,"legacy indexing is idempotent");
        foreach(string method in new[]{"stage","sift","phase","ncc"}){
            var settings=new ScanSettings{StitchMethod=method,StitchBlendMode="sharp",StitchOptions=new(){MaxShiftMm=.4}};settings.Validate();
            Check(method=="stage"?settings.AutomaticRefinement()==null:settings.AutomaticRefinement() is {MaxShiftMm:.4} options&&options.Method==method,$"after-scan dispatch honors default method {method} and its configured limits");
        }
        var preferences=new AppDefaults{Scan=new(){StitchMethod="ncc",StitchBlendMode="sharp"}};var store=new DefaultsStore();var previous=store.Load();store.Save(preferences);
        Check(store.Load()!.Scan.StitchMethod=="ncc"&&store.Load()!.Scan.StitchBlendMode=="sharp","after-scan method and blend choices survive saved-default round-trip");if(previous!=null)store.Save(previous);
    }
}
