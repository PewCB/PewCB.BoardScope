using OpenCvSharp;

namespace BoardScope;

// Two mechanical offsets per tile and ONE scale/angle correction to the stage
// mapping. Image pixels never change scale or orientation between captures.
// Soft priors distinguish systematic calibration error from mechanical slack;
// neither tile 1 nor any other tile is fixed. Matrix-free PCG bounds memory.
public static class SimilaritySolver
{
    private sealed record Equation(int[] Indices,double[] Values,double Target,double Weight);
    public static TileRegistration[] Solve(IReadOnlyList<Tile> tiles,IReadOnlyList<FeatureLink> links,RegistrationOptions options,double pixelsPerMm,CancellationToken ct=default,Action<int,string>? progress=null)
        => SolveLayout(tiles,links,options,pixelsPerMm,ct,progress).Tiles;
    public static RegistrationSolution SolveLayout(IReadOnlyList<Tile> tiles,IReadOnlyList<FeatureLink> links,RegistrationOptions options,double pixelsPerMm,CancellationToken ct=default,Action<int,string>? progress=null)
    {
        if(tiles.Count==0)throw new ArgumentException("No tiles to refine.");
        int g=tiles.Count*2,n=g+2;
        var centres=tiles.Select(t=>new XY(t.X+t.Width/2d,t.Y+t.Height/2d)).ToArray();
        // Unsupported captures do not distort the calibration prior or its pivot.
        var supported=links.SelectMany(l=>new[]{l.First,l.Second}).Distinct().ToArray();
        double pivotX=supported.Length==0?centres.Average(c=>c.X):supported.Average(i=>centres[i].X);
        double pivotY=supported.Length==0?centres.Average(c=>c.Y):supported.Average(i=>centres[i].Y);
        double radius=Math.Max(1,supported.Length==0?1:supported.Max(i=>double.Hypot(centres[i].X-pivotX,centres[i].Y-pivotY)));
        double limit=options.MaxShiftMm*pixelsPerMm;
        double huber=3/options.ImageFactor(tiles[0].Width,tiles[0].Height);
        var equations=new List<Equation>();
        foreach(var link in links)
        {
            var i=tiles[link.First];var j=tiles[link.Second];
            double dx=centres[link.First].X-centres[link.Second].X,dy=centres[link.First].Y-centres[link.Second].Y;
            int a=link.First*2,b=link.Second*2;
            equations.Add(new([a,b,g,g+1],[1,-1,dx/radius,-dy/radius],j.X+link.Q.X-i.X-link.P.X,link.Weight));
            equations.Add(new([a+1,b+1,g,g+1],[1,-1,dy/radius,dx/radius],j.Y+link.Q.Y-i.Y-link.P.Y,link.Weight));
        }
        double positionSigma=Math.Max(1,limit/3);
        var priors=new double[n];
        for(int i=0;i<g;i++)priors[i]=1/(positionSigma*positionSigma);
        // Let systematic stage errors enter the shared calibration rather than
        // shrink it toward zero and distribute the same error over every tile.
        // Tiny regularization keeps evidence-free directions finite/unchanged.
        priors[g]=priors[g+1]=1e-10;
        var solution=new double[n];var weights=equations.Select(e=>e.Weight).ToArray();
        for(int iteration=0;iteration<5;iteration++)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Invoke(iteration,$"Shared stage scale/angle + tile positions · robust pass {iteration+1}/5");
            var diagonal=(double[])priors.Clone();var rhs=new double[n];
            for(int r=0;r<equations.Count;r++)
            {
                var e=equations[r];double w=weights[r];
                for(int k=0;k<e.Indices.Length;k++){int v=e.Indices[k];diagonal[v]+=w*e.Values[k]*e.Values[k];rhs[v]+=w*e.Values[k]*e.Target;}
            }
            void Multiply(double[] x,double[] y)
            {
                for(int k=0;k<n;k++)y[k]=priors[k]*x[k];
                for(int r=0;r<equations.Count;r++)
                {
                    var e=equations[r];double value=0;
                    for(int k=0;k<e.Indices.Length;k++)value+=e.Values[k]*x[e.Indices[k]];
                    value*=weights[r];
                    for(int k=0;k<e.Indices.Length;k++)y[e.Indices[k]]+=e.Values[k]*value;
                }
            }
            ConjugateGradient(solution,rhs,diagonal,Multiply,ct);
            for(int i=0;i<tiles.Count;i++)
            {
                int k=2*i;double shift=double.Hypot(solution[k],solution[k+1]);
                if(shift>limit){solution[k]*=limit/shift;solution[k+1]*=limit/shift;}
            }
            double a=1+solution[g]/radius,b=solution[g+1]/radius;
            double scale=Math.Clamp(double.Hypot(a,b),1-options.MaxScalePercent/100,1+options.MaxScalePercent/100);
            double angle=Math.Clamp(Math.Atan2(b,a),-options.MaxRotationDegrees*Math.PI/180,options.MaxRotationDegrees*Math.PI/180);
            solution[g]=(scale*Math.Cos(angle)-1)*radius;solution[g+1]=scale*Math.Sin(angle)*radius;
            for(int r=0;r<equations.Count;r+=2)
            {
                double error=double.Hypot(Residual(equations[r],solution),Residual(equations[r+1],solution));
                double robust=Math.Min(1,huber/Math.Max(.001,error));
                weights[r]=equations[r].Weight*robust;weights[r+1]=equations[r+1].Weight*robust;
            }
        }
        double sharedA=1+solution[g]/radius,sharedB=solution[g+1]/radius;
        var shared=new SharedStageCorrection(double.Hypot(sharedA,sharedB),Math.Atan2(sharedB,sharedA)*180/Math.PI,pivotX,pivotY);
        bool sharedLimited=Math.Abs(shared.RotationDegrees)>=options.MaxRotationDegrees*.98||Math.Abs(shared.Scale-1)>=options.MaxScalePercent/100*.98;
        var transforms=tiles.Select((tile,i)=>
        {
            double x=centres[i].X-pivotX,y=centres[i].Y-pivotY;
            return new Similarity(1,0,tile.X+(sharedA-1)*x-sharedB*y+solution[2*i],tile.Y+sharedB*x+(sharedA-1)*y+solution[2*i+1]);
        }).ToArray();
        var registrations=tiles.Select((tile,i)=>
        {
            var connected=links.Where(l=>l.First==i||l.Second==i).ToArray();
            int count=connected.Select(l=>l.First==i?l.Second:l.First).Distinct().Count();
            double error=connected.Length==0?0:Median(connected.Select(l=>Error(transforms[l.First],transforms[l.Second],l)).ToArray());
            var t=transforms[i];
            bool limited=double.Hypot(solution[2*i],solution[2*i+1])>=limit*.98||sharedLimited;
            return new TileRegistration(t,t.X-tile.X,t.Y-tile.Y,count,error,count==0?"shared-stage":limited?"limit-reached":"refined")
                {MechanicalDeltaX=solution[2*i],MechanicalDeltaY=solution[2*i+1]};
        }).ToArray();
        return new(registrations,shared);
    }
    public static double Error(Similarity first,Similarity second,FeatureLink link)
    {var p=first.Apply(link.P.X,link.P.Y);var q=second.Apply(link.Q.X,link.Q.Y);return double.Hypot(p.X-q.X,p.Y-q.Y);}
    public static double Median(double[] values){if(values.Length==0)return 0;Array.Sort(values);return values[values.Length/2];}
    private static double Residual(Equation e,double[] x){double value=-e.Target;for(int i=0;i<e.Indices.Length;i++)value+=e.Values[i]*x[e.Indices[i]];return value;}
    private static void ConjugateGradient(double[] x,double[] rhs,double[] diagonal,Action<double[],double[]> multiply,CancellationToken ct)
    {
        int n=x.Length;var r=new double[n];var z=new double[n];var p=new double[n];var product=new double[n];multiply(x,product);
        for(int k=0;k<n;k++){r[k]=rhs[k]-product[k];z[k]=r[k]/diagonal[k];p[k]=z[k];}
        double rz=Dot(r,z),tolerance=Math.Max(1e-20,Dot(rhs,rhs)*1e-12);
        for(int iteration=0;iteration<400&&Dot(r,r)>tolerance;iteration++)
        {
            if(iteration%10==0)ct.ThrowIfCancellationRequested();multiply(p,product);double denominator=Dot(p,product);
            if(!double.IsFinite(denominator)||denominator<=1e-25)break;
            double alpha=rz/denominator;
            for(int k=0;k<n;k++){x[k]+=alpha*p[k];r[k]-=alpha*product[k];z[k]=r[k]/diagonal[k];}
            double next=Dot(r,z),beta=next/Math.Max(1e-30,rz);rz=next;
            for(int k=0;k<n;k++)p[k]=z[k]+beta*p[k];
        }
        if(x.Any(v=>!double.IsFinite(v)))throw new InvalidOperationException("Similarity optimization did not converge to finite positions.");
    }
    private static double Dot(double[] a,double[] b){double sum=0;for(int i=0;i<a.Length;i++)sum+=a[i]*b[i];return sum;}
}
