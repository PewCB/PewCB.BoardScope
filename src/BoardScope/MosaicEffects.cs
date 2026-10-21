using OpenCvSharp;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace BoardScope;

internal sealed record PyramidSource(string Root,int Width,int Height,int TileSize,int Top,string Format)
{
    public static PyramidSource Open(string id,SavedMap map)
    {
        if(!Regex.IsMatch(map.Dzi,"^mosaic-[A-Za-z0-9-]+\\.dzi$"))throw new ArgumentException("Invalid mosaic path.");
        string path=StitchCatalog.SafePath(id,map.Dzi);var image=XDocument.Load(path).Root??throw new IOException("Empty mosaic descriptor.");
        var size=image.Element(image.Name.Namespace+"Size")??throw new IOException("Missing mosaic dimensions.");
        int width=(int?)size.Attribute("Width")??0,height=(int?)size.Attribute("Height")??0,tileSize=(int?)image.Attribute("TileSize")??0,overlap=(int?)image.Attribute("Overlap")??-1;
        string format=(string?)image.Attribute("Format")??"";
        if(width!=map.Width||height!=map.Height||width<1||height<1||tileSize is < 64 or > 1024||overlap!=0||format is not ("jpg" or "jpeg"))throw new IOException("This mosaic's dimensions or tile format are not supported for effects.");
        return new(StitchCatalog.SafePath(id,Path.ChangeExtension(map.Dzi,null)+"_files"),width,height,tileSize,(int)Math.Ceiling(Math.Log2(Math.Max(width,height))),format);
    }
    public string Tile(int level,int x,int y)=>Path.Combine(Root,level.ToString(),$"{x}_{y}.{Format}");
}

public static class MosaicEffects
{
    public static byte[] Preview(string id,string stitchId,string mapId,double? x=null,double? y=null,double? width=null,double? height=null,CancellationToken ct=default)
    {
        var p=Files.Load(id);var entry=StitchCatalog.Find(p,stitchId);var map=entry.Maps.SingleOrDefault(m=>m.Id==mapId)??throw new ArgumentException("Saved map was not found.");
        var source=PyramidSource.Open(id,map);
        double left=x??0,top=y??0,w=width??source.Width,h=height??source.Height;
        if(new[]{left,top,w,h}.Any(v=>!double.IsFinite(v))||left<0||top<0||w<=0||h<=0||left+w>source.Width+.01||top+h>source.Height+.01)throw new ArgumentException("Choose a preview area inside the mosaic.");
        int level=Math.Clamp((int)Math.Floor(source.Top+Math.Log2(1024/Math.Max(w,h))),0,source.Top);
        double factor=Math.Pow(2,level-source.Top);
        int lx=(int)Math.Floor(left*factor),ly=(int)Math.Floor(top*factor),right=(int)Math.Ceiling((left+w)*factor),bottom=(int)Math.Ceiling((top+h)*factor);
        using var preview=new Mat(bottom-ly,right-lx,MatType.CV_8UC3,Scalar.All(0));
        for(int row=ly/source.TileSize;row<=(bottom-1)/source.TileSize;row++)for(int col=lx/source.TileSize;col<=(right-1)/source.TileSize;col++)
        {
            ct.ThrowIfCancellationRequested();using var tile=Cv2.ImRead(source.Tile(level,col,row));if(tile.Empty())throw new IOException("A mosaic preview tile is missing.");
            int tx=col*source.TileSize,ty=row*source.TileSize,px=Math.Max(lx,tx),py=Math.Max(ly,ty),pw=Math.Min(right,tx+tile.Width)-px,ph=Math.Min(bottom,ty+tile.Height)-py;
            if(pw<=0||ph<=0)throw new IOException("A mosaic preview tile has incorrect dimensions.");
            using var src=new Mat(tile,new Rect(px-tx,py-ty,pw,ph));using var dst=new Mat(preview,new Rect(px-lx,py-ly,pw,ph));src.CopyTo(dst);
        }
        ct.ThrowIfCancellationRequested();return preview.ImEncode(".png");
    }
    public static void Apply(ScanProject p,ApplyEffectsRequest request,Action<string,int,int,string> progress,CancellationToken ct=default)
    {
        ArgumentNullException.ThrowIfNull(request.Effects);request.Effects.Validate();
        StitchCatalog.Ensure(p);StitchCatalog.View(p,request.StitchId,request.MapId);
        var entry=StitchCatalog.Find(p,request.StitchId);var map=entry.Maps.Single(m=>m.Id==request.MapId);var source=PyramidSource.Open(p.Id,map);
        string folder=Files.ProjectPath(p.Id),name="mosaic-effects-"+Guid.NewGuid().ToString("N"),dzi=name+".dzi",root=Path.Combine(folder,name+"_files");
        var transform=new ImageEffectTransform(request.Effects);
        int cols=(source.Width+source.TileSize-1)/source.TileSize,rows=(source.Height+source.TileSize-1)/source.TileSize,total=checked(cols*rows),done=0;
        try
        {
            ct.ThrowIfCancellationRequested();string topFolder=Path.Combine(root,source.Top.ToString());Directory.CreateDirectory(topFolder);
            var parallel=new ParallelOptions{CancellationToken=ct,MaxDegreeOfParallelism=Math.Clamp(Environment.ProcessorCount/2,1,8)};
            var progressLock=new object();
            Parallel.For(0,total,parallel,index=>
            {
                int col=index%cols,row=index/cols;ct.ThrowIfCancellationRequested();
                string input=source.Tile(source.Top,col,row),output=Path.Combine(topFolder,$"{col}_{row}.jpg");
                using var image=Cv2.ImRead(input);
                if(image.Empty()||image.Width!=Math.Min(source.TileSize,source.Width-col*source.TileSize)||image.Height!=Math.Min(source.TileSize,source.Height-row*source.TileSize))throw new IOException("A full-resolution mosaic tile is missing or has incorrect dimensions.");
                if(request.Effects.Neutral)File.Copy(input,output);
                else{transform.Apply(image,ct);if(!Cv2.ImWrite(output,image,[new ImageEncodingParam(ImwriteFlags.JpegQuality,95)]))throw new IOException("Could not save an adjusted tile.");}
                lock(progressLock)progress("effects",++done,total,"Applying image effects to full-resolution mosaic tiles");
            });
            MosaicPyramid.BuildLowerLevels(root,source.Width,source.Height,source.TileSize,progress,ct);
            ct.ThrowIfCancellationRequested();
            File.WriteAllText(Path.Combine(folder,dzi),$"<?xml version=\"1.0\" encoding=\"UTF-8\"?><Image TileSize=\"{source.TileSize}\" Overlap=\"0\" Format=\"jpg\" xmlns=\"http://schemas.microsoft.com/deepzoom/2008\"><Size Width=\"{source.Width}\" Height=\"{source.Height}\"/></Image>");
            var adjusted=new SavedMap(Guid.NewGuid().ToString("N"),DateTimeOffset.UtcNow,map.BlendMode,dzi,map.Width,map.Height,map.OriginX,map.OriginY){Effects=request.Effects,EffectSourceMapId=map.Id,LightBackground=map.LightBackground};
            entry.Maps.Add(adjusted);p.ActiveStitchId=entry.Id;p.ActiveMapId=adjusted.Id;p.Dzi=adjusted.Dzi;p.Width=adjusted.Width;p.Height=adjusted.Height;p.OriginX=adjusted.OriginX;p.OriginY=adjusted.OriginY;p.Status="ready";p.Error=null;
            ct.ThrowIfCancellationRequested();Files.Save(Path.Combine(folder,"project.json"),p);
        }
        catch
        {
            // Only these newly generated artifacts are removed. Earlier maps
            // and metadata remain intact after a stopped or failed operation.
            if(Directory.Exists(root))ProjectLibrary.DeleteEntry(root,Path.GetFullPath(folder));
            if(File.Exists(Path.Combine(folder,dzi)))File.Delete(Path.Combine(folder,dzi));
            throw;
        }
    }
}

