using BoardScope;
using OpenCvSharp;
using System.Xml.Linq;
using Stitcher = BoardScope.Stitcher;

static void Check(bool condition,string message) { if(!condition)throw new Exception(message);Console.WriteLine("PASS "+message); }
if (args is ["--motion-stop"]) { await MotionStopChecks.Run(); return; }
if (args is ["--pages-readmes",var readmeFolder]) { Console.WriteLine($"Added {PagesReadme.Backfill(readmeFolder)} board READMEs and refreshed the main scan index.");return; }
if (args is ["--pages"]) { Files.Root=Path.Combine(Path.GetTempPath(),"boardscope-pages-check-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(Files.Root);PagesExportChecks.Run();return; }
if (args is ["--light-background"]) { Files.Root=Path.Combine(Path.GetTempPath(),"boardscope-light-check-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(Files.Root);LightBackgroundChecks.Run();return; }
if (args is ["--effects"]) { Files.Root=Path.Combine(Path.GetTempPath(),"boardscope-effects-check-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(Files.Root);EffectChecks.Run();Console.WriteLine("Evidence: "+Files.Root);return; }
if (args is ["--parallax"]) { Files.Root=Path.Combine(Path.GetTempPath(),"boardscope-parallax-check-"+Guid.NewGuid().ToString("N"));ParallaxChecks.Run();Console.WriteLine("Evidence: "+Files.Root);return; }
if(args.Length==2 && args[0]=="--real")
{
    string source=Path.GetFullPath(args[1]);Files.Root=Path.Combine(Path.GetTempPath(),"boardscope-real-check-"+Guid.NewGuid().ToString("N"));
    string id=Path.GetFileName(source),folder=Files.ProjectPath(id);Directory.CreateDirectory(Path.Combine(folder,"captures"));
    File.Copy(Path.Combine(source,"project.json"),Path.Combine(folder,"project.json"));
    foreach(string file in Directory.GetFiles(Path.Combine(source,"captures")))File.Copy(file,Path.Combine(folder,"captures",Path.GetFileName(file)));
    var p=Files.Load(id);new Stitcher().Run(p,(phase,done,total,message)=>{if(done==total)Console.WriteLine(message);},CancellationToken.None);
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { p.Width,p.Height,p.Placement,p.OriginX,p.OriginY,tiles=p.Tiles.Select((t,i)=>new {sequence=i+1,t.StageX,t.StageY,t.NominalX,t.NominalY,t.X,t.Y}) },Files.Json));
    Console.WriteLine("Evidence: "+folder);return;
}

var settings=new ScanSettings {FovWidth=4.48,FovHeight=3.2,Overlap=30,BoardWidth=10.752,BoardHeight=7.68,MaxTravelX=100,MaxTravelY=100,ReferencePoint="top-left",GridYSign=1};
var plan=Planner.Create(settings);
Check(plan.Rows==3&&plan.Cols==3,"board coverage has no floating-point extra row");
Check(plan.Points.Select(p=>p.Col).SequenceEqual(new[]{0,1,2,0,1,2,0,1,2}),"all rows capture in the same left-to-right order");
Check(Planner.Create(settings with {BoardWidth=1,BoardHeight=1}).Points.Count==1,"board smaller than FOV uses one frame");
try {Planner.Create(settings with {MaxTravelX=.1});throw new Exception("travel accepted");}catch(ArgumentException){Console.WriteLine("PASS travel envelope rejects an out-of-bounds scan");}
var test=Planner.Create(settings with {TestGrid=true,StepX=10,StepY=5,StageXSign=-1,StageYSign=1,MaxTravelX=20,MaxTravelY=10});
var expected=new(double X,double Y)[]{(0,0),(-10,0),(-20,0),(0,5),(-10,5),(-20,5),(0,10),(-10,10),(-20,10)};
Check(test.Points.Count==9 && test.Points.Select((p,i)=>Math.Abs(p.StageDx-test.Points[0].StageDx-expected[i].X)<1e-9&&Math.Abs(p.StageDy-test.Points[0].StageDy-expected[i].Y)<1e-9).All(x=>x),"legacy patch keeps exact signed capture coordinates in raster order");
foreach(bool swapped in new[]{false,true})foreach(int xSign in new[]{-1,1})foreach(int ySign in new[]{-1,1})
{
    var raster=Planner.Create(settings with {SwapStageAxes=swapped,StageXSign=xSign,StageYSign=ySign,RowApproachMm=.7});
    Check(raster.Moves.Where(m=>m.Capture).Select(m=>m.Point).SequenceEqual(raster.Points),$"approach moves produce no extra captures (swap={swapped}, signs={xSign},{ySign})");
    Check(raster.Moves.Count(m=>!m.Capture)==raster.Rows && !raster.Moves[0].Capture,"every row including the first starts with a preload move");
    for(int i=0;i<raster.Moves.Count;i++)if(!raster.Moves[i].Capture)
    {
        var approach=raster.Moves[i];var capture=raster.Moves[i+1];
        Check(capture.Capture&&capture.Point.Col==0&&capture.Point==approach.Point&&
            Math.Abs(capture.StageDx-approach.StageDx-(swapped?0:xSign*.7))<1e-9&&
            Math.Abs(capture.StageDy-approach.StageDy-(swapped?ySign*.7:0))<1e-9,"first capture is approached along grid-right only, after returning");
    }
}
Check(Planner.Create(settings with {RowApproachMm=0}).Moves.All(m=>m.Capture),"zero row approach disables extra motion");
try {Planner.Create(settings with {BoardWidth=1,BoardHeight=1,RowApproachMm=2,MaxTravelX=1});throw new Exception("approach travel accepted");}catch(ArgumentException){Console.WriteLine("PASS travel envelope includes preload beyond the capture positions");}
try {Planner.Create(settings with {RowApproachMm=double.NaN});throw new Exception("invalid approach accepted");}catch(ArgumentException){Console.WriteLine("PASS nonfinite approach rejected");}
try {Planner.Create(settings with {TestGrid=true,StepX=10,MaxTravelX=19.9});throw new Exception("test travel accepted");}catch(ArgumentException){Console.WriteLine("PASS test grid checks travel from first tile centre");}
var swap=new PlacementSettings {FovWidth=4.48,FovHeight=3.2,StageXSign=-1,StageYSign=1,SwapStageAxes=true,GridYSign=1}.Pixels(-2,3,448,320);
Check(Math.Abs(swap.X-300)<1e-8&&Math.Abs(swap.Y-200)<1e-8,"stage signs and swapped axes have consistent pixel conversion");
foreach(bool swapped in new[]{false,true})foreach(int xSign in new[]{-1,1})foreach(int ySign in new[]{-1,1})
{
    var current=settings with {SwapStageAxes=swapped,StageXSign=xSign,StageYSign=ySign,GridYSign=-1};
    var raster=Planner.Create(current);
    var first=raster.Points[0];var nextRow=raster.Points[raster.Cols];
    Check(Math.Abs(nextRow.StageDx-first.StageDx-(swapped?-xSign*raster.StepY:0))<1e-9&&
        Math.Abs(nextRow.StageDy-first.StageDy-(swapped?0:-ySign*raster.StepY))<1e-9,
        "automatic grid-down opposes jog Y-up across signs and swapped axes");
    var stage=Planner.GridToStage(current,2.1,3.4);
    var pixel=PlacementSettings.From(current).Pixels(stage.X,stage.Y,448,320);
    Check(Math.Abs(pixel.X-210)<1e-9&&Math.Abs(pixel.Y-340)<1e-9,"new capture mapping round-trips stage coordinates to image pixels");
    var approach=raster.Moves[0];var capture=raster.Moves[1];
    Check(Math.Abs(capture.StageDx-approach.StageDx-(swapped?0:xSign*current.RowApproachMm))<1e-9&&
        Math.Abs(capture.StageDy-approach.StageDy-(swapped?ySign*current.RowApproachMm:0))<1e-9,
        "Y interpretation change preserves row-start X approach direction");
}
Check(new ScanSettings().GridYSign==-1&&new ScanSettings().ReferencePoint=="center","new scan defaults use Y-up jogging and a center reference");
foreach(string referencePoint in new[]{"center","top-left","top-right","bottom-left","bottom-right"})
foreach(bool swapped in new[]{false,true})
{
    var s=settings with {GridYSign=-1,ReferencePoint=referencePoint,SwapStageAxes=swapped,StageXSign=-1,StageYSign=1};
    var reference=BoardReference.Offset(referencePoint,s.BoardWidth,s.BoardHeight);
    var referencePlan=Planner.Create(s);
    foreach(var p in referencePlan.Points)
    {
        var pixels=PlacementSettings.From(s).Pixels(p.StageDx,p.StageDy,448,320);
        Check(Math.Abs(pixels.X+reference.X*100-p.U*100)<1e-8&&Math.Abs(pixels.Y+reference.Y*100-p.V*100)<1e-8,"reference point plans the same board coverage and round-trips its stage coordinates");
    }
}
using(var disconnectedMotion=new MotionService())
using(var disconnectedCamera=new CameraService())
{
    var disconnectedEngine=new ScanEngine(disconnectedCamera,disconnectedMotion,new Stitcher());
    try { await disconnectedEngine.HomeAsync();throw new Exception("disconnected homing accepted"); }
    catch(InvalidOperationException) { Check(!disconnectedEngine.Busy,"homing rejects a disconnected controller without starting a job"); }
}

Files.Root=Path.Combine(Path.GetTempPath(),"boardscope-checks-"+Guid.NewGuid().ToString("N"));
string projectId="position-check",target=Files.ProjectPath(projectId);Directory.CreateDirectory(Path.Combine(target,"captures"));
var defaultsStore=new DefaultsStore();
Check(defaultsStore.Load()==null,"no defaults file produces an empty preference result");
var preferences=new AppDefaults {Scan=settings with {StageXSign=-1,StageYSign=1,GridYSign=-1},JogStep=5,CameraDeviceId="test-camera",CameraMode=new(10,3840,2160,30,"MJPG"),CameraProperties=[new("camera","Exposure",-5,true)]};
defaultsStore.Save(preferences);
var restored=new DefaultsStore().Load()!;
Check(restored.Scan==preferences.Scan&&restored.JogStep==5&&restored.CameraMode==preferences.CameraMode&&restored.CameraProperties.SequenceEqual(preferences.CameraProperties),"scan, axis, camera and jog defaults survive a store restart");
try{defaultsStore.Save(preferences with {JogStep=double.NaN});throw new Exception("invalid defaults accepted");}catch(ArgumentException){}
Check(defaultsStore.Load()!.JogStep==5,"invalid defaults cannot overwrite saved settings");
using var board=new Mat(800,1100,MatType.CV_8UC3);Cv2.Randu(board,Scalar.All(30),Scalar.All(210));
var project=new ScanProject {Id=projectId,Name="Internal position check",Settings=settings with {Refine=true},Reference=new(-9.263,-10.55),Planned=9};
foreach(var point in plan.Points)
{
    string file=$"captures/r{point.Row}-c{point.Col}.png";
    // Image crops deliberately disagree with motion. They must never move placement.
    using var crop=new Mat(board,new Rect(point.Col*314+point.Col,point.Row*224+point.Row,448,320));Cv2.ImWrite(Path.Combine(target,file),crop);
    project.Tiles.Add(new(){File=file,Row=point.Row,Col=point.Col,Width=448,Height=320,StageX=-3.263-point.Col*3.14,StageY=-6.05+point.Row*2.24,NominalX=-point.Col*1600,NominalY=point.Row*600});
}
var recorded=project.Tiles.Select(t=>(t.StageX,t.StageY,t.NominalX,t.NominalY)).ToArray();
new Stitcher().Run(project,(_,_,_,_)=>{},CancellationToken.None);
Check(project.Tiles.All(t=>Math.Abs(t.X+t.Col*314)<1e-7&&Math.Abs(t.Y-t.Row*224)<1e-7),"stitching uses recorded stage positions only, even when legacy refinement is enabled");
Check(project.OriginX==-628&&project.Width==1076&&project.Height==768,"canvas preserves all negatively placed tiles");
Check(project.AcceptedMatches==0&&project.RejectedMatches==0,"no image matching contributes offsets");
Check(project.Tiles.All(t=>t.SourceDzi!=null&&File.Exists(Path.Combine(target,t.SourceDzi))),"each source tile has an independent zoom pyramid");
project.Placement=new(){FovWidth=4.48,FovHeight=9,StageXSign=-1,StageYSign=1,GridYSign=1};
new Stitcher().Run(project,(_,_,_,_)=>{},CancellationToken.None);
Check(Math.Abs(project.Placement.FovHeight-3.2)<1e-9,"height is derived from frame width and saved aspect ratio");
Check(project.Tiles.All(t=>Math.Abs(t.X-t.Col*314)<1e-7&&Math.Abs(t.Y-t.Row*224)<1e-7),"changing the explicit stage mapping changes placement without image guesses");
Check(project.Tiles.Select(t=>(t.StageX,t.StageY,t.NominalX,t.NominalY)).SequenceEqual(recorded),"restitching preserves gross coordinates and initial estimates exactly");
var xml=XDocument.Load(Path.Combine(target,project.Dzi!));
Check(xml.Root!.Attribute("TileSize")!.Value=="512","mosaic DZI uses 512 px tiles");
string pyramid=Path.Combine(target,Path.GetFileNameWithoutExtension(project.Dzi!) + "_files");
int top=(int)Math.Ceiling(Math.Log2(Math.Max(project.Width,project.Height)));
Check(Directory.GetDirectories(pyramid).Length==top+1,"every mosaic mip level exists");
using var edge=Cv2.ImRead(Path.Combine(pyramid,top.ToString(),"2_1.jpg"));
Check(edge.Width==52&&edge.Height==256,"last pyramid tiles have correct edge dimensions");
Check(File.ReadAllText(Path.Combine(target,"positions.csv")).StartsWith("sequence,file,row,col,stage_x_mm"),"export keeps sequence numbers, gross coordinates and placed coordinates");
foreach(string referencePoint in new[]{"center","top-left","top-right","bottom-left","bottom-right"})
{
    var s=settings with {GridYSign=-1,ReferencePoint=referencePoint};var p=new ScanProject {Id=projectId,Settings=s,Reference=new(100,80),CropToBoard=true};
    foreach(var point in Planner.Create(s).Points)
        p.Tiles.Add(new(){File=$"captures/r{point.Row}-c{point.Col}.png",Row=point.Row,Col=point.Col,Width=448,Height=320,StageX=100+point.StageDx,StageY=80+point.StageDy});
    new Stitcher().Run(p,(_,_,_,_)=>{},CancellationToken.None);
    Check(p.Tiles.All(t=>Math.Abs(t.X-t.Col*313.6)<1e-7&&Math.Abs(t.Y-t.Row*224)<1e-7)&&p.OriginX==0&&p.OriginY==0&&p.Width==1076&&p.Height==768,$"cropped mosaic placement is identical with reference {referencePoint}");
}
await PlatformChecks.Run();
await MotionStopChecks.Run();
BlendChecks.Run();
LibraryChecks.Run();
CapturePreviewChecks.Run();
AutomaticCalibrationChecks.Run();
SimilarityChecks.Run();
NativeMatchingChecks.Run();
FeatureSensitivityChecks.Run();
PhaseChecks.Run();
NccChecks.Run();
HybridChecks.Run();
ParallaxChecks.Run();
StitchHistoryChecks.Run();
EffectChecks.Run();
LightBackgroundChecks.Run();
PagesExportChecks.Run();
Console.WriteLine("Checks complete. Temporary evidence: "+target);
