using BoardScope;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.FileProviders;
using OpenCvSharp;
using System.IO.Ports;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://127.0.0.1:5274");
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 1024L * 1024 * 1024);
builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = 1024L * 1024 * 1024);
builder.Services.AddSingleton<CameraService>();
builder.Services.AddSingleton<MotionService>();
builder.Services.AddSingleton<BoardScope.Stitcher>();
builder.Services.AddSingleton<ScanEngine>();
builder.Services.AddSingleton<DefaultsStore>();
var app = builder.Build();
Files.Root = Path.GetFullPath(Environment.GetEnvironmentVariable("BOARDSCOPE_DATA") ?? Path.Combine(app.Environment.ContentRootPath, "..", "..", "data"));
Directory.CreateDirectory(Files.Root);
var camera = app.Services.GetRequiredService<CameraService>();
var motion = app.Services.GetRequiredService<MotionService>();
var engine = app.Services.GetRequiredService<ScanEngine>(); engine.Init();
app.Use(async (context, next) =>
{
    // The header and origin check prevent another website from issuing hardware commands.
    if (context.Request.Method != "GET" && (context.Request.Headers["X-BoardScope"] != "1" || (context.Request.Headers.Origin.Count > 0 && context.Request.Headers.Origin != "http://127.0.0.1:5274")))
    { context.Response.StatusCode = 403; await context.Response.WriteAsJsonAsync(new { error = "Open BoardScope locally to issue commands." }); return; }
    try { await next(); }
    catch (Exception e)
    {
        if (context.Response.HasStarted) throw;
        context.Response.StatusCode = e is ArgumentException ? 400 : e is InvalidOperationException ? 409 : 500;
        await context.Response.WriteAsJsonAsync(new { error = e.Message });
    }
});
app.UseDefaultFiles(); app.UseStaticFiles();
app.UseStaticFiles(new StaticFileOptions { FileProvider = new PhysicalFileProvider(Files.Root), RequestPath = "/projects", ServeUnknownFileTypes = true, DefaultContentType = "application/octet-stream", OnPrepareResponse = c => c.Context.Response.Headers.CacheControl = "no-cache" });
app.MapGet("/api/state", (DefaultsStore store) => new { engine = engine.Snapshot, camera = camera.Info, motion = motion.Snapshot, platform=store.LoadPlatform(), calibration=engine.CalibrationStatus, lightBackground=LightBackgroundStore.Load(), dataFolder = Files.Root, features = new { similarityRefinement = true, automaticCalibration=true, automaticCalibrationPhase=true, sharedStageCalibration=true, matchDiagnostics=true, featureSensitivity=true, phaseCorrelation=true, normalizedCrossCorrelation=true, hybridMatching=true, parallaxMatching=true, contentAwareSeams=true, imageEffects=true, lightBackground=true, stitchHistory=true, pagesExport=true, pagesReadme=true, platformCenter=true } });
app.MapGet("/api/defaults", (DefaultsStore store) => store.Load());
app.MapPost("/api/defaults", (AppDefaults settings, DefaultsStore store) => store.Save(settings));
app.MapGet("/api/platform", (DefaultsStore store)=>store.LoadPlatform());
app.MapPost("/api/platform", (PlatformSettings platform)=>engine.SavePlatformAsync(platform));
app.MapGet("/api/devices", async () => new { cameras = await camera.DevicesAsync(), ports = SerialPort.GetPortNames().Order().ToArray() });
app.MapGet("/api/camera/modes", (string deviceId) => camera.ModesAsync(deviceId));
app.MapGet("/api/camera/properties", () => camera.PropertiesAsync());
app.MapPost("/api/light-background/capture",(Delegate)((HttpContext context)=>engine.CaptureLightBackgroundAsync(context.RequestAborted)));
app.MapPost("/api/light-background",(LightBackgroundEnabled request)=>engine.EnableLightBackgroundAsync(request.Enabled));
app.MapGet("/api/light-background/{id}/preview",(string id,string? kind)=>Results.File(LightBackgroundStore.Preview(id,kind??"reference"),"image/jpeg"));
app.MapPost("/api/camera/open", (CameraOpen request) => engine.EditAsync(() => camera.OpenAsync(request)));
app.MapPost("/api/camera/close", () => engine.EditAsync(() => camera.CloseAsync()));
app.MapPost("/api/camera/property", (PropertySet request) => engine.EditAsync(() => camera.SetAsync(request)));
app.MapGet("/api/camera/frame", () => { using var frame = camera.Latest(); return Results.File(frame.ImEncode(".jpg", [new ImageEncodingParam(ImwriteFlags.JpegQuality, 85)]), "image/jpeg"); });
app.MapPost("/api/motion/connect", (MotionConnect request) => engine.EditAsync(async () => { await motion.ConnectAsync(request); return motion.Snapshot; }));
app.MapPost("/api/motion/disconnect", () => engine.EditAsync(() => { motion.Disconnect(); return Task.FromResult(motion.Snapshot); }));
app.MapPost("/api/motion/query", () => engine.EditAsync(() => motion.QueryAsync(CancellationToken.None), true));
app.MapPost("/api/motion/jog", (Jog request) => engine.JogAsync(request));
app.MapPost("/api/motion/reference", (BoardReferenceRequest request) => engine.ReferenceAsync(request.Point));
app.MapPost("/api/motion/home", () => engine.HomeAsync());
app.MapPost("/api/motion/board-center", (MoveBoardCenterRequest request)=>engine.MoveBoardCenterAsync(request.Feed));
// Keep the legacy route, but the UI stop button now finishes the current move.
app.MapPost("/api/motion/hold", () => { engine.Stop(); return Results.Ok(); });
app.MapPost("/api/motion/reset", () => engine.EditAsync(async () => { motion.Realtime("\u0018"); motion.Disconnect(); await Task.Delay(200); return new { message = "Reset sent. Reconnect and home the stage again." }; }));
app.MapPost("/api/motion/unlock", () => engine.EditAsync(() => motion.CommandAsync("$X", CancellationToken.None)));
app.MapPost("/api/calibration/frame", () => engine.CalibrationFrameAsync());
app.MapPost("/api/calibration/auto", (AutomaticCalibrationRequest request) => engine.StartAutomaticCalibrationAsync(request));
app.MapGet("/api/calibration/auto/{id}", (string id) => engine.CalibrationResult(id));
app.MapGet("/api/calibration/auto/{id}/image/{index:int}", (string id,int index) => engine.CalibrationImage(id,index) is {} bytes?Results.File(bytes,"image/png"):Results.NotFound());
app.MapPost("/api/calibration/auto/{id}/approve", (string id,CalibrationApproval request) => engine.ApproveCalibrationAsync(id,request));
app.MapPost("/api/plan", (ScanSettings settings) => Planner.Create(settings with { TestGrid = false,ReferencePoint="center" }));
app.MapPost("/api/scan", (ScanSettings settings) => engine.StartScanAsync(settings with { TestGrid = false }));
app.MapPost("/api/manual/start", (ScanSettings settings) => engine.StartManualAsync(settings with { TestGrid = false }));
app.MapPost("/api/manual/capture", () => engine.CaptureManualAsync());
app.MapPost("/api/manual/finish", () => engine.FinishManualAsync());
app.MapPost("/api/stop", () => { engine.Stop(); return Results.Ok(); });
app.MapGet("/api/projects", () => Directory.GetDirectories(Files.Root).Select(dir => { try { return Files.Load(Path.GetFileName(dir)); } catch { return null; } }).Where(p => p != null).OrderByDescending(p => p!.Created).Select(p => new { p!.Id, p.Name, p.Created, p.Status, count = p.Tiles.Count, p.Planned, p.Width, p.Height, p.Dzi, p.Placement, refined=p.Registration!=null, registrationMethod=p.Registration?.Options.Method, stitchCount=p.Stitches.Count, mapCount=p.Stitches.Sum(s=>s.Maps.Count), p.ActiveStitchId, p.ActiveMapId, p.AcceptedMatches, p.RejectedMatches, p.Error }));
app.MapGet("/api/projects/{id}", (string id) => Files.Load(id));
app.MapPost("/api/projects/{id}/stitches/initialize", (string id)=>engine.InitializeStitchesAsync(id));
app.MapGet("/api/projects/{id}/stitches/{stitchId}",(string id,string stitchId,string? mapId)=>StitchCatalog.View(Files.Load(id),stitchId,mapId));
app.MapDelete("/api/projects/{id}/stitches/{stitchId}",(string id,string stitchId)=>engine.DeleteStitchAsync(id,stitchId));
app.MapDelete("/api/projects/{id}/stitches/{stitchId}/maps/{mapId}",(string id,string stitchId,string mapId)=>engine.DeleteStitchAsync(id,stitchId,mapId));
app.MapGet("/api/projects/{id}/matches/{first:int}/{second:int}", (string id,int first,int second,string? version,string? stitchId,HttpContext context) => engine.InspectPairAsync(id,first,second,version,context.RequestAborted,stitchId));
app.MapPost("/api/projects/{id}/matches/{first:int}/{second:int}/recheck", (string id,int first,int second,PairRecheckRequest request,HttpContext context) => engine.RecheckPairAsync(id,first,second,request,context.RequestAborted));
app.MapGet("/api/projects/{id}/preview", (string id) => ProjectLibrary.PreviewPath(Files.Load(id)) is string path ? Results.File(path,"image/jpeg") : Results.NotFound());
app.MapPost("/api/projects/{id}/name", (string id, ProjectRename request) => engine.RenameAsync(id,request.Name));
app.MapPost("/api/projects/{id}/export-pages",(string id,PagesExportRequest request)=>engine.ExportPagesAsync(id,request));
app.MapDelete("/api/projects/{id}", async (string id) => { await engine.DeleteAsync(id);return Results.NoContent(); });
app.MapGet("/api/projects/{id}/effects-preview",async (string id,string stitchId,string mapId,double? x,double? y,double? width,double? height,HttpContext context)=>Results.File(await Task.Run(()=>MosaicEffects.Preview(id,stitchId,mapId,x,y,width,height,context.RequestAborted),context.RequestAborted),"image/png"));
app.MapPost("/api/projects/{id}/effects",(string id,ApplyEffectsRequest request)=>engine.ApplyEffectsAsync(id,request));
app.MapPost("/api/projects/{id}/stitch", (string id, StitchRequest request) => engine.StitchAsync(id,request.Placement,request.Refine,request.Registration,request.BlendMode,request.RenderOnly,request.StitchId));
app.MapPost("/api/projects/{id}/folder", (string id) => { var path = Files.ProjectPath(id); if (!Directory.Exists(path)) return Results.NotFound(); System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", path) { UseShellExecute = true }); return Results.Ok(); });
app.MapPost("/api/import", async (HttpRequest request) =>
{
    var form = await request.ReadFormAsync();
    var settings = System.Text.Json.JsonSerializer.Deserialize<ScanSettings>(form["settings"].ToString(), Files.Json) ?? throw new ArgumentException("Missing scan settings.");
    return await engine.ImportAsync(settings, int.Parse(form["rows"].ToString()), int.Parse(form["cols"].ToString()), bool.Parse(form["snake"].ToString()), form.Files);
});
app.MapPost("/api/import-image", (ImagePath input) => engine.ImportLargeAsync(input.Path));
app.Lifetime.ApplicationStopping.Register(() => engine.Stop());
app.Lifetime.ApplicationStopped.Register(() => { engine.ShutdownAsync().GetAwaiter().GetResult(); motion.Dispose(); camera.Dispose(); });
app.Run();
record ImagePath(string Path);
