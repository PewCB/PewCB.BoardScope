using BoardScope;
using OpenCvSharp;

static class PagesExportChecks
{
    static void Check(bool condition,string message){if(!condition)throw new Exception(message);Console.WriteLine("PASS "+message);}
    static void Reject(Action action){try{action();throw new Exception("Invalid export accepted");}catch(Exception e)when(e is ArgumentException or InvalidOperationException){}}
    public static void Run(){
        const string id="pages-export-check";string folder=Files.ProjectPath(id);Directory.CreateDirectory(Path.Combine(folder,"captures"));
        using(var image=new Mat(360,640,MatType.CV_8UC3,new Scalar(20,70,180)))Cv2.ImWrite(Path.Combine(folder,"captures/a.png"),image);
        var p=new ScanProject{Id=id,Name="CoreXY",Settings=new(){FovWidth=10,FovHeight=5.625},Tiles=[new(){File="captures/a.png",Width=640,Height=360}]};
        new BoardScope.Stitcher().Run(p,(_,_,_,_)=>{},CancellationToken.None);
        string metadata=File.ReadAllText(Path.Combine(folder,"project.json")),dzi=File.ReadAllText(Path.Combine(folder,p.Dzi!));
        string parent=Path.Combine(Path.GetTempPath(),"boardscope-pages-export-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(parent);File.WriteAllText(Path.Combine(parent,"README.md"),"# My scans\n\nKeep this introduction.\n");
        const string url="https://pewcb.github.io/PewCB.GigaPixelScans/CoreXY/",description="A CoreXY PCB & microscope scan.";
        string html=$"<!doctype html><head><title>CoreXY</title><meta content=\"A CoreXY PCB &amp; microscope scan.\" name=\"description\"><link href=\"{url}\" rel=\"canonical\"></head>";
        var request=new PagesExportRequest(parent,"CoreXY",p.Dzi!,html,Description:description,PublicUrl:url);
        var result=PagesExport.Save(id,request);
        Check(File.ReadAllText(result.Index)==request.Html&&File.ReadAllText(Path.Combine(result.Folder,p.Dzi!))==dzi&&Directory.Exists(Path.Combine(result.Folder,Path.GetFileNameWithoutExtension(p.Dzi)+"_files")),"Pages export creates the named board folder with index.html and the exact selected pyramid");
        using(var preview=Cv2.ImRead(Path.Combine(result.Folder,"preview.jpg")))Check(preview.Width==1200&&preview.Height==630,"Pages social preview is a 1200 × 630 raster image");
        using(var thumbnail=Cv2.ImRead(Path.Combine(result.Folder,"thumbnail.jpg")))Check(thumbnail.Width==512&&thumbnail.Height==512,"Pages export includes a separate square board thumbnail");
        string boardReadme=File.ReadAllText(Path.Combine(result.Folder,"README.md")),main=File.ReadAllText(Path.Combine(parent,"README.md"));
        Check(boardReadme.Contains("# CoreXY")&&boardReadme.Contains(description)&&boardReadme.Contains($"[Open interactive scan](<{url}>)")&&main.Contains("Keep this introduction.")&&main.Contains("./CoreXY/README.md")&&main.Contains(url),"board README has its description/public link and the root scan index preserves existing prose");
        string secondUrl="https://pewcb.github.io/PewCB.GigaPixelScans/Weather%20Station/";
        PagesExport.Save(id,request with{BoardName="Weather Station",Description="A board | ready.",PublicUrl=secondUrl,Html=$"<!doctype html><head><meta name='description' content='A board | ready.'><link rel='canonical' href='{secondUrl}'></head>"});
        main=File.ReadAllText(Path.Combine(parent,"README.md"));File.AppendAllText(Path.Combine(parent,"README.md"),"\nKeep this footer.\n");
        Check(main.Contains("./Weather%20Station/README.md")&&main.Contains("A board \\| ready.")&&main.Contains(url)&&main.Contains(secondUrl),"main README indexes every scan with encoded local paths and public URLs, escaping table descriptions");
        File.WriteAllText(Path.Combine(result.Folder,"keep.txt"),"previous export");
        Reject(()=>PagesExport.Save(id,request));
        Check(File.Exists(Path.Combine(result.Folder,"keep.txt")),"an existing board export is preserved unless replacement is explicitly selected");
        var replaced=PagesExport.Save(id,request with{ReplaceExisting=true});
        Check(replaced.Backup!=null&&File.ReadAllText(Path.Combine(replaced.Backup,"keep.txt"))=="previous export"&&File.Exists(replaced.Index),"replacement publishes a complete new board and retains the complete previous folder as a backup");
        main=File.ReadAllText(Path.Combine(parent,"README.md"));
        Check(main.Split("./CoreXY/README.md").Length==2&&main.Contains(secondUrl)&&main.Contains("Keep this footer.")&&!main.Contains("backup-"),"re-export replaces its index row without duplicates, preserves outside text and excludes backups");
        File.Delete(Path.Combine(result.Folder,"README.md"));string siblingReadme=Path.Combine(parent,"Weather Station","README.md");File.WriteAllText(siblingReadme,"User notes stay here.");
        Check(PagesReadme.Backfill(parent)==1&&File.ReadAllText(Path.Combine(result.Folder,"README.md")).Contains(description)&&File.ReadAllText(siblingReadme)=="User notes stay here."&&PagesReadme.Backfill(parent)==0,"retroactive backfill reads canonical URLs and decoded HTML descriptions, preserves existing READMEs and is idempotent");
        foreach(string badUrl in new[]{"file:///C:/scan/","javascript:alert(1)",url+"?secret=1",url+"#view"})Reject(()=>PagesExport.Save(id,request with{BoardName="BadUrl",PublicUrl=badUrl}));
        Check(PagesReadme.Describe("CoreXY",html).PublicUrl==url&&PagesReadme.Describe("CoreXY",html).Description==description,"older clients can supply the description and public URL through viewer metadata");
        string rootReadme=Path.Combine(parent,"README.md"),marker=Path.Combine(result.Folder,"rollback.txt");
        File.WriteAllText(marker,"Keep this published board.");main=File.ReadAllText(rootReadme);File.SetAttributes(rootReadme,FileAttributes.ReadOnly);
        try{try{PagesExport.Save(id,request with{ReplaceExisting=true});throw new Exception("Read-only main README overwritten");}catch(UnauthorizedAccessException){}}
        finally{File.SetAttributes(rootReadme,FileAttributes.Normal);}
        Check(File.ReadAllText(marker)=="Keep this published board."&&File.ReadAllText(rootReadme)==main&&!Directory.GetFiles(parent,".boardscope-readme-*").Any(),"a failed root README update rolls back the board replacement and preserves the published files");
        foreach(string name in new[]{"../bad","CON",".git","trailing."})Reject(()=>PagesExport.Save(id,request with{BoardName=name}));
        Reject(()=>PagesExport.Save(id,request with{ParentFolder="relative"}));
        Reject(()=>PagesExport.Save(id,request with{ParentFolder=Files.Root}));
        Reject(()=>PagesExport.Save(id,request with{ParentFolder=Path.GetDirectoryName(Path.GetDirectoryName(Files.Root))!,BoardName=Path.GetFileName(Path.GetDirectoryName(Files.Root))!}));
        Reject(()=>PagesExport.Save(id,request with{Dzi="captures/a.png"}));
        Check(File.ReadAllText(Path.Combine(folder,"project.json"))==metadata&&File.ReadAllText(Path.Combine(folder,p.Dzi!))==dzi,"export validation rejects unsafe names, unrelated maps and library destinations without changing the source dataset");
        string missing=Directory.GetFiles(Path.Combine(folder,Path.GetFileNameWithoutExtension(p.Dzi)+"_files", "10"))[0];
        byte[] bytes=File.ReadAllBytes(missing);File.Delete(missing);
        main=File.ReadAllText(Path.Combine(parent,"README.md"));
        try{PagesExport.Save(id,request with{BoardName="Incomplete"});throw new Exception("Missing preview tile accepted");}catch(IOException){}finally{File.WriteAllBytes(missing,bytes);}
        Check(!Directory.Exists(Path.Combine(parent,"Incomplete"))&&!Directory.GetDirectories(parent,".boardscope-export-*").Any()&&!Directory.GetFiles(parent,".boardscope-readme-*").Any()&&File.ReadAllText(Path.Combine(parent,"README.md"))==main,"a failed export removes staging files and leaves both the board folder and root index unchanged");
    }
}
