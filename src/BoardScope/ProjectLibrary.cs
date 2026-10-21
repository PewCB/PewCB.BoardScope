using OpenCvSharp;
using System.Text.Json.Nodes;

namespace BoardScope;

public record ProjectRename(string? Name);
public static class ProjectLibrary
{
    public static void Delete(string id)
    {
        string root=Path.TrimEndingDirectorySeparator(Path.GetFullPath(Files.Root));
        string folder=Path.TrimEndingDirectorySeparator(Path.GetFullPath(Files.ProjectPath(id)));
        // A recursive removal must target exactly one immediate project folder.
        if(!string.Equals(Path.GetDirectoryName(folder),root,StringComparison.OrdinalIgnoreCase))throw new ArgumentException("Invalid project folder.");
        if(!Directory.Exists(folder))return;
        if((File.GetAttributes(folder)&FileAttributes.ReparsePoint)!=0)throw new ArgumentException("A linked folder cannot be deleted as a scan.");
        string metadata=Path.Combine(folder,"project.json");
        if(!File.Exists(metadata))throw new ArgumentException("This folder is not a saved scan.");
        // Keep its library entry until captures, caches and pyramid folders are removed.
        foreach(string entry in Directory.EnumerateFileSystemEntries(folder))
        {
            if(string.Equals(entry,metadata,StringComparison.OrdinalIgnoreCase))continue;
            DeleteEntry(entry,folder);
        }
        File.Delete(metadata);
        Directory.Delete(folder);
    }
    internal static void DeleteEntry(string entry,string parent)
    {
        entry=Path.GetFullPath(entry);
        if(!string.Equals(Path.GetDirectoryName(entry),parent,StringComparison.OrdinalIgnoreCase))throw new ArgumentException("Invalid project file path.");
        var attributes=File.GetAttributes(entry);
        if((attributes&FileAttributes.Directory)==0) {File.Delete(entry);return;}
        // Remove directory links themselves; never recurse into their targets.
        if((attributes&FileAttributes.ReparsePoint)==0)
            foreach(string child in Directory.EnumerateFileSystemEntries(entry))DeleteEntry(child,entry);
        Directory.Delete(entry);
    }
    public static string Rename(string id,string? name)
    {
        name=name?.Trim();
        if(string.IsNullOrEmpty(name)||name.Length>120||name.Any(char.IsControl))throw new ArgumentException("Enter a project name of 1–120 characters.");
        string path=Path.Combine(Files.ProjectPath(id),"project.json");
        var document=JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        document["name"]=name;
        if(document["settings"] is JsonObject settings)settings["name"]=name;
        Files.Save(path,document);
        return name;
    }
    public static string? PreviewPath(ScanProject p)
    {
        string folder=Files.ProjectPath(p.Id);
        string? source=p.Dzi??p.Tiles.FirstOrDefault()?.SourceDzi;
        if(source!=null)
        {
            int width=p.Dzi!=null?p.Width:p.Tiles[0].Width,height=p.Dzi!=null?p.Height:p.Tiles[0].Height;
            if(width>0&&height>0)
            {
                int top=(int)Math.Ceiling(Math.Log2(Math.Max(width,height)));
                string preview=Path.Combine(folder,Path.ChangeExtension(source,null)+"_files",Math.Min(8,top).ToString(),"0_0.jpg");
                if(File.Exists(preview))return preview;
            }
        }
        var first=p.Tiles.FirstOrDefault();if(first==null)return null;
        string image=Path.Combine(folder,first.File);if(!File.Exists(image))return null;
        var info=new FileInfo(image);
        string target=Path.Combine(folder,$"preview-first-{info.Length}-{info.LastWriteTimeUtc.Ticks}.jpg");
        if(!File.Exists(target))
        {
            using var raw=Cv2.ImRead(image);if(raw.Empty())return null;
            using var thumbnail=new Mat();double factor=Math.Min(1,192d/Math.Max(raw.Width,raw.Height));
            Cv2.Resize(raw,thumbnail,new Size(Math.Max(1,(int)Math.Round(raw.Width*factor)),Math.Max(1,(int)Math.Round(raw.Height*factor))),0,0,InterpolationFlags.Area);
            string temp=target+"-"+Guid.NewGuid().ToString("N")+".jpg";
            try { if(!Cv2.ImWrite(temp,thumbnail,[new ImageEncodingParam(ImwriteFlags.JpegQuality,85)]))throw new IOException("Could not save project preview.");File.Move(temp,target,true); }
            finally { if(File.Exists(temp))File.Delete(temp); }
        }
        return target;
    }
}
