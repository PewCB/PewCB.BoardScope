using OpenCvSharp;

namespace BoardScope;

public sealed class Stitcher
{
    public const int TileSize = 512;
    public static Mat Orient(Mat source, ScanSettings settings)
    {
        var result = source.Clone();
        if (settings.Rotation != 0) Cv2.Rotate(result, result, settings.Rotation switch { 90 => RotateFlags.Rotate90Clockwise, 180 => RotateFlags.Rotate180, _ => RotateFlags.Rotate90Counterclockwise });
        if (settings.FlipX || settings.FlipY) Cv2.Flip(result, result, settings.FlipX && settings.FlipY ? FlipMode.XY : settings.FlipX ? FlipMode.Y : FlipMode.X);
        return result;
    }
    public void Run(ScanProject project, Action<string, int, int, string> progress, CancellationToken ct, RegistrationOptions? refinement = null)
    {
        MosaicBlend.Validate(project.BlendMode);
        if (project.Tiles.Count == 0) throw new ArgumentException("Capture or import some tiles first.");
        string folder = Files.ProjectPath(project.Id);
        StitchCatalog.Ensure(project);
        LightBackgroundStore.SelectForStitch(project);
        var first = project.Tiles[0];
        var placement = (project.Placement ?? PlacementSettings.From(project.Settings)) with { FovHeight = (project.Placement?.FovWidth ?? project.Settings.FovWidth)*first.Height/first.Width };
        placement.Validate(); project.Placement = placement;
        if(project.Dzi!=null)project.PreviousMosaic=new(project.Dzi,project.Width,project.Height,project.OriginX,project.OriginY){Label=project.Registration==null?"Previous stage stitch":"Previous "+RegistrationOptions.MethodName(project.Registration.Options.Method).ToLowerInvariant()+" stitch"};
        if(refinement!=null && project.Registration==null && project.Dzi!=null)
            project.StageMosaic=new(project.Dzi,project.Width,project.Height,project.OriginX,project.OriginY);
        foreach (var tile in project.Tiles)
        {
            tile.Registration=null;
            if (project.Reference == null) { tile.X = tile.NominalX; tile.Y = tile.NominalY; tile.BaseX=tile.X;tile.BaseY=tile.Y;continue; }
            // Preserve every recorded machine position and initial estimate. Only convert units
            // with the explicitly entered FOV/signs; image content never adjusts these offsets.
            var origin = project.CropToBoard ? project.Reference : new XY(project.Tiles[0].StageX, project.Tiles[0].StageY);
            var position = placement.Pixels(tile.StageX - origin.X, tile.StageY - origin.Y, tile.Width, tile.Height);
            var reference=BoardReference.Offset(project.Settings.ReferencePoint,project.Settings.BoardWidth,project.Settings.BoardHeight);
            tile.X = position.X + (project.CropToBoard ? reference.X*tile.Width/placement.FovWidth-tile.Width/2d : 0);
            tile.Y = position.Y + (project.CropToBoard ? reference.Y*tile.Height/placement.FovHeight-tile.Height/2d : 0);
            tile.BaseX=tile.X;tile.BaseY=tile.Y;
        }
        project.AcceptedMatches = project.RejectedMatches = 0;
        project.Registration=refinement==null?null:refinement.Method is "phase" or "ncc"?new TranslationRegistrar().Run(project,folder,refinement,progress,ct):new SimilarityRegistrar().Run(project,folder,refinement,progress,ct);
        if(project.Registration is {} report){project.AcceptedMatches=report.AcceptedPairs;project.RejectedMatches=report.RejectedPairs;}
        SourcePyramids(project, folder, progress, ct);
        RenderPyramid(project, folder, progress, ct);
        if(refinement==null)project.StageMosaic=new(project.Dzi!,project.Width,project.Height,project.OriginX,project.OriginY);
        WritePositions(project, folder);
        project.Status = "ready"; project.Error = null;
        ct.ThrowIfCancellationRequested();StitchCatalog.AddMap(project,StitchCatalog.AddAlignment(project,StitchCatalog.Snapshot(project),project.Registration?.Created??DateTimeOffset.UtcNow));
        Files.Save(Path.Combine(folder, "project.json"), project);
    }
    public void Reblend(ScanProject project, Action<string, int, int, string> progress, CancellationToken ct)
    {
        MosaicBlend.Validate(project.BlendMode);
        if (project.Tiles.Count == 0) throw new ArgumentException("Capture or import some tiles first.");
        var first=project.Tiles[0];
        project.Placement ??= PlacementSettings.From(project.Settings) with { FovHeight=project.Settings.FovWidth*first.Height/first.Width };
        project.Placement.Validate();
        string folder=Files.ProjectPath(project.Id);
        StitchCatalog.Ensure(project);var entry=project.ActiveStitchId!=null?StitchCatalog.Find(project,project.ActiveStitchId):StitchCatalog.AddAlignment(project,StitchCatalog.Snapshot(project),DateTimeOffset.UtcNow);
        // Render only from the persisted alignment, never from an edited preview.
        string blend=project.BlendMode;StitchCatalog.ApplyAlignment(project,entry);project.BlendMode=blend;
        LightBackgroundStore.SelectForStitch(project);
        SourcePyramids(project,folder,progress,ct);
        RenderPyramid(project,folder,progress,ct);
        if(project.Registration==null) project.StageMosaic=new(project.Dzi!,project.Width,project.Height,project.OriginX,project.OriginY);
        WritePositions(project,folder);
        project.Status="ready";project.Error=null;
        ct.ThrowIfCancellationRequested();StitchCatalog.AddMap(project,entry);
        Files.Save(Path.Combine(folder,"project.json"),project);
    }
    private static void SourcePyramids(ScanProject p, string folder, Action<string, int, int, string> progress, CancellationToken ct)
    {
        using var correction=LightCorrection.For(p);
        for (int i = 0; i < p.Tiles.Count; i++)
        {
            ct.ThrowIfCancellationRequested(); var t = p.Tiles[i];
            string view=LightCorrection.ViewPath(t,correction),target=Path.Combine(folder,Path.ChangeExtension(view,null)!);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (!File.Exists(target + ".dzi"))
            {
                using var corrected=correction==null?null:LightCorrection.Read(Path.Combine(folder,t.File),ImreadModes.Color,correction);
                if(corrected!=null&&corrected.Empty())throw new IOException("Cannot read tile: "+t.File);
                if(corrected!=null)Cv2.CvtColor(corrected,corrected,ColorConversionCodes.BGR2RGB);
                using var memory=corrected==null?null:NetVips.Image.NewFromMemoryCopy(corrected.Data,(ulong)(corrected.Total()*corrected.ElemSize()),corrected.Width,corrected.Height,3,NetVips.Enums.BandFormat.Uchar);
                using var image=memory==null?NetVips.Image.NewFromFile(Path.Combine(folder,t.File),access:NetVips.Enums.Access.Sequential):memory.Copy(interpretation:NetVips.Enums.Interpretation.Srgb);
                image.Dzsave(target, tileSize: TileSize, overlap: 0, suffix: ".jpg[Q=95]");
            }
            t.SourceDzi = view;
            progress("tile-views",i+1,p.Tiles.Count,"Preparing individual tile views");
        }
    }
    public static void WritePositions(ScanProject p, string folder)
    {
        File.WriteAllText(Path.Combine(folder, "positions.csv"), "sequence,file,row,col,stage_x_mm,stage_y_mm,initial_x_px,initial_y_px,placed_x_px,placed_y_px,stage_placed_x_px,stage_placed_y_px,rotation_deg,scale,links,error_px,registration_status\n" + string.Join('\n', p.Tiles.Select((t,i) => $"{i+1},{t.File},{t.Row},{t.Col},{Files.N(t.StageX)},{Files.N(t.StageY)},{Files.N(t.NominalX)},{Files.N(t.NominalY)},{Files.N(t.X)},{Files.N(t.Y)},{Files.N(t.BaseX??t.X)},{Files.N(t.BaseY??t.Y)},{Files.N(Similarity.For(t).Degrees)},{Files.N(Similarity.For(t).Scale)},{t.Registration?.Links??0},{Files.N(t.Registration?.ErrorPixels??0)},{t.Registration?.Status??"stage-only"}")));
    }
    private sealed class ImageCache(LightCorrection? correction) : IDisposable
    {
        private readonly Dictionary<string, (Mat Image, long Used)> _images = [];
        private long _clock;
        public Mat Get(string path)
        {
            if (_images.TryGetValue(path, out var value)) { _images[path] = (value.Image, ++_clock); return value.Image; }
            var mat = LightCorrection.Read(path, ImreadModes.Color,correction);
            if (mat.Empty()) { mat.Dispose(); throw new IOException("Cannot read tile: " + path); }
            long bytes = mat.Total() * mat.ElemSize();
            while (_images.Count > 0 && (bytes + _images.Sum(i => i.Value.Image.Total() * i.Value.Image.ElemSize()) > 96L * 1024 * 1024 || _images.Count >= 8))
            {
                var oldest = _images.MinBy(i => i.Value.Used); oldest.Value.Image.Dispose(); _images.Remove(oldest.Key);
            }
            _images[path] = (mat, ++_clock); return mat;
        }
        public void Dispose() { foreach (var item in _images.Values) item.Image.Dispose(); }
    }
    private static unsafe Mat RenderRegion(ScanProject p, string folder, Rect region, ImageCache cache, (Tile Tile,Similarity Transform,Rect2d Bounds)[] sources, CancellationToken ct,ContentSeams? seams=null)
    {
        bool sharp=p.BlendMode is "sharp" or "content";
        using var sum = sharp ? new Mat() : new Mat(region.Height, region.Width, MatType.CV_32FC3, Scalar.All(0));
        using var weights = sharp
            ? new Mat(region.Height, region.Width, MatType.CV_64FC1, Scalar.All(double.MaxValue))
            : new Mat(region.Height, region.Width, MatType.CV_32FC1, Scalar.All(0));
        var output = new Mat(region.Height, region.Width, MatType.CV_8UC3, Scalar.All(0));
        using var warped = new Mat();
        for(int sourceIndex=0;sourceIndex<sources.Length;sourceIndex++)
        {
            var entry=sources[sourceIndex];
            var tile=entry.Tile;var transform=entry.Transform;var bounds=entry.Bounds;
            if (bounds.X >= region.Right || bounds.Y >= region.Bottom || bounds.Right <= region.X || bounds.Bottom <= region.Y) continue;
            ct.ThrowIfCancellationRequested();
            var source = cache.Get(Path.Combine(folder, tile.File));
            using var matrix = new Mat(2, 3, MatType.CV_64F);
            matrix.Set(0, 0, transform.A); matrix.Set(0, 1, -transform.B); matrix.Set(0, 2, transform.X - region.X);
            matrix.Set(1, 0, transform.B); matrix.Set(1, 1, transform.A); matrix.Set(1, 2, transform.Y - region.Y);
            Cv2.WarpAffine(source, warped, matrix, region.Size, InterpolationFlags.Linear, BorderTypes.Constant);
            double feather = Math.Max(8, Math.Min(tile.Width, tile.Height) * p.Settings.Overlap / 200);
            double determinant=transform.A*transform.A+transform.B*transform.B;
            var centre=transform.Apply(tile.Width/2d,tile.Height/2d);
            for (int y = 0; y < region.Height; y++)
            {
                float* acc = sharp ? null : (float*)sum.Ptr(y), weight = sharp ? null : (float*)weights.Ptr(y);
                double* nearest = sharp ? (double*)weights.Ptr(y) : null;
                byte* pixels = (byte*)warped.Ptr(y), dst=(byte*)output.Ptr(y);
                for (int x = 0; x < region.Width; x++)
                {
                    double dx=region.X+x-transform.X,dy=region.Y+y-transform.Y;
                    double tx=(transform.A*dx+transform.B*dy)/determinant,ty=(-transform.B*dx+transform.A*dy)/determinant;
                    if (tx < 0 || tx > tile.Width - 1 || ty < 0 || ty > tile.Height - 1) continue;
                    if(sharp)
                    {
                        // Nearest centre in mosaic pixels gives straight Voronoi seams,
                        // even when neighboring tiles have different rotations/scales.
                        double cx=region.X+x-centre.X,cy=region.Y+y-centre.Y,distance=cx*cx+cy*cy;
                        // Pairwise seams can meet at multi-tile junctions. Pick
                        // the fewest violated cuts, then nearest center, so every
                        // covered pixel has a deterministic source without holes.
                        if(seams!=null)distance+=seams.Loss(sourceIndex,region.X+x,region.Y+y)*1e15;
                        if(distance<nearest[x])
                        {
                            nearest[x]=distance;
                            for(int band=0;band<3;band++)dst[x*3+band]=pixels[x*3+band];
                        }
                        continue;
                    }
                    float w = (float)(Math.Clamp(Math.Min(tx + .5, tile.Width - .5 - tx) / feather, 0.001, 1) * Math.Clamp(Math.Min(ty + .5, tile.Height - .5 - ty) / feather, 0.001, 1));
                    weight[x] += w;
                    for (int band = 0; band < 3; band++) acc[x * 3 + band] += pixels[x * 3 + band] * w;
                }
            }
        }
        if(sharp)return output;
        for (int y = 0; y < region.Height; y++)
        {
            float* acc = (float*)sum.Ptr(y), weight = (float*)weights.Ptr(y); byte* dst = (byte*)output.Ptr(y);
            for (int x = 0; x < region.Width; x++) for (int b = 0; b < 3; b++) dst[x * 3 + b] = weight[x] > 0 ? (byte)Math.Clamp(Math.Round(acc[x * 3 + b] / weight[x]), 0, 255) : (byte)0;
        }
        return output;
    }
    private static void RenderPyramid(ScanProject p, string folder, Action<string, int, int, string> progress, CancellationToken ct)
    {
        var first = p.Tiles[0];
        var sources=p.Tiles.Select(t=>(Tile:t,Transform:Similarity.For(t),Bounds:Similarity.For(t).Bounds(t.Width,t.Height))).ToArray();
        // Manual captures may have unknown FOV, irregular steps and negative coordinates.
        // Their canvas must follow the registered image union, not an arbitrary board size.
        p.OriginX = p.CropToBoard ? 0 : Math.Floor(sources.Min(t=>t.Bounds.X));
        p.OriginY = p.CropToBoard ? 0 : Math.Floor(sources.Min(t=>t.Bounds.Y));
        int width = p.CropToBoard ? (int)Math.Ceiling(p.Settings.BoardWidth * first.Width / p.Placement!.FovWidth) : (int)Math.Ceiling(sources.Max(t=>t.Bounds.Right) - p.OriginX);
        int height = p.CropToBoard ? (int)Math.Ceiling(p.Settings.BoardHeight * first.Height / p.Placement!.FovHeight) : (int)Math.Ceiling(sources.Max(t=>t.Bounds.Bottom) - p.OriginY);
        if ((long)width * height > 20_000_000_000L) throw new ArgumentException("MVP output limit is 20 gigapixels.");
        int maxLevel = (int)Math.Ceiling(Math.Log2(Math.Max(width, height)));
        // Write to a versioned directory. An interrupted build never replaces a working viewer.
        string name = "mosaic-" + Guid.NewGuid().ToString("N")[..8];
        string root = Path.Combine(folder, name + "_files");
        Directory.CreateDirectory(Path.Combine(root, maxLevel.ToString()));
        int cols = (width + TileSize - 1) / TileSize, rows = (height + TileSize - 1) / TileSize, done = 0;
        var seams=p.BlendMode=="content"?ContentSeams.Build(p,folder,progress,ct):null;
        using var correction=LightCorrection.For(p);
        using (var cache = new ImageCache(correction))
            for (int row = 0; row < rows; row++) for (int col = 0; col < cols; col++)
            {
                ct.ThrowIfCancellationRequested();
                var region = new Rect(col * TileSize, row * TileSize, Math.Min(TileSize, width - col * TileSize), Math.Min(TileSize, height - row * TileSize));
                using var image = RenderRegion(p, folder, new Rect(region.X + (int)p.OriginX, region.Y + (int)p.OriginY, region.Width, region.Height), cache, sources, ct,seams);
                Cv2.ImWrite(Path.Combine(root, maxLevel.ToString(), $"{col}_{row}.jpg"), image, [new ImageEncodingParam(ImwriteFlags.JpegQuality, 95)]);
                progress("rendering", ++done, rows * cols, $"Blending full-resolution tiles · {width:N0} × {height:N0} px");
            }
        MosaicPyramid.BuildLowerLevels(root,width,height,TileSize,progress,ct);
        string dzi = name + ".dzi";
        File.WriteAllText(Path.Combine(folder, dzi), $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><Image TileSize=\"{TileSize}\" Overlap=\"0\" Format=\"jpg\" xmlns=\"http://schemas.microsoft.com/deepzoom/2008\"><Size Width=\"{width}\" Height=\"{height}\"/></Image>");
        p.Width = width; p.Height = height; p.Dzi = dzi;
    }
}