internal static class MosaicPyramid
{
    public static void BuildLowerLevels(string root,int width,int height,int tileSize,Action<string,int,int,string> progress,CancellationToken ct)
    {
        int maxLevel=(int)Math.Ceiling(Math.Log2(Math.Max(width,height))),childWidth=width,childHeight=height;
        for(int level=maxLevel-1;level>=0;level--)
        {
            int nextWidth=(childWidth+1)/2,nextHeight=(childHeight+1)/2;
            string levelFolder=Path.Combine(root,level.ToString());Directory.CreateDirectory(levelFolder);
            int nx=(nextWidth+tileSize-1)/tileSize,ny=(nextHeight+tileSize-1)/tileSize;
            for(int y=0;y<ny;y++)for(int x=0;x<nx;x++)
            {
                ct.ThrowIfCancellationRequested();int cw=Math.Min(tileSize*2,childWidth-x*tileSize*2),ch=Math.Min(tileSize*2,childHeight-y*tileSize*2);
                using var block=new Mat(ch,cw,MatType.CV_8UC3,Scalar.All(0));
                for(int by=0;by<2;by++)for(int bx=0;bx<2;bx++)
                {
                    if(bx*tileSize>=cw||by*tileSize>=ch)continue;
                    string path=Path.Combine(root,(level+1).ToString(),$"{x*2+bx}_{y*2+by}.jpg");
                    using var child=Cv2.ImRead(path);if(child.Empty())throw new IOException("A child mosaic tile is missing.");
                    using var roi=new Mat(block,new Rect(bx*tileSize,by*tileSize,child.Width,child.Height));child.CopyTo(roi);
                }
                using var small=new Mat();Cv2.Resize(block,small,new((cw+1)/2,(ch+1)/2),0,0,InterpolationFlags.Area);
                if(!Cv2.ImWrite(Path.Combine(levelFolder,$"{x}_{y}.jpg"),small,[new ImageEncodingParam(ImwriteFlags.JpegQuality,92)]))throw new IOException("Could not save a mosaic zoom level.");
            }
            childWidth=nextWidth;childHeight=nextHeight;progress("pyramid",maxLevel-level,maxLevel,"Building zoom levels");
        }
    }
}

public sealed partial class ScanEngine
{
    public Task<string> ApplyEffectsAsync(string id,ApplyEffectsRequest request)=>EditAsync(()=>
    {
        ArgumentNullException.ThrowIfNull(request.Effects);request.Effects.Validate();var p=Files.Load(id);StitchCatalog.Ensure(p);
        var entry=StitchCatalog.Find(p,request.StitchId);var map=entry.Maps.SingleOrDefault(m=>m.Id==request.MapId)??throw new ArgumentException("Saved map was not found.");PyramidSource.Open(id,map);
        StartJob(p,ct=>{MosaicEffects.Apply(p,request,Progress,ct);return Task.CompletedTask;},preserveOnFailure:true);return Task.FromResult(id);
    });
}
