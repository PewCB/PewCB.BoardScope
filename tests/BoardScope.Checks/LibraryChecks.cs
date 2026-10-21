using BoardScope;
using OpenCvSharp;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

static class LibraryChecks
{
    static void Check(bool condition,string message){if(!condition)throw new Exception(message);Console.WriteLine("PASS "+message);}
    public static void Run()
    {
        const string id="library-check";string folder=Files.ProjectPath(id);Directory.CreateDirectory(Path.Combine(folder,"captures"));
        using(var image=new Mat(360,640,MatType.CV_8UC3,new Scalar(30,80,150)))Cv2.ImWrite(Path.Combine(folder,"captures/a.png"),image);
        var p=new ScanProject {Id=id,Name="PCB inspection",Tiles=[new(){File="captures/a.png",Width=640,Height=360,StageX=12.345,StageY=-6.789,NominalX=17.2,NominalY=4.7}],Settings=new(){Name="PCB inspection"}};
        string path=Path.Combine(folder,"project.json");Files.Save(path,p);
        var original=JsonNode.Parse(File.ReadAllText(path))!.AsObject();original["futureMetadata"]=new JsonObject { ["preserved"]=true };Files.Save(path,original);
        byte[] rawHash=SHA256.HashData(File.ReadAllBytes(Path.Combine(folder,"captures/a.png")));
        string name=ProjectLibrary.Rename(id,"  PCB α · power board  ");
        var expected=(JsonObject)original.DeepClone();expected["name"]=name;expected["settings"]!["name"]=name;
        Check(name=="PCB α · power board"&&JsonNode.DeepEquals(JsonNode.Parse(File.ReadAllText(path)),expected),"project renaming persists Unicode names and changes only the two name fields");
        foreach(string invalid in new[]{" ",new string('a',121),"line\nbreak"})
        {
            try {ProjectLibrary.Rename(id,invalid);throw new Exception("Invalid name accepted.");}catch(ArgumentException){}
        }
        Check(JsonNode.DeepEquals(JsonNode.Parse(File.ReadAllText(path)),expected),"invalid project names leave saved metadata untouched");
        string preview=ProjectLibrary.PreviewPath(p)!;using(var small=Cv2.ImRead(preview))Check(small.Width==192&&small.Height==108,"unstitched projects get a small cached preview from their first capture");
        DateTime modified=File.GetLastWriteTimeUtc(preview);
        Check(ProjectLibrary.PreviewPath(p)==preview&&File.GetLastWriteTimeUtc(preview)==modified&&SHA256.HashData(File.ReadAllBytes(Path.Combine(folder,"captures/a.png"))).SequenceEqual(rawHash),"preview requests reuse the thumbnail and leave original pixels unchanged");
        p.Dzi="mosaic-test.dzi";p.Width=4000;p.Height=2000;
        string level=Path.Combine(folder,"mosaic-test_files","8");Directory.CreateDirectory(level);File.Copy(preview,Path.Combine(level,"0_0.jpg"));
        Check(ProjectLibrary.PreviewPath(p)==Path.Combine(level,"0_0.jpg"),"stitched project previews come directly from the complete mosaic's small mip level");
        Check(ProjectLibrary.PreviewPath(new(){Id=id})==null,"empty projects have no preview");
        string keep=Path.Combine(Files.Root,"library-keep");Directory.CreateDirectory(keep);File.WriteAllText(Path.Combine(keep,"keep.txt"),"keep");
        string defaults=Path.Combine(Files.Root,"defaults.json");byte[] preferences=File.ReadAllBytes(defaults);
        string cache=Path.Combine(folder,"registration-cache");Directory.CreateDirectory(cache);File.WriteAllText(Path.Combine(cache,"features.bin"),"cache");
        foreach(string invalid in new[]{"../library-keep","..","","library-keep"})
        {
            try {ProjectLibrary.Delete(invalid);throw new Exception("Invalid project deletion accepted.");}catch(ArgumentException){}
        }
        Check(Directory.Exists(folder)&&File.ReadAllText(Path.Combine(keep,"keep.txt"))=="keep","deletion rejects traversal and folders that are not scans");
        ProjectLibrary.Delete(id);ProjectLibrary.Delete(id);
        Check(!Directory.Exists(folder)&&File.ReadAllText(Path.Combine(keep,"keep.txt"))=="keep"&&File.ReadAllBytes(defaults).SequenceEqual(preferences),"deletion removes captures, previews, mosaics, caches and metadata, preserving neighboring scans and defaults");
    }
}
