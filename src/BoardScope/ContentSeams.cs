using OpenCvSharp;

namespace BoardScope;

// Compute whole-overlap paths once, then reuse them in every output block.
// Bounded-resolution costs choose ownership only; rendered pixels are original
// full-resolution pixels and never mixed across sources.
internal sealed class ContentSeams
{
    private sealed record Cut(int First,int Second,Rect2d Bounds,bool Vertical,bool FirstLow,double[] Path);
    private readonly Dictionary<int,List<Cut>> _cuts=[];
    public int Loss(int tile,double x,double y)
    {
        if(!_cuts.TryGetValue(tile,out var cuts))return 0;int loss=0;
        foreach(var cut in cuts)
        {
            var r=cut.Bounds;if(x<r.X||y<r.Y||x>=r.Right||y>=r.Bottom)continue;
            double along=cut.Vertical?(y-r.Y)/r.Height:(x-r.X)/r.Width;
            double position=Math.Clamp(along*cut.Path.Length-.5,0,cut.Path.Length-1);int k=(int)position;
            double seam=cut.Path[k]+(cut.Path[Math.Min(k+1,cut.Path.Length-1)]-cut.Path[k])*(position-k);
            bool low=(cut.Vertical?x:y)<seam,winnerFirst=low==cut.FirstLow;
            if(winnerFirst!=(tile==cut.First))loss++;
        }
        return loss;
    }
    public static ContentSeams Build(ScanProject p,string folder,Action<string,int,int,string> progress,CancellationToken ct)
    {
        var plan=new ContentSeams();var candidates=new List<(int I,int J,Rect2d Bounds)>();
        var transforms=p.Tiles.Select(Similarity.For).ToArray();var bounds=p.Tiles.Select((t,i)=>transforms[i].Bounds(t.Width,t.Height)).ToArray();
        for(int i=0;i<p.Tiles.Count;i++)for(int j=i+1;j<p.Tiles.Count;j++)
        {
            var a=bounds[i];var b=bounds[j];double x=Math.Max(a.X,b.X),y=Math.Max(a.Y,b.Y),w=Math.Min(a.Right,b.Right)-x,h=Math.Min(a.Bottom,b.Bottom)-y;
            if(w>=16&&h>=16)candidates.Add((i,j,new(x,y,w,h)));
        }
        using var correction=LightCorrection.For(p);
        using var cache=new TranslationRegistrar.ImageCache(folder,correction);int done=0;
        foreach(var (i,j,r) in candidates)
        {
            ct.ThrowIfCancellationRequested();var a=p.Tiles[i];var b=p.Tiles[j];var ta=transforms[i];var tb=transforms[j];var ca=ta.Apply(a.Width/2d,a.Height/2d);var cb=tb.Apply(b.Width/2d,b.Height/2d);
            bool vertical=Math.Abs(cb.X-ca.X)/Math.Max(1,(a.Width+b.Width)/2d)>=Math.Abs(cb.Y-ca.Y)/Math.Max(1,(a.Height+b.Height)/2d);
            bool firstLow=vertical?ca.X<=cb.X:ca.Y<=cb.Y;
            double factor=Math.Min(1,512/Math.Max(r.Width,r.Height));int w=Math.Max(2,(int)Math.Ceiling(r.Width*factor)),h=Math.Max(2,(int)Math.Ceiling(r.Height*factor));
            using var pa=Sample(cache.Get(a),ta,r,w,h);using var pb=Sample(cache.Get(b),tb,r,w,h);
            using var ga=new Mat();using var gb=new Mat();Cv2.Laplacian(pa,ga,MatType.CV_32F);Cv2.Laplacian(pb,gb,MatType.CV_32F);
            int across=vertical?w:h,length=vertical?h:w;var cost=new float[length,across];
            for(int y=0;y<h;y++)for(int x=0;x<w;x++)
            {
                double difference=Math.Abs(pa.At<float>(y,x)-pb.At<float>(y,x));
                double detail=Math.Abs(ga.At<float>(y,x))+Math.Abs(gb.At<float>(y,x));
                int row=vertical?y:x,col=vertical?x:y;
                // Discourage cutting high-detail component edges and keep a
                // weak center preference in uniformly matching areas.
                cost[row,col]=(float)(difference+.35*detail+.015*Math.Abs(col-(across-1)/2d));
            }
            var indices=MinimumPath(cost,ct);var path=indices.Select(k=>vertical?r.X+(k+.5)*r.Width/w:r.Y+(k+.5)*r.Height/h).ToArray();
            var cut=new Cut(i,j,r,vertical,firstLow,path);
            if(!plan._cuts.TryGetValue(i,out var ai))plan._cuts[i]=ai=[];ai.Add(cut);
            if(!plan._cuts.TryGetValue(j,out var bj))plan._cuts[j]=bj=[];bj.Add(cut);
            progress("seams",++done,candidates.Count,"Finding content-aware sharp seams");
        }
        return plan;
    }
    private static Mat Sample(Mat source,Similarity transform,Rect2d r,int width,int height)
    {
        using var matrix=new Mat(2,3,MatType.CV_64F);
        double fx=width/r.Width,fy=height/r.Height;
        matrix.Set(0,0,transform.A*fx);matrix.Set(0,1,-transform.B*fx);matrix.Set(0,2,(transform.X-r.X)*fx-.5);
        matrix.Set(1,0,transform.B*fy);matrix.Set(1,1,transform.A*fy);matrix.Set(1,2,(transform.Y-r.Y)*fy-.5);
        using var warped=new Mat();Cv2.WarpAffine(source,warped,matrix,new(width,height),InterpolationFlags.Linear,BorderTypes.Replicate);
        var result=new Mat();warped.ConvertTo(result,MatType.CV_32F);return result;
    }
    internal static int[] MinimumPath(float[,] cost,CancellationToken ct=default)
    {
        int length=cost.GetLength(0),width=cost.GetLength(1);var back=new sbyte[length,width];var previous=new double[width];var next=new double[width];
        for(int x=0;x<width;x++)previous[x]=cost[0,x];
        for(int y=1;y<length;y++)
        {
            if(y%32==0)ct.ThrowIfCancellationRequested();
            for(int x=0;x<width;x++)
            {
                double best=previous[x];int dx=0;
                if(x>0&&previous[x-1]+.5<best){best=previous[x-1]+.5;dx=-1;}
                if(x+1<width&&previous[x+1]+.5<best){best=previous[x+1]+.5;dx=1;}
                next[x]=cost[y,x]+best;back[y,x]=(sbyte)dx;
            }
            (previous,next)=(next,previous);
        }
        int end=Enumerable.Range(0,width).MinBy(x=>previous[x]);var result=new int[length];result[^1]=end;
        for(int y=length-1;y>0;y--){end+=back[y,end];result[y-1]=end;}return result;
    }
}
