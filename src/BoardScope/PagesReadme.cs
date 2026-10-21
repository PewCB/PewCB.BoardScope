using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace BoardScope;

internal sealed record PagesListing(string Name,string Description,string PublicUrl);

internal static class PagesReadme
{
    private const string Start="<!-- boardscope-scan-index:start -->",End="<!-- boardscope-scan-index:end -->";
    internal static readonly UTF8Encoding Utf8=new(false);
    public static PagesListing Describe(string name,string html,string? description=null,string? publicUrl=null)
    {
        int end=html.IndexOf("</head>",StringComparison.OrdinalIgnoreCase);string head=end<0?html:html[..end];
        string? canonical=null,summary=null;
        foreach(Match tag in Regex.Matches(head,"<(?:meta|link)\\b[^>]*>",RegexOptions.IgnoreCase,TimeSpan.FromSeconds(1))){
            var attrs=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
            foreach(Match attr in Regex.Matches(tag.Value,"([a-zA-Z:]+)\\s*=\\s*(?:\"([^\"]*)\"|'([^']*)')",RegexOptions.None,TimeSpan.FromSeconds(1)))attrs[attr.Groups[1].Value]=WebUtility.HtmlDecode(attr.Groups[2].Success?attr.Groups[2].Value:attr.Groups[3].Value);
            if(attrs.GetValueOrDefault("rel")?.Equals("canonical",StringComparison.OrdinalIgnoreCase)==true)canonical=attrs.GetValueOrDefault("href");
            string? key=attrs.GetValueOrDefault("name")??attrs.GetValueOrDefault("property");
            if(key?.Equals("description",StringComparison.OrdinalIgnoreCase)==true)summary=attrs.GetValueOrDefault("content");
            if(canonical==null&&key?.Equals("og:url",StringComparison.OrdinalIgnoreCase)==true)canonical=attrs.GetValueOrDefault("content");
        }
        string address=(publicUrl??canonical??"").Trim();
        if(!Uri.TryCreate(address,UriKind.Absolute,out var url)||url.Scheme is not ("http" or "https")||url.Query!=""||url.Fragment!=""||url.UserInfo!="")throw new ArgumentException("Enter the public HTTP or HTTPS page address before exporting.");
        var builder=new UriBuilder(url);if(builder.Path.EndsWith("/index.html",StringComparison.OrdinalIgnoreCase))builder.Path=builder.Path[..^10];if(!builder.Path.EndsWith('/'))builder.Path+="/";
        string text=(description??summary??$"Explore the {name} PCB in a high-resolution microscope scan. Pan and zoom to inspect components, solder joints and traces.").Trim();
        return new(name,text,builder.Uri.AbsoluteUri);
    }
    public static string Board(PagesListing page)=>$"# {Escape(page.Name)}\n\n{page.Description}\n\n[Open interactive scan](<{page.PublicUrl}>)\n\nPan and zoom to explore the stitched microscope mosaic. Image tiles load as needed.\n";
    private static string Escape(string text)=>Regex.Replace(text,@"([\\`*_{}\[\]<>()#!|~])",@"\$1");
    private static string Cell(string text)=>Escape(Regex.Replace(text,@"\s+"," ").Trim());
    private static List<PagesListing> Listings(string parent)
    {
        var pages=new List<PagesListing>();if(!Directory.Exists(parent))return pages;
        foreach(string folder in Directory.EnumerateDirectories(parent)){
            string name=Path.GetFileName(folder);if(name.StartsWith('.')||(File.GetAttributes(folder)&FileAttributes.ReparsePoint)!=0)continue;
            string index=Path.Combine(folder,"index.html");
            if(!File.Exists(index)||(File.GetAttributes(index)&FileAttributes.ReparsePoint)!=0||!Directory.EnumerateFiles(folder,"*.dzi").Any())continue;
            try{pages.Add(Describe(name,File.ReadAllText(index)));}catch(ArgumentException){/* Unrelated static pages do not belong in the scan index. */}
        }
        return pages;
    }
    public static string Index(string parent,PagesListing? pending=null)
    {
        var pages=Listings(parent);if(pending!=null){pages.RemoveAll(p=>p.Name.Equals(pending.Name,StringComparison.OrdinalIgnoreCase));pages.Add(pending);}
        var block=new StringBuilder(Start+"\n## Scan index\n\n| Board | Public viewer | Description |\n| --- | --- | --- |\n");
        foreach(var p in pages.OrderBy(p=>p.Name,StringComparer.OrdinalIgnoreCase))block.Append($"| [{Cell(p.Name)}](./{Uri.EscapeDataString(p.Name)}/README.md) | [Open scan](<{p.PublicUrl}>) | {Cell(p.Description)} |\n");
        block.Append(End);
        string readme=Path.Combine(parent,"README.md");PagesExport.RejectLinks(readme);
        string previous=File.Exists(readme)?File.ReadAllText(readme):"# PCB microscope scans\n\nHigh-resolution PCB scans with interactive tiled viewers.\n";
        int start=previous.IndexOf(Start,StringComparison.Ordinal),end=previous.IndexOf(End,StringComparison.Ordinal);
        if(start>=0&&end>start)return previous[..start]+block+previous[(end+End.Length)..];
        if(start>=0||end>=0)throw new IOException("The generated scan index markers in README.md are incomplete. Restore the markers before exporting.");
        return previous.TrimEnd()+"\n\n"+block+"\n";
    }
    // One-time backfill also uses the exported HTML's canonical URL and description.
    // Existing board READMEs and text outside the root index remain untouched.
    public static int Backfill(string parent)
    {
        parent=Path.GetFullPath(parent);PagesExport.RejectLinks(parent);if(!Directory.Exists(parent))throw new DirectoryNotFoundException(parent);
        var pages=Listings(parent);string index=Index(parent);int count=0;
        foreach(var page in pages){string path=Path.Combine(parent,page.Name,"README.md");PagesExport.RejectLinks(path);if(File.Exists(path))continue;File.WriteAllText(path,Board(page),Utf8);count++;}
        AtomicWrite(Path.Combine(parent,"README.md"),index);return count;
    }
    private static void AtomicWrite(string path,string content)
    {
        string temp=Path.Combine(Path.GetDirectoryName(path)!,".boardscope-readme-"+Guid.NewGuid().ToString("N")+".tmp");
        try{File.WriteAllText(temp,content,Utf8);File.Move(temp,path,true);}finally{if(File.Exists(temp))File.Delete(temp);}
    }
}
