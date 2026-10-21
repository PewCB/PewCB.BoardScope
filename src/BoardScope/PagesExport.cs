using System.Text;
using System.Xml.Linq;
using OpenCvSharp;

namespace BoardScope;

public record PagesExportRequest(string ParentFolder,string BoardName,string Dzi,string Html,bool ReplaceExisting=false,string? Description=null,string? PublicUrl=null);
public record PagesExportResult(string Folder,string Index,string? Backup,int Files);
public static class PagesExport
{
    public static PagesExportResult Save(string id,PagesExportRequest request)
    {
        string name=request.BoardName.Trim();
        if(name.Length is <1 or >100||name.StartsWith('.')||name.EndsWith('.')||name.EndsWith(' ')||name.IndexOfAny(Path.GetInvalidFileNameChars())>=0||System.Text.RegularExpressions.Regex.IsMatch(name,"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\\.|$)",System.Text.RegularExpressions.RegexOptions.IgnoreCase))throw new ArgumentException("Choose a valid board folder name (up to 100 characters).");
        if(!Path.IsPathFullyQualified(request.ParentFolder))throw new ArgumentException("Enter an absolute parent folder path.");
        if(string.IsNullOrWhiteSpace(request.Html)||request.Html.Length>2_000_000||!request.Html.StartsWith("<!doctype html>",StringComparison.OrdinalIgnoreCase))throw new ArgumentException("Invalid viewer HTML.");
        var page=PagesReadme.Describe(name,request.Html,request.Description,request.PublicUrl);
        var p=Files.Load(id);
        if(!System.Text.RegularExpressions.Regex.IsMatch(request.Dzi,"^mosaic-[a-zA-Z0-9-]+\\.dzi$")||(p.Dzi!=request.Dzi&&!p.Stitches.Any(s=>s.Maps.Any(m=>m.Dzi==request.Dzi))))throw new ArgumentException("Choose a saved rendered map from this project.");
        string parent=Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.ParentFolder)),destination=Child(parent,name);
        string dataRoot=Path.TrimEndingDirectorySeparator(Path.GetFullPath(Files.Root));
        if(destination.Equals(dataRoot,StringComparison.OrdinalIgnoreCase)||destination.StartsWith(dataRoot+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)||dataRoot.StartsWith(destination+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new ArgumentException("Export outside the scan library and its parent folders.");
        if(Path.GetDirectoryName(typeof(PagesExport).Assembly.Location)!.StartsWith(destination+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new ArgumentException("Choose a folder outside the running app.");
        RejectLinks(parent);
        if(File.Exists(destination))throw new ArgumentException("The board path is an existing file.");
        if(Directory.Exists(destination)){RejectLinks(destination);if(!request.ReplaceExisting)throw new InvalidOperationException("This board folder already exists. Choose another name or enable replacement with backup.");}
        string source=Files.ProjectPath(id),dzi=Path.Combine(source,request.Dzi),tiles=Path.Combine(source,Path.GetFileNameWithoutExtension(request.Dzi)+"_files");
        RejectLinks(dzi);RejectLinks(tiles);
        if(!File.Exists(dzi)||!Directory.Exists(tiles))throw new IOException("The saved map pyramid is missing.");
        Directory.CreateDirectory(parent);
        string rootReadme=Path.Combine(parent,"README.md"),indexMarkdown=PagesReadme.Index(parent,page);
        string pendingReadme=Child(parent,".boardscope-readme-"+Guid.NewGuid().ToString("N")+".tmp");
        string staging=Child(parent,".boardscope-export-"+Guid.NewGuid().ToString("N")),backup=null!;
        Directory.CreateDirectory(staging);
        int count=0;
        try{
            File.Copy(dzi,Path.Combine(staging,request.Dzi));count++;
            string tileDestination=Path.Combine(staging,Path.GetFileName(tiles));
            Copy(tiles,tileDestination,ref count);
            Preview(dzi,tiles,Path.Combine(staging,"preview.jpg"),Path.Combine(staging,"thumbnail.jpg"));count+=2;
            File.WriteAllText(Path.Combine(staging,"index.html"),request.Html,new UTF8Encoding(false));count++;
            File.WriteAllText(Path.Combine(staging,"README.md"),PagesReadme.Board(page),PagesReadme.Utf8);count++;
            File.WriteAllText(pendingReadme,indexMarkdown,PagesReadme.Utf8);
            // Publish only a complete folder. Replacing an existing export keeps
            // all its files in a sibling backup rather than deleting them.
            if(Directory.Exists(destination)){
                if(!request.ReplaceExisting)throw new InvalidOperationException("The board folder was created while exporting. Choose another name.");
                RejectLinks(destination);backup=Child(parent,"."+name+"-backup-"+DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N")[..8]);
                Directory.Move(destination,backup);
            }
            try{
                Directory.Move(staging,destination);
                try{File.Move(pendingReadme,rootReadme,true);}catch{Directory.Move(destination,staging);throw;}
            }catch{if(backup!=null&&!Directory.Exists(destination))Directory.Move(backup,destination);throw;}
            return new(destination,Path.Combine(destination,"index.html"),backup,count);
        }finally{if(Directory.Exists(staging))ProjectLibrary.DeleteEntry(staging,parent);if(File.Exists(pendingReadme))File.Delete(pendingReadme);}
    }
    private static string Child(string parent,string name){string path=Path.GetFullPath(Path.Combine(parent,name));if(!string.Equals(Path.GetDirectoryName(path),parent,StringComparison.OrdinalIgnoreCase))throw new ArgumentException("Invalid export folder.");return path;}
    internal static void RejectLinks(string path){for(string? current=Path.GetFullPath(path);current!=null;current=Path.GetDirectoryName(current))if((Directory.Exists(current)||File.Exists(current))&&(File.GetAttributes(current)&FileAttributes.ReparsePoint)!=0)throw new ArgumentException("Linked export or source folders are not supported.");}
    private static void Copy(string source,string destination,ref int count){
        RejectLinks(source);Directory.CreateDirectory(destination);
        foreach(string path in Directory.EnumerateFileSystemEntries(source)){
            RejectLinks(path);string target=Path.Combine(destination,Path.GetFileName(path));
            if(Directory.Exists(path))Copy(path,target,ref count);else{File.Copy(path,target);count++;}
        }
    }
    private static void Preview(string dzi,string tiles,string social,string thumbnail){
        var root=XDocument.Load(dzi).Root??throw new IOException("Invalid pyramid descriptor.");var size=root.Elements().Single(e=>e.Name.LocalName=="Size");
        int width=int.Parse(size.Attribute("Width")!.Value),height=int.Parse(size.Attribute("Height")!.Value),tileSize=int.Parse(root.Attribute("TileSize")!.Value),overlap=int.Parse(root.Attribute("Overlap")!.Value);
        string format=root.Attribute("Format")!.Value;
        if(width<=0||height<=0||tileSize is <1 or >4096||overlap<0||overlap>=tileSize||format is not ("jpg" or "jpeg" or "png" or "webp"))throw new IOException("Unsupported pyramid descriptor.");
        int top=(int)Math.Ceiling(Math.Log2(Math.Max(width,height))),level=Math.Min(11,top);double factor=Math.Pow(2,level-top);
        int w=(int)Math.Ceiling(width*factor),h=(int)Math.Ceiling(height*factor);
        using var overview=new Mat(h,w,MatType.CV_8UC3,Scalar.All(0));
        for(int y=0;y<(int)Math.Ceiling(h/(double)tileSize);y++)for(int x=0;x<(int)Math.Ceiling(w/(double)tileSize);x++){
            using var image=Cv2.ImRead(Path.Combine(tiles,level.ToString(),$"{x}_{y}.{format}"));if(image.Empty())throw new IOException("A preview pyramid tile is missing.");
            int tw=Math.Min(tileSize,w-x*tileSize),th=Math.Min(tileSize,h-y*tileSize);
            using var from=new Mat(image,new Rect(x>0?overlap:0,y>0?overlap:0,tw,th));using var to=new Mat(overview,new Rect(x*tileSize,y*tileSize,tw,th));from.CopyTo(to);
        }
        SavePreview(overview,social,1200,630);SavePreview(overview,thumbnail,512,512);
    }
    private static void SavePreview(Mat source,string path,int width,int height){
        using var canvas=new Mat(height,width,MatType.CV_8UC3,new Scalar(40,28,18));double scale=Math.Min(width/(double)source.Width,height/(double)source.Height);
        int w=Math.Max(1,(int)Math.Round(source.Width*scale)),h=Math.Max(1,(int)Math.Round(source.Height*scale));
        using var resized=new Mat();Cv2.Resize(source,resized,new Size(w,h),0,0,scale<1?InterpolationFlags.Area:InterpolationFlags.Linear);
        using var target=new Mat(canvas,new Rect((width-w)/2,(height-h)/2,w,h));resized.CopyTo(target);
        if(!Cv2.ImWrite(path,canvas,[new ImageEncodingParam(ImwriteFlags.JpegQuality,92)]))throw new IOException("Could not save preview image.");
    }
}
