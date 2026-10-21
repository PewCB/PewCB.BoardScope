using System.Text.Json;
using System.Text.RegularExpressions;

namespace BoardScope;

public class SavedStitch
{
    public string Id {get;set;}="";
    public DateTimeOffset Created {get;set;}
    public string Method {get;set;}="stage";
    public string? AlignmentFile {get;set;}
    public string? ReportFile {get;set;}
    public string? Note {get;set;}
    public int SupportedTiles {get;set;}
    public int AcceptedPairs {get;set;}
    public double? ErrorPixels {get;set;}
    public List<SavedMap> Maps {get;set;}=[];
}
public record SavedMap(string Id,DateTimeOffset Created,string BlendMode,string Dzi,int Width,int Height,double OriginX,double OriginY)
{
    public LightBackground? LightBackground { get; init; }
    public ImageEffects? Effects { get; init; }
    public string? EffectSourceMapId { get; init; }
}
public record SavedTileAlignment(string File,double X,double Y,double? BaseX,double? BaseY,TileRegistration? Registration);
public record SavedAlignment(PlacementSettings? Placement,RegistrationReport? Registration,bool CropToBoard,ScanSettings Settings,List<SavedTileAlignment> Tiles)
{
    public LightBackground? LightBackground { get; init; }
}

public static class StitchCatalog
{
    public static string MethodName(string method)=>method switch{"stage"=>"Stage positions","import"=>"Imported image","legacy"=>"Older map",_=>RegistrationOptions.MethodName(method)};
    private static string NewId()=>Guid.NewGuid().ToString("N");
    public static SavedAlignment Snapshot(ScanProject p)=>new(p.Placement,p.Registration,p.CropToBoard,p.Settings,p.Tiles.Select(t=>new SavedTileAlignment(t.File,t.X,t.Y,t.BaseX,t.BaseY,t.Registration)).ToList()){LightBackground=p.LightBackground};
    public static SavedStitch AddAlignment(ScanProject p,SavedAlignment alignment,DateTimeOffset created,string? note=null)
    {
        string id=NewId(),file=$"stitches/{id}/alignment.json",folder=Files.ProjectPath(p.Id);
        Directory.CreateDirectory(Path.Combine(folder,"stitches",id));Files.Save(Path.Combine(folder,file),alignment);
        var entry=new SavedStitch{Id=id,Created=created,Method=alignment.Registration?.Options.Method??"stage",AlignmentFile=file,ReportFile=alignment.Registration?.ReportFile,Note=note,SupportedTiles=alignment.Registration?.SupportedTiles??alignment.Tiles.Count,AcceptedPairs=alignment.Registration?.AcceptedPairs??0,ErrorPixels=alignment.Registration?.ErrorPixels};
        p.Stitches.Add(entry);return entry;
    }
    public static void AddMap(ScanProject p,SavedStitch entry,DateTimeOffset? created=null)
    {
        if(p.Dzi==null)throw new InvalidOperationException("No completed map to store.");
        var map=new SavedMap(NewId(),created??DateTimeOffset.UtcNow,p.BlendMode,p.Dzi,p.Width,p.Height,p.OriginX,p.OriginY){LightBackground=p.LightBackground};
        entry.Maps.Add(map);p.ActiveStitchId=entry.Id;p.ActiveMapId=map.Id;
    }
    // Called under the engine's edit lock, or on a job's private project copy.
    // Adopt old artifacts once; deleted entries are never rediscovered.
    public static void Ensure(ScanProject p)
    {
        if(p.StitchCatalogVersion>=1)return;
        string folder=Files.ProjectPath(p.Id);
        if(p.Dzi!=null||p.Registration!=null){
            var entry=p.Tiles.Count>0?AddAlignment(p,Snapshot(p),p.Registration?.Created??FileTime(folder,p.Dzi,p.Created),"Adopted existing stitch"):new SavedStitch{Id=NewId(),Created=FileTime(folder,p.Dzi,p.Created),Method="import"};
            if(p.Tiles.Count==0)p.Stitches.Add(entry);
            if(p.Dzi!=null)AddMap(p,entry,FileTime(folder,p.Dzi,p.Created));else p.ActiveStitchId=entry.Id;
        }
        foreach(var older in new[]{p.PreviousMosaic,p.StageMosaic}.OfType<MosaicVersion>()){
            if(p.Stitches.Any(s=>s.Maps.Any(m=>m.Dzi==older.Dzi)))continue;
            var entry=new SavedStitch{Id=NewId(),Created=FileTime(folder,older.Dzi,p.Created),Method="legacy",Note="Map retained; its exact alignment was not recorded with this pyramid."};
            entry.Maps.Add(new(NewId(),entry.Created,"unknown",older.Dzi,older.Width,older.Height,older.OriginX,older.OriginY));p.Stitches.Add(entry);
        }
        // Versioned old reports contain exact transforms, but no trustworthy
        // association to arbitrary old pyramids. Recover them as alignments only.
        foreach(string path in Directory.GetFiles(folder,"registration-*.json")){
            try{
                using var doc=JsonDocument.Parse(File.ReadAllText(path));var report=doc.RootElement.GetProperty("report").Deserialize<RegistrationReport>(Files.Json);if(report==null)continue;
                if(p.Stitches.Any(s=>s.ReportFile==report.ReportFile))continue;
                var tiles=new List<SavedTileAlignment>();
                foreach(var item in doc.RootElement.GetProperty("tiles").EnumerateArray()){
                    string file=item.GetProperty("file").GetString()!;var reg=item.GetProperty("registration").Deserialize<TileRegistration>(Files.Json);
                    if(reg==null)throw new IOException("Missing transform");
                    double? Number(string key)=>item.TryGetProperty(key,out var v)&&v.ValueKind==JsonValueKind.Number?v.GetDouble():null;
                    tiles.Add(new(file,reg.Transform.X,reg.Transform.Y,Number("baseX"),Number("baseY"),reg));
                }
                if(tiles.Count!=p.Tiles.Count||!tiles.Select(t=>t.File).SequenceEqual(p.Tiles.Select(t=>t.File)))continue;
                AddAlignment(p,new(null,report,false,p.Settings,tiles),report.Created,"Recovered from an older report. Exact tile transforms retained; reblending uses their full bounds because the original crop/mapping was not recorded.");
            }catch(Exception e)when(e is JsonException or KeyNotFoundException or InvalidOperationException or IOException or ArgumentException){/* Not a complete compatible alignment. */}
        }
        p.StitchCatalogVersion=1;
    }
    private static DateTimeOffset FileTime(string folder,string? file,DateTimeOffset fallback)=>file!=null&&File.Exists(Path.Combine(folder,file))?new DateTimeOffset(File.GetLastWriteTimeUtc(Path.Combine(folder,file))):fallback;
    public static SavedStitch Find(ScanProject p,string stitchId)=>p.Stitches.SingleOrDefault(s=>s.Id==stitchId)??throw new ArgumentException("Saved stitch was not found.");
    public static SavedAlignment ReadAlignment(ScanProject p,SavedStitch entry)
    {
        if(entry.AlignmentFile==null)throw new InvalidOperationException("Only a rendered map was preserved for this entry; tile inspection and reblending require a saved alignment.");
        string expected=$"stitches/{entry.Id}/alignment.json";
        if(entry.AlignmentFile!=expected||!Regex.IsMatch(entry.Id,"^[a-f0-9]{32}$"))throw new ArgumentException("Invalid alignment path.");
        return JsonSerializer.Deserialize<SavedAlignment>(File.ReadAllText(SafePath(p.Id,expected)),Files.Json)??throw new IOException("Saved alignment is empty.");
    }
    public static void ApplyAlignment(ScanProject p,SavedStitch entry)
    {
        var alignment=ReadAlignment(p,entry);
        if(alignment.Tiles.Count!=p.Tiles.Count||!alignment.Tiles.Select(t=>t.File).SequenceEqual(p.Tiles.Select(t=>t.File)))throw new InvalidOperationException("Saved alignment does not match this project's captures.");
        for(int i=0;i<p.Tiles.Count;i++){var t=p.Tiles[i];var s=alignment.Tiles[i];t.X=s.X;t.Y=s.Y;t.BaseX=s.BaseX;t.BaseY=s.BaseY;t.Registration=s.Registration;}
        p.Placement=alignment.Placement??p.Placement;p.Registration=alignment.Registration;p.CropToBoard=alignment.CropToBoard;p.Settings=alignment.Settings;p.LightBackground=alignment.LightBackground;
        using(var correction=LightCorrection.For(p))foreach(var tile in p.Tiles)tile.SourceDzi=LightCorrection.ViewPath(tile,correction);
        p.AcceptedMatches=entry.AcceptedPairs;p.RejectedMatches=alignment.Registration?.RejectedPairs??0;
        p.ActiveStitchId=entry.Id;p.ActiveMapId=null;p.Dzi=null;p.Width=p.Height=0;
    }
    public static ScanProject View(ScanProject p,string stitchId,string? mapId=null)
    {
        var entry=Find(p,stitchId);
        if(entry.AlignmentFile!=null)ApplyAlignment(p,entry);else {p.Registration=null;foreach(var tile in p.Tiles)tile.Registration=null;p.ActiveStitchId=entry.Id;p.ActiveMapId=null;p.Dzi=null;p.AcceptedMatches=p.RejectedMatches=0;}
        var map=mapId==null?entry.Maps.LastOrDefault():entry.Maps.SingleOrDefault(m=>m.Id==mapId)??throw new ArgumentException("Saved map was not found.");
        if(map!=null){p.Dzi=map.Dzi;p.Width=map.Width;p.Height=map.Height;p.OriginX=map.OriginX;p.OriginY=map.OriginY;p.BlendMode=map.BlendMode;p.ActiveMapId=map.Id;p.LightBackground=map.LightBackground;}
        using(var correction=LightCorrection.For(p))foreach(var tile in p.Tiles)tile.SourceDzi=LightCorrection.ViewPath(tile,correction);
        p.PreviousMosaic=p.StageMosaic=null;return p;
    }
    public static ScanProject Initialize(string id){var p=Files.Load(id);Ensure(p);Files.Save(Path.Combine(Files.ProjectPath(id),"project.json"),p);return p;}
    public static ScanProject Delete(string id,string stitchId,string? mapId=null)
    {
        var p=Files.Load(id);Ensure(p);var entry=Find(p,stitchId);var targets=new List<string>();
        var maps=mapId==null?entry.Maps.ToArray():new[]{entry.Maps.SingleOrDefault(m=>m.Id==mapId)??throw new ArgumentException("Saved map was not found.")};
        foreach(var map in maps){
            if(!Regex.IsMatch(map.Dzi,"^mosaic-[A-Za-z0-9-]+\\.dzi$"))throw new ArgumentException("Invalid saved map path.");
            if(p.Stitches.Where(s=>s.Id!=entry.Id).Any(s=>s.Maps.Any(m=>m.Dzi==map.Dzi))||entry.Maps.Except(maps).Any(m=>m.Dzi==map.Dzi))continue;
            targets.Add(SafePath(id,map.Dzi));targets.Add(SafePath(id,Path.ChangeExtension(map.Dzi,null)+"_files"));
        }
        if(mapId==null){
            if(entry.AlignmentFile!=null){ReadAlignment(p,entry);targets.Add(SafePath(id,$"stitches/{entry.Id}"));}
            if(entry.ReportFile!=null&&!p.Stitches.Where(s=>s.Id!=entry.Id).Any(s=>s.ReportFile==entry.ReportFile)){
                if(!Regex.IsMatch(entry.ReportFile,"^registration-[A-Za-z0-9-]+\\.json$"))throw new ArgumentException("Invalid registration path.");
                targets.Add(SafePath(id,entry.ReportFile));targets.Add(SafePath(id,Path.ChangeExtension(entry.ReportFile,null)+"-debug"));
                string cache=SafePath(id,"match-debug-cache");if(Directory.Exists(cache))targets.AddRange(Directory.GetFiles(cache,Path.GetFileNameWithoutExtension(entry.ReportFile)+"-*.json").Select(f=>SafePath(id,Path.GetRelativePath(Files.ProjectPath(id),f))));
            }
            p.Stitches.Remove(entry);
        }else foreach(var map in maps)entry.Maps.Remove(map);
        foreach(var map in maps){if(p.PreviousMosaic?.Dzi==map.Dzi)p.PreviousMosaic=null;if(p.StageMosaic?.Dzi==map.Dzi)p.StageMosaic=null;}
        if(p.ActiveStitchId==stitchId&&(mapId==null||p.ActiveMapId==mapId)){
            var fallback=p.Stitches.FirstOrDefault(s=>s.Id==p.ActiveStitchId&&s.Maps.Count>0)??p.Stitches.Where(s=>s.Maps.Count>0).OrderByDescending(s=>s.Maps.Max(m=>m.Created)).FirstOrDefault()??p.Stitches.Where(s=>s.AlignmentFile!=null).OrderByDescending(s=>s.Created).FirstOrDefault();
            if(fallback!=null)View(p,fallback.Id);else{
                p.ActiveStitchId=p.ActiveMapId=p.Dzi=null;p.Registration=null;p.AcceptedMatches=p.RejectedMatches=0;p.Width=p.Height=0;
                foreach(var t in p.Tiles){t.X=t.BaseX??t.NominalX;t.Y=t.BaseY??t.NominalY;t.Registration=null;}
            }
            p.Status=p.Dzi==null?"captured":"ready";
        }
        // All paths and fallback snapshots are checked before changing metadata.
        Files.Save(Path.Combine(Files.ProjectPath(id),"project.json"),p);
        Stitcher.WritePositions(p,Files.ProjectPath(id));
        foreach(var target in targets.Distinct(StringComparer.OrdinalIgnoreCase))if(File.Exists(target)||Directory.Exists(target))ProjectLibrary.DeleteEntry(target,Path.GetDirectoryName(target)!);
        return p;
    }
    internal static string SafePath(string id,string relative)
    {
        string root=Path.GetFullPath(Files.ProjectPath(id)),path=Path.GetFullPath(Path.Combine(root,relative));
        if(!path.StartsWith(root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new ArgumentException("Invalid stitch artifact path.");
        for(string? parent=Path.GetDirectoryName(path);parent!=null&&parent.StartsWith(root,StringComparison.OrdinalIgnoreCase);parent=Path.GetDirectoryName(parent))
            if(Directory.Exists(parent)&&(File.GetAttributes(parent)&FileAttributes.ReparsePoint)!=0)throw new ArgumentException("Linked stitch folders are not supported.");
        return path;
    }
}
