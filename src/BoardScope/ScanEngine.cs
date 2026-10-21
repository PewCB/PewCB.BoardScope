using OpenCvSharp;

namespace BoardScope;

public sealed partial class ScanEngine(CameraService camera, MotionService motion, Stitcher stitcher, DefaultsStore? defaults=null)
{
    private readonly DefaultsStore _defaults=defaults??new();
    private readonly object _sync = new();
    private readonly SemaphoreSlim _actions = new(1);
    private CancellationTokenSource? _jobCancel;
    private Task? _job;
    private JobState _state = new(false, "idle", 0, 0, "Ready for a real camera", null, null);
    private ScanProject? _manual;
    private XY? _reference;
    private string? _referencePoint;
    private int _referenceEpoch;
    private int _positionEpoch;
    public bool Busy { get { lock (_sync) return _state.Busy; } }
    public JobState State { get { lock (_sync) return _state; } }
    public object Snapshot { get { lock (_sync) return new { job = _state, reference = _reference, referencePoint = _referencePoint, manual = _manual == null ? null : new { id = _manual.Id, count = _manual.Tiles.Count, total = _manual.Planned } }; } }
    public void Init() => motion.PositionLost += () => { lock (_sync) { _reference = null; _referenceEpoch++; _positionEpoch++; } if (Busy && State.Phase is "capturing" or "calibrating" or "centering") _jobCancel?.Cancel(); };
    public Task<object> CalibrationFrameAsync() => EditAsync<object>(async () =>
    {
        if (!camera.Info.Connected) throw new InvalidOperationException("Open the camera first.");
        var before = await motion.IdlePositionAsync(CancellationToken.None);
        int epoch; lock (_sync) epoch = _positionEpoch;
        using var frame = await camera.FreshAsync(3, CancellationToken.None);
        var after = await motion.IdlePositionAsync(CancellationToken.None);
        lock (_sync) if (epoch != _positionEpoch) throw new InvalidOperationException("Stage coordinates changed. Restart calibration.");
        if (Math.Abs(after.X-before.X)>.025 || Math.Abs(after.Y-before.Y)>.025) throw new InvalidOperationException("Stage moved during the snapshot. Try again while Idle.");
        return new { image = "data:image/jpeg;base64," + Convert.ToBase64String(frame.ImEncode(".jpg", [new ImageEncodingParam(ImwriteFlags.JpegQuality, 95)])), frame.Width, frame.Height, position = after, epoch, deviceId = camera.DeviceId };
    });
    public async Task<T> EditAsync<T>(Func<Task<T>> work, bool allowManual = false)
    {
        await _actions.WaitAsync();
        try
        {
            if (Busy) throw new InvalidOperationException("A job is running. Stop it before changing hardware or settings.");
            if (_manual != null && !allowManual) throw new InvalidOperationException("Finish manual collection before changing camera settings.");
            return await work();
        }
        finally { _actions.Release(); }
    }
    public Task<XY> ReferenceAsync(string point) => EditAsync(async () =>
    {
        BoardReference.Offset(point,1,1);
        var position = await motion.IdlePositionAsync(CancellationToken.None);
        lock (_sync) { _reference = position; _referencePoint = point; _referenceEpoch++; }
        return position;
    });
    public Task<object> HomeAsync() => EditAsync<object>(() =>
    {
        if (!motion.Connected) throw new InvalidOperationException("Connect FluidNC first.");
        _jobCancel?.Dispose(); _jobCancel = new(); var token = _jobCancel.Token;
        lock (_sync) { _reference = null; _referenceEpoch++; _state = new(true, "homing", 0, 1, "Homing stage…", null, null); }
        _job = Task.Run(async () =>
        {
            try
            {
                await motion.HomeAsync(token);
                lock (_sync) _state = new(false, "homed", 1, 1, "Homing complete. Move to the saved board center or start a scan.", null, null);
            }
            catch (Exception e)
            {
                lock (_sync) _state = new(false, e is OperationCanceledException ? "stopped" : "failed", 0, 1,
                    e is OperationCanceledException ? "Homing stopped. Reconnect FluidNC before continuing." : e.Message, null, e is OperationCanceledException ? null : e.Message);
            }
        });
        return Task.FromResult<object>(new { message = "Homing started." });
    });
    public Task<PlatformSettings> SavePlatformAsync(PlatformSettings platform)=>EditAsync(()=>Task.FromResult(_defaults.SavePlatform(platform)));
    private void RequireController()
    {
        if(!motion.Connected)throw new InvalidOperationException("Connect FluidNC first.");
    }
    public Task<object> MoveBoardCenterAsync(double feed=500)=>EditAsync<object>(async()=>
    {
        if(!double.IsFinite(feed)||feed is < 1 or > 3000)throw new ArgumentException("Choose a feed between 1 and 3000 mm/min.");
        RequireController();var center=_defaults.LoadPlatform().Center();
        int epoch;lock(_sync)epoch=_positionEpoch;
        await motion.IdlePositionAsync(CancellationToken.None);
        RequireController();
        lock(_sync)if(epoch!=_positionEpoch)throw new InvalidOperationException("Stage coordinates changed. Home the stage again.");
        _jobCancel?.Dispose();_jobCancel=new();var token=_jobCancel.Token;
        lock(_sync)_state=new(true,"centering",0,1,$"Moving to board center · X {Files.N(center.X)}, Y {Files.N(center.Y)} mm",null,null);
        _job=Task.Run(async()=>
        {
            try{
                RequireController();
                lock(_sync)if(epoch!=_positionEpoch)throw new InvalidOperationException("Stage coordinates changed. Home the stage again.");
                await motion.MoveAsync(center,feed,token);
                lock(_sync)_state=new(false,"centered",1,1,"At board center",null,null);
            }catch(Exception error){
                bool stopped=error is OperationCanceledException;
                lock(_sync)_state=new(false,stopped?"stopped":"failed",0,1,stopped?"Move to board center stopped.":error.Message,null,stopped?null:error.Message);
            }
        });
        return new{message="Moving to board center.",center};
    });
    public Task<XY> JogAsync(Jog jog) => EditAsync(async () =>
    {
        if (!double.IsFinite(jog.X) || !double.IsFinite(jog.Y) || Math.Abs(jog.X) > 10 || Math.Abs(jog.Y) > 10) throw new ArgumentException("Manual jogs are limited to 10 mm per axis.");
        var position = await motion.IdlePositionAsync(CancellationToken.None);
        return await motion.MoveAsync(new(position.X + jog.X, position.Y + jog.Y), jog.Feed, CancellationToken.None);
    }, true);
    private void Progress(string phase, int done, int total, string message)
    {
        lock (_sync) if (!_state.Stopping) _state = _state with { Phase = phase, Done = done, Total = total, Message = message };
    }
    private ScanProject NewProject(ScanSettings settings, string status)
    {
        var plan = Planner.Create(settings);
        var project = new ScanProject { Id = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6], Name = settings.Name, Settings = settings, Planned = plan.Points.Count, Status = status };
        Directory.CreateDirectory(Path.Combine(Files.ProjectPath(project.Id), "captures"));
        Files.Save(Path.Combine(Files.ProjectPath(project.Id), "project.json"), project); return project;
    }
    private void StartJob(ScanProject project, Func<CancellationToken, Task> work, bool preserveOnFailure = false)
    {
        _jobCancel?.Dispose(); _jobCancel = new(); var token = _jobCancel.Token;
        lock (_sync) _state = new(true, "starting", 0, project.Planned, "Starting", project.Id, null);
        _job = Task.Run(async () =>
        {
            try
            {
                await work(token);
                token.ThrowIfCancellationRequested();
                lock (_sync) _state = new(false, project.Dzi != null ? "ready" : "captured", project.Tiles.Count, project.Planned, project.Dzi != null ? "Mosaic ready to explore" : "Captures saved", project.Id, null);
            }
            catch (Exception e)
            {
                project.Status = e is OperationCanceledException ? "stopped" : "failed";
                project.Error = e is OperationCanceledException ? preserveOnFailure ? "Restitch stopped. Previous mosaic and placement kept." : "Stopped. Captures preserved." : e.Message;
                if(preserveOnFailure)
                {
                    var previous=Files.Load(project.Id);previous.Error=project.Error;Files.Save(Path.Combine(Files.ProjectPath(project.Id),"project.json"),previous);
                }
                else Files.Save(Path.Combine(Files.ProjectPath(project.Id), "project.json"), project);
                lock (_sync) _state = new(false, project.Status, project.Tiles.Count, project.Planned, project.Error, project.Id, e is OperationCanceledException ? null : e.Message);
            }
        });
    }
    public Task<string> StartScanAsync(ScanSettings settings) => EditAsync(async () =>
    {
        if (!camera.Info.Connected) throw new InvalidOperationException("Open the microscope camera first.");
        settings = FrameAspect(settings with{ReferencePoint="center"});
        var plan = Planner.Create(settings);
        var platform=_defaults.LoadPlatform();
        RequireController();
        XY origin; int epoch;
        var current = await motion.IdlePositionAsync(CancellationToken.None);
        RequireController();
        lock (_sync)
        {
            // The test patch starts at the current FIRST TILE centre, without homing or a corner move.
            origin = settings.TestGrid ? new(current.X-plan.Points[0].StageDx,current.Y-plan.Points[0].StageDy) : platform.ScanCenter(settings);
            epoch = _positionEpoch;
        }
        var project = NewProject(settings, "capturing");
        project.CropToBoard = !settings.TestGrid;
        project.Reference = origin;project.Platform=platform; project.Camera = camera.Info; project.CameraProperties = await camera.PropertiesAsync();
        Files.Save(Path.Combine(Files.ProjectPath(project.Id), "project.json"), project);
        StartJob(project, async ct =>
        {
            foreach (var move in plan.Moves)
            {
                ct.ThrowIfCancellationRequested();
                lock (_sync) if (_positionEpoch != epoch) throw new InvalidOperationException("Stage coordinates were lost. Reconnect and check the controller before continuing.");
                var point = move.Point;
                Progress("capturing", project.Tiles.Count, project.Planned, move.Capture ? $"Moving to row {point.Row + 1}, column {point.Col + 1}" : $"Approaching row {point.Row + 1} start from the capture direction");
                var actual = await motion.MoveAsync(new(origin.X + move.StageDx, origin.Y + move.StageDy), settings.Feed, ct);
                if (!move.Capture) continue;
                await Task.Delay(settings.SettleMs, ct);
                using var raw = await camera.FreshAsync(settings.DiscardFrames, ct);
                using var frame = Stitcher.Orient(raw, settings);
                if (project.Tiles.Count > 0 && (frame.Width != project.Tiles[0].Width || frame.Height != project.Tiles[0].Height)) throw new InvalidOperationException("Camera dimensions changed during capture.");
                SaveTile(project, CreateTile(frame, settings, point, actual, origin, true), frame);
                Progress("capturing", project.Tiles.Count, project.Planned, $"Captured row {point.Row + 1}, column {point.Col + 1}");
            }
            project.Status = "captured"; Stitcher.WritePositions(project, Files.ProjectPath(project.Id));
            Files.Save(Path.Combine(Files.ProjectPath(project.Id), "project.json"), project);
            if (settings.AutoStitch) RunAutomaticStitch(project,ct);
        });
        return project.Id;
    });
    private static Tile CreateTile(Mat frame, ScanSettings s, CapturePoint point, XY actual, XY? origin, bool measured)
    {
        double u = point.U, v = point.V;
        if (measured && origin != null)
        {
            var pixels=PlacementSettings.From(s).Pixels(actual.X-origin.X,actual.Y-origin.Y,frame.Width,frame.Height);
            u=pixels.X*s.FovWidth/frame.Width;v=pixels.Y*s.FovHeight/frame.Height;
            var reference=BoardReference.Offset(s.ReferencePoint,s.BoardWidth,s.BoardHeight);
            u += reference.X; v += reference.Y;
        }
        double px = (u - s.FovWidth / 2) * frame.Width / s.FovWidth, py = (v - s.FovHeight / 2) * frame.Height / s.FovHeight;
        return new() { File = $"captures/tile-r{point.Row:D3}-c{point.Col:D3}.png", Row = point.Row, Col = point.Col, Width = frame.Width, Height = frame.Height, NominalX = px, NominalY = py, X = px, Y = py, StageX = actual.X, StageY = actual.Y, CapturedAt = DateTimeOffset.UtcNow };
    }
    private static void SaveTile(ScanProject project, Tile tile, Mat frame)
    {
        var folder = Files.ProjectPath(project.Id);
        if (!Cv2.ImWrite(Path.Combine(folder, tile.File), frame)) throw new IOException("Unable to save capture.");
        CapturePreview.Write(folder, tile, frame);
        project.Tiles.Add(tile); Files.Save(Path.Combine(folder, "project.json"), project); Stitcher.WritePositions(project, folder);
    }
    public Task<string> StartManualAsync(ScanSettings settings) => EditAsync(async () =>
    {
        if (!camera.Info.Connected) throw new InvalidOperationException("Open the microscope camera first.");
        settings = FrameAspect(settings);
        var project = NewProject(settings, "manual"); project.Camera = camera.Info; project.CameraProperties = await camera.PropertiesAsync();
        if (motion.Connected)
        {
            var position = await motion.IdlePositionAsync(CancellationToken.None); var first = Planner.Create(settings).Points[0];
            project.Reference = new(position.X - first.StageDx, position.Y - first.StageDy);
        }
        Files.Save(Path.Combine(Files.ProjectPath(project.Id), "project.json"), project);
        lock (_sync) { _manual = project; _state = new(false, "manual", 0, project.Planned, "Position the FIRST tile, then capture. Scan each row left to right, returning to the row start between rows.", project.Id, null); }
        return project.Id;
    });
    private ScanSettings FrameAspect(ScanSettings s)
    {
        var c=camera.Info;bool rotated=s.Rotation%180!=0;
        return s with { FovHeight=s.FovWidth*(rotated ? (double)c.Width/c.Height : (double)c.Height/c.Width) };
    }
    public Task<object> CaptureManualAsync() => EditAsync<object>(async () =>
    {
        var project = _manual ?? throw new InvalidOperationException("Start manual collection first."); var points = Planner.Create(project.Settings).Points;
        if (project.Tiles.Count >= points.Count) throw new InvalidOperationException("All planned tiles are captured. Finish and stitch.");
        var point = points[project.Tiles.Count];
        XY actual = motion.Connected ? await motion.IdlePositionAsync(CancellationToken.None) : new(point.StageDx, point.StageDy);
        if (project.Tiles.Count == 0 && motion.Connected) project.Reference = new(actual.X - point.StageDx, actual.Y - point.StageDy);
        if (project.Reference != null && !motion.Connected) throw new InvalidOperationException("FluidNC connection was lost. Finish collection; don't mix measured and guessed positions.");
        await Task.Delay(project.Settings.SettleMs);
        using var raw = await camera.FreshAsync(project.Settings.DiscardFrames, CancellationToken.None); using var frame = Stitcher.Orient(raw, project.Settings);
        if (project.Tiles.Count > 0 && (frame.Width != project.Tiles[0].Width || frame.Height != project.Tiles[0].Height)) throw new InvalidOperationException("Frame dimensions changed during collection.");
        var tile = CreateTile(frame, project.Settings, point, actual, project.Reference, project.Reference != null); SaveTile(project, tile, frame);
        lock (_sync) _state = new(false, "manual", project.Tiles.Count, project.Planned, project.Tiles.Count == project.Planned ? "All tiles captured. Finish and stitch." : $"Saved tile {project.Tiles.Count}. Move to the next highlighted tile.", project.Id, null);
        return new { tile, next = points.Skip(project.Tiles.Count).FirstOrDefault() };
    }, true);
    public Task<string> FinishManualAsync() => EditAsync(() =>
    {
        var project = _manual ?? throw new InvalidOperationException("No manual collection is active.");
        if (project.Tiles.Count == 0) throw new InvalidOperationException("Capture at least one tile first.");
        lock (_sync) _manual = null;
        project.Status = "captured"; Files.Save(Path.Combine(Files.ProjectPath(project.Id), "project.json"), project);
        if (project.Settings.AutoStitch) StartJob(project, ct => { RunAutomaticStitch(project,ct); return Task.CompletedTask; });
        return Task.FromResult(project.Id);
    }, true);
    private void RunAutomaticStitch(ScanProject project,CancellationToken ct){project.BlendMode=project.Settings.StitchBlendMode;stitcher.Run(project,Progress,ct,project.Settings.AutomaticRefinement());}
    public Task<ScanProject> InitializeStitchesAsync(string id)=>EditAsync(()=>Task.FromResult(StitchCatalog.Initialize(id)));
    public Task<ScanProject> DeleteStitchAsync(string id,string stitchId,string? mapId=null)=>EditAsync(()=>Task.FromResult(StitchCatalog.Delete(id,stitchId,mapId)));
    public Task<string> StitchAsync(string id, PlacementSettings? placement = null, bool refine = false, RegistrationOptions? registration = null, string? blendMode = null, bool renderOnly = false,string? stitchId=null) => EditAsync(() =>
    {
        var project = Files.Load(id); if (project.Tiles.Count == 0) throw new ArgumentException("This project has no source captures.");
        StitchCatalog.Ensure(project);
        MosaicBlend.Validate(blendMode ?? project.BlendMode);
        if (renderOnly && (placement != null || refine)) throw new ArgumentException("Reblending keeps the saved alignment. Use restitching to change placement or refinement.");
        if(stitchId!=null&&!renderOnly)throw new ArgumentException("Selecting a saved alignment is for reblending; restitching creates a new alignment.");
        if(renderOnly){StitchCatalog.Ensure(project);if(stitchId!=null)StitchCatalog.ApplyAlignment(project,StitchCatalog.Find(project,stitchId));}
        project.BlendMode = blendMode ?? project.BlendMode;
        if (placement != null) { placement.Validate(); project.Placement = placement; }
        var options=refine?(registration??new RegistrationOptions()):null;options?.Validate();
        StartJob(project, ct => { if (renderOnly) stitcher.Reblend(project, Progress, ct); else stitcher.Run(project, Progress, ct, options); return Task.CompletedTask; },preserveOnFailure:true); return Task.FromResult(id);
    });
    public Task<string> RenameAsync(string id,string? name) => EditAsync(() => Task.FromResult(ProjectLibrary.Rename(id,name)));
    public Task<PagesExportResult> ExportPagesAsync(string id,PagesExportRequest request)=>EditAsync(()=>Task.Run(()=>PagesExport.Save(id,request)));
    public Task<string> DeleteAsync(string id) => EditAsync(() =>
    {
        ProjectLibrary.Delete(id);
        lock(_sync) if(_state.ProjectId==id)_state=new(false,"idle",0,0,"Scan deleted",null,null);
        return Task.FromResult(id);
    });
    public Task<string> ImportAsync(ScanSettings settings, int rows, int cols, bool snake, IFormFileCollection uploads) => EditAsync(async () =>
    {
        if (rows is < 1 or > 100 || cols is < 1 or > 100 || uploads.Count != rows * cols) throw new ArgumentException("File count must match rows × columns (maximum 100 per axis).");
        var s = settings with { BoardWidth = settings.FovWidth * (1 + (cols - 1) * (1 - settings.Overlap / 100)), BoardHeight = settings.FovHeight * (1 + (rows - 1) * (1 - settings.Overlap / 100)) };
        var project = NewProject(s, "importing"); project.Planned = uploads.Count;
        try
        {
            int width = 0, height = 0;
            for (int i = 0; i < uploads.Count; i++)
            {
                var file = uploads[i];
                if (file.Length > 128L * 1024 * 1024 || file.Length == 0) throw new ArgumentException("Each input image must be between 1 byte and 128 MB.");
                using var buffer = new MemoryStream(); await file.CopyToAsync(buffer); using var raw = Cv2.ImDecode(buffer.ToArray(), ImreadModes.Color);
                if (raw.Empty() || raw.Total() > 40_000_000) throw new ArgumentException("Inputs must be readable images of at most 40 megapixels each.");
                using var frame = Stitcher.Orient(raw, s);
                if (width != 0 && (frame.Width != width || frame.Height != height)) throw new ArgumentException("All tiles must have the same resolution.");
                width = frame.Width; height = frame.Height;
                int row = i / cols, col = snake && row % 2 == 1 ? cols - 1 - i % cols : i % cols;
                double u = s.FovWidth / 2 + col * s.FovWidth * (1 - s.Overlap / 100), v = s.FovHeight / 2 + row * s.FovHeight * (1 - s.Overlap / 100);
                SaveTile(project, CreateTile(frame, s, new(i, row, col, u, v, 0, 0), new(u, v), null, false), frame);
            }
            StartJob(project, ct => { stitcher.Run(project, Progress, ct); return Task.CompletedTask; }); return project.Id;
        }
        catch (Exception e) { project.Status = "failed"; project.Error = e.Message; Files.Save(Path.Combine(Files.ProjectPath(project.Id), "project.json"), project); throw; }
    });
    public Task<string> ImportLargeAsync(string path) => EditAsync(() =>
    {
        path = Path.GetFullPath(path); if (!File.Exists(path)) throw new ArgumentException("Image file does not exist on this computer.");
        var project = NewProject(new() { Name = Path.GetFileNameWithoutExtension(path) }, "importing");
        StartJob(project, ct =>
        {
            ct.ThrowIfCancellationRequested(); using var image = NetVips.Image.NewFromFile(path, access: NetVips.Enums.Access.Sequential);
            string name = "mosaic-" + Guid.NewGuid().ToString("N")[..8]; Progress("pyramid", 0, 1, "Generating zoom levels from the existing image");
            image.Dzsave(Path.Combine(Files.ProjectPath(project.Id), name), tileSize: Stitcher.TileSize, overlap: 0, suffix: ".jpg[Q=95]");
            ct.ThrowIfCancellationRequested(); project.Width = image.Width; project.Height = image.Height; project.Dzi = name + ".dzi"; project.Status = "ready";
            StitchCatalog.Ensure(project);Files.Save(Path.Combine(Files.ProjectPath(project.Id), "project.json"), project); return Task.CompletedTask;
        }); return Task.FromResult(project.Id);
    });
    public void Stop()
    {
        CancellationTokenSource? cancellation;
        bool homing;
        lock (_sync)
        {
            if (!_state.Busy || _state.Stopping) return;
            cancellation = _jobCancel;
            homing = _state.Phase == "homing";
            _state = _state with { Stopping = true, Message = homing ? "Stopping homing…" :
                _state.Phase is "capturing" or "calibrating" or "centering" ? "Stopping after the current move…" : "Stopping…" };
        }
        // Explicit homing abort still requires reset. Ordinary job cancellation
        // leaves the accepted move running and preserves machine coordinates.
        if (homing) motion.Realtime("\u0018");
        cancellation?.Cancel();
    }
    public async Task ShutdownAsync() { Stop(); if (_job != null) { try { await _job.WaitAsync(TimeSpan.FromSeconds(5)); } catch { } } }
}
