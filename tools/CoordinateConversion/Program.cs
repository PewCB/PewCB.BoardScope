using BoardScope;
using OpenCvSharp;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Stitcher = BoardScope.Stitcher;

// A one-time conversion of the three inspected legacy datasets in this library.
// Captured pixels and machine coordinates remain in their original convention.
Files.Root=Path.GetFullPath(args.FirstOrDefault(a=>!a.StartsWith("--")) ?? "data");
bool apply=args.Contains("--apply");
string[] ids=["20261001-143941-682ecd","20261001-164821-209aa6","20261005-183926-04cfa8"];
const string repairedId="20261001-164821-209aa6", restoredDzi="mosaic-80c76a33.dzi";
string backup=Path.Combine(Files.Root,"..","output","coordinate-conversion-"+DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss"));
var changes=new List<(string Id,JsonObject Original,JsonObject Converted)>();
foreach(string id in ids)
{
    string folder=Files.ProjectPath(id),path=Path.Combine(folder,"project.json");
    if(!File.Exists(path))continue;
    var original=JsonNode.Parse(File.ReadAllText(path))!.AsObject();
    if(File.Exists(Path.Combine(folder,"coordinate-conversion-v1.json"))) { Console.WriteLine($"SKIP {id}: already converted");continue; }
    var p=original.Deserialize<ScanProject>(Files.Json)!;
    if(p.Reference==null||!p.CropToBoard||p.Tiles.Count==0)throw new Exception($"Unexpected legacy dataset: {id}");
    var oldSettings=p.Settings with {ReferencePoint="top-left",GridYSign=1};
    double nominalError=p.Tiles.Max(t=>
    {
        var xy=PlacementSettings.From(oldSettings).Pixels(t.StageX-p.Reference.X,t.StageY-p.Reference.Y,t.Width,t.Height);
        return Math.Max(Math.Abs(xy.X-t.Width/2d-t.NominalX),Math.Abs(xy.Y-t.Height/2d-t.NominalY));
    });
    if(nominalError>.01)throw new Exception($"Legacy mapping does not reproduce saved initial coordinates in {id}: {nominalError} px");
    if(id!=repairedId && (original["settings"]?["gridYSign"]!=null||original["settings"]?["referencePoint"]!=null))throw new Exception($"Unexpected newer convention in {id}");
    var converted=(JsonObject)original.DeepClone();
    ConvertMapping(converted["settings"]!.AsObject());
    converted["settings"]!["referencePoint"]="top-left";
    if(converted["placement"] is JsonObject placement)ConvertMapping(placement);
    var q=converted.Deserialize<ScanProject>(Files.Json)!;
    var newPlacement=q.Placement??PlacementSettings.From(q.Settings);
    var oldPlacement=(p.Placement??PlacementSettings.From(oldSettings)) with {GridYSign=1};
    double positionError=p.Tiles.Max(t=>
    {
        var before=oldPlacement.Pixels(t.StageX-p.Reference.X,t.StageY-p.Reference.Y,t.Width,t.Height);
        var after=newPlacement.Pixels(t.StageX-q.Reference!.X,t.StageY-q.Reference.Y,t.Width,t.Height);
        return Math.Max(Math.Abs(before.X-after.X),Math.Abs(before.Y-after.Y));
    });
    if(positionError>1e-8)throw new Exception($"Conversion changed image placement in {id}");
    if(id==repairedId)
    {
        if(p.Registration!=null||p.Tiles.Any(t=>t.Registration!=null))throw new Exception("Cannot restore stage mosaic over saved visual corrections.");
        ValidateOriginalMosaic(p,folder,restoredDzi);
        foreach(var tile in converted["tiles"]!.AsArray())
        {
            var t=tile!.Deserialize<Tile>(Files.Json)!;
            var xy=newPlacement.Pixels(t.StageX-q.Reference!.X,t.StageY-q.Reference.Y,t.Width,t.Height);
            tile!["x"]=xy.X-t.Width/2d;tile["baseX"]=xy.X-t.Width/2d;
            tile["y"]=xy.Y-t.Height/2d;tile["baseY"]=xy.Y-t.Height/2d;
        }
        converted["dzi"]=restoredDzi;
        converted["stageMosaic"]=JsonSerializer.SerializeToNode(new MosaicVersion(restoredDzi,p.Width,p.Height,0,0),Files.Json);
        converted["originX"]=0;converted["originY"]=0;
        converted["status"]="ready";converted["error"]=null;
    }
    foreach(string key in new[]{"reference","camera","cameraProperties","created"})
        if(!JsonNode.DeepEquals(original[key],converted[key]))throw new Exception($"Conversion changed preserved {key}");
    for(int i=0;i<p.Tiles.Count;i++)foreach(string key in new[]{"file","stageX","stageY","nominalX","nominalY","capturedAt","registration"})
        if(!JsonNode.DeepEquals(original["tiles"]![i]![key],converted["tiles"]![i]![key]))throw new Exception($"Conversion changed preserved tile {i}: {key}");
    Console.WriteLine($"CHECK {id}: {p.Tiles.Count} tiles, nominal error {nominalError:G3} px, mapping change {positionError:G3} px");
    changes.Add((id,original,converted));
}
if(!apply){Console.WriteLine("Dry run passed. Use --apply to write backed-up metadata.");return;}
// Validate every project before changing any project.
foreach(var c in changes)
{
    string folder=Files.ProjectPath(c.Id),saved=Path.Combine(backup,c.Id);
    Directory.CreateDirectory(saved);
    File.Copy(Path.Combine(folder,"project.json"),Path.Combine(saved,"project.json"));
    if(File.Exists(Path.Combine(folder,"positions.csv")))File.Copy(Path.Combine(folder,"positions.csv"),Path.Combine(saved,"positions.csv"));
}
foreach(var c in changes)
{
    string folder=Files.ProjectPath(c.Id);
    Files.Save(Path.Combine(folder,"project.json"),c.Converted);
    var reloaded=Files.Load(c.Id);
    Stitcher.WritePositions(reloaded,folder);
    Files.Save(Path.Combine(folder,"coordinate-conversion-v1.json"),new {convertedAt=DateTimeOffset.UtcNow,backup,convention="Y-up jogging, Y-down grid, original top-left reference",restoredMosaic=c.Id==repairedId?restoredDzi:null});
    Console.WriteLine($"CONVERTED {c.Id}");
}
Console.WriteLine("Backup: "+Path.GetFullPath(backup));

static void ConvertMapping(JsonObject mapping)
{
    bool swapped=mapping["swapStageAxes"]!.GetValue<bool>();
    string verticalAxis=swapped?"stageXSign":"stageYSign";
    mapping[verticalAxis]=-mapping[verticalAxis]!.GetValue<int>();
    mapping["gridYSign"]=-1;
}
static void ValidateOriginalMosaic(ScanProject p,string folder,string dzi)
{
    var xml=XDocument.Load(Path.Combine(folder,dzi));var size=xml.Root!.Elements().Single();
    if(int.Parse(size.Attribute("Width")!.Value)!=p.Width||int.Parse(size.Attribute("Height")!.Value)!=p.Height)throw new Exception("Original mosaic dimensions differ.");
    int top=(int)Math.Ceiling(Math.Log2(Math.Max(p.Width,p.Height)));
    string tiles=Path.Combine(folder,Path.GetFileNameWithoutExtension(dzi)+"_files",top.ToString());
    double maximumError=0;
    foreach(int index in Enumerable.Range(0,20).Select(i=>i*(p.Tiles.Count-1)/19))
    {
        var t=p.Tiles[index];
        int x=(int)Math.Round(t.NominalX+t.Width/2d),y=(int)Math.Round(t.NominalY+t.Height/2d);
        // Keep a 17-pixel patch inside one stored pyramid tile.
        x=Math.Clamp(x%512,8,503)+x/512*512;y=Math.Clamp(y%512,8,503)+y/512*512;
        using var mosaic=Cv2.ImRead(Path.Combine(tiles,$"{x/512}_{y/512}.jpg"));
        using var raw=Cv2.ImRead(Path.Combine(folder,t.File));
        using var expected=new Mat();
        Cv2.GetRectSubPix(raw,new Size(17,17),new Point2f((float)(x-t.NominalX),(float)(y-t.NominalY)),expected);
        using var actual=new Mat(mosaic,new Rect(x%512-8,y%512-8,17,17));
        double error=Cv2.Norm(expected,actual,NormTypes.L1)/(17*17*3);
        maximumError=Math.Max(maximumError,error);
        if(error>12)throw new Exception($"Original mosaic does not match recorded layout at tile {index+1}: {error:F2} intensity error");
    }
    Console.WriteLine($"Original mosaic verified against 20 source patches: maximum mean intensity error {maximumError:F2}/255");
}
