using OpenCvSharp;
using System.Text.Json;

namespace BoardScope;

// Total-degree four: 1, x, y, x², xy, y², ... y⁴, on sensor coordinates [-1,1].
public record LightBackground(string Id,DateTimeOffset Created,int Width,int Height,string CameraName,string? DeviceId,double[][] Coefficients,double[] Mean,double FitErrorPercent,double MinGain,double MaxGain)
{
    public void Validate()
    {
        if(!Guid.TryParseExact(Id,"N",out _)||Width<2||Height<2||Coefficients.Length!=3||Coefficients.Any(c=>c.Length!=15||c.Any(v=>!double.IsFinite(v)))||Mean.Length!=3||Mean.Any(v=>!double.IsFinite(v)||v<=0))throw new IOException("Invalid light background calibration.");
    }
    internal static double[] Terms(double x,double y)
    {
        var terms=new double[15];FillTerms(x,y,terms);return terms;
    }
    internal static void FillTerms(double x,double y,Span<double> terms)
    {
        ReadOnlySpan<double> xp=[1,x,x*x,x*x*x,x*x*x*x],yp=[1,y,y*y,y*y*y,y*y*y*y];int k=0;
        for(int degree=0;degree<=4;degree++)for(int j=0;j<=degree;j++)terms[k++]=xp[degree-j]*yp[j];
    }
    internal double Value(int channel,double x,double y)
    {
        Span<double> terms=stackalloc double[15];FillTerms(x,y,terms);double result=0;for(int k=0;k<15;k++)result+=Coefficients[channel][k]*terms[k];return result;
    }
    public static LightBackground Fit(Mat image,string cameraName="",string? deviceId=null)
    {
        if(image.Empty()||image.Type()!=MatType.CV_8UC3||image.Width<32||image.Height<32)throw new ArgumentException("Capture a color reference of at least 32 × 32 pixels.");
        // Inspect the full frame: a smooth fit cannot recover clipped white pixels.
        var clipped=new long[3];int imageWidth=image.Width,imageHeight=image.Height;
        unsafe{for(int y=0;y<imageHeight;y++){byte* row=(byte*)image.Ptr(y);for(int x=0;x<imageWidth;x++)for(int b=0;b<3;b++)if(row[3*x+b]>=250)clipped[b]++;}}
        if(clipped.Any(n=>n>image.Total()*.01))throw new ArgumentException("The white reference is overexposed. Reduce exposure until no channel clips, then capture again.");
        int w=Math.Min(64,image.Width),h=Math.Min(64,image.Height),count=w*h;
        using var small=new Mat();Cv2.Resize(image,small,new(w,h),0,0,InterpolationFlags.Area);
        var basis=new double[count][];var values=new double[count,3];
        for(int y=0;y<h;y++)for(int x=0;x<w;x++){
            int i=y*w+x;basis[i]=Terms(2*(x+.5)/w-1,2*(y+.5)/h-1);var v=small.At<Vec3b>(y,x);for(int b=0;b<3;b++)values[i,b]=v[b];
        }
        var coefficients=new double[3][];var means=new double[3];double squared=0;
        for(int b=0;b<3;b++){
            double[] weights=Enumerable.Repeat(1d,count).ToArray(),c=new double[15];
            for(int pass=0;pass<4;pass++){
                using var a=new Mat(count,15,MatType.CV_64F);using var target=new Mat(count,1,MatType.CV_64F);using var result=new Mat();
                for(int i=0;i<count;i++){double weight=Math.Sqrt(weights[i]);for(int k=0;k<15;k++)a.Set(i,k,basis[i][k]*weight);target.Set(i,0,values[i,b]*weight);}
                if(!Cv2.Solve(a,target,result,DecompTypes.SVD))throw new IOException("Could not fit the light background.");
                for(int k=0;k<15;k++)c[k]=result.At<double>(k,0);
                var residuals=Enumerable.Range(0,count).Select(i=>values[i,b]-Dot(c,basis[i])).ToArray();
                double median=Median(residuals),sigma=Math.Max(.5,1.4826*Median(residuals.Select(r=>Math.Abs(r-median)).ToArray()));
                for(int i=0;i<count;i++)weights[i]=Math.Min(1,1.5*sigma/Math.Max(1e-9,Math.Abs(residuals[i]-median)));
            }
            coefficients[b]=c;means[b]=Enumerable.Range(0,count).Average(i=>Dot(c,basis[i]));
            if(means[b]<16)throw new ArgumentException("The white reference is too dark. Increase illumination or exposure and capture again.");
            for(int i=0;i<count;i++)squared+=weights[i]*Math.Pow((values[i,b]-Dot(c,basis[i]))/means[b],2);
        }
        var model=new LightBackground(Guid.NewGuid().ToString("N"),DateTimeOffset.UtcNow,image.Width,image.Height,cameraName,deviceId,coefficients,means,100*Math.Sqrt(squared/(3*count)),1,1);
        double min=double.MaxValue,max=0;
        // Check edges as well as interior; polynomial extrapolation at the border
        // must remain positive and bounded before any frame can use this model.
        for(int y=0;y<=128;y++)for(int x=0;x<=128;x++)for(int b=0;b<3;b++){
            double v=model.Value(b,x/64d-1,y/64d-1),gain=means[b]/v;
            if(!double.IsFinite(gain)||gain is < .25 or > 4)throw new ArgumentException("The fitted background varies too much or becomes invalid near an edge. Use a clean, evenly diffusing white target and capture again.");
            min=Math.Min(min,gain);max=Math.Max(max,gain);
        }
        return model with{MinGain=min,MaxGain=max};
    }
    private static double Dot(double[] a,double[] b){double sum=0;for(int i=0;i<a.Length;i++)sum+=a[i]*b[i];return sum;}
    private static double Median(double[] a){Array.Sort(a);return (a[(a.Length-1)/2]+a[a.Length/2])/2;}
}

public record LightBackgroundState(bool Enabled,LightBackground? Profile);
public record LightBackgroundEnabled(bool Enabled);

public static class LightBackgroundStore
{
    private static readonly object Gate=new();
    private static string StatePath=>Path.Combine(Files.Root,"light-background.json");
    public static LightBackgroundState Load(){lock(Gate){var state=File.Exists(StatePath)?JsonSerializer.Deserialize<LightBackgroundState>(File.ReadAllText(StatePath),Files.Json):null;state?.Profile?.Validate();return state??new(false,null);}}
    public static LightBackgroundState Enable(bool enabled){lock(Gate){var state=Load();if(enabled&&state.Profile==null)throw new InvalidOperationException("Capture a light background first.");state=state with{Enabled=enabled};Files.Save(StatePath,state);return state;}}
    public static LightBackgroundState Capture(Mat image,string name,string? deviceId)
    {
        var profile=LightBackground.Fit(image,name,deviceId);string folder=ProfileFolder(profile.Id);Directory.CreateDirectory(folder);
        try{
            if(!Cv2.ImWrite(Path.Combine(folder,"reference.png"),image))throw new IOException("Could not save the light reference.");
            Files.Save(Path.Combine(folder,"model.json"),profile);
            var state=new LightBackgroundState(true,profile);lock(Gate)Files.Save(StatePath,state);return state;
        }catch{ProjectLibrary.DeleteEntry(folder,Path.GetFullPath(Files.Root));throw;}
    }
    private static string ProfileFolder(string id){if(!Guid.TryParseExact(id,"N",out _))throw new ArgumentException("Invalid light background ID.");return Path.Combine(Files.Root,"light-backgrounds",id);}
    public static byte[] Preview(string id,string kind)
    {
        var state=Load();var model=state.Profile;if(model==null||model.Id!=id)throw new ArgumentException("Light background was replaced; refresh the page.");
        using var raw=Cv2.ImRead(Path.Combine(ProfileFolder(id),"reference.png"));if(raw.Empty())throw new IOException("Light reference is missing.");
        using var image=new Mat();double scale=Math.Min(1,640d/Math.Max(raw.Width,raw.Height));Cv2.Resize(raw,image,new((int)Math.Round(raw.Width*scale),(int)Math.Round(raw.Height*scale)),0,0,InterpolationFlags.Area);
        if(kind is not ("reference" or "fit" or "corrected"))throw new ArgumentException("Choose reference, fit or corrected preview.");
        int width=image.Width,height=image.Height;
        for(int y=0;y<height;y++)for(int x=0;x<width;x++){
            double nx=2*(x+.5)/width-1,ny=2*(y+.5)/height-1;var pixel=image.At<Vec3b>(y,x);
            for(int b=0;b<3;b++){double value=model.Value(b,nx,ny);if(kind=="fit")pixel[b]=(byte)Math.Clamp(Math.Round(value),0,255);else if(kind=="corrected")pixel[b]=(byte)Math.Clamp(Math.Round(pixel[b]*model.Mean[b]/value),0,255);}
            image.Set(y,x,pixel);
        }
        return image.ImEncode(".jpg");
    }
    public static void SelectForStitch(ScanProject p)
    {
        var state=Load();p.LightBackground=state.Enabled?state.Profile:null;
        if(p.LightBackground is {} model){using var correction=new LightCorrection(model,p.Settings);foreach(var tile in p.Tiles)correction.CheckSize(tile.Width,tile.Height);if(p.Camera!=null&&model.CameraName!=""&&p.Camera.Name!=model.CameraName)throw new InvalidOperationException("The light background belongs to another camera. Capture a matching background or disable light correction.");}
    }
}

// A single reusable sensor gain grid is oriented exactly like the saved captures.
// All corrections happen in source coordinates, before registration/warping.
internal sealed class LightCorrection:IDisposable
{
    private readonly LightBackground _model;
    private readonly ScanSettings _settings;
    private Mat? _gain;
    public string Key {get;}
    public static LightCorrection? For(ScanProject p)=>p.LightBackground is {} model?new(model,p.Settings):null;
    public LightCorrection(LightBackground model,ScanSettings settings){model.Validate();_model=model;_settings=settings;Key=$"light-{model.Id}-{settings.Rotation}-{(settings.FlipX?1:0)}{(settings.FlipY?1:0)}";}
    public void CheckSize(int width,int height){bool rotated=_settings.Rotation%180!=0;if(width!=(rotated?_model.Height:_model.Width)||height!=(rotated?_model.Width:_model.Height))throw new InvalidOperationException("Frame size does not match the light background. Capture a background in this camera mode or disable light correction.");}
    private Mat Gains()
    {
        if(_gain!=null)return _gain;
        int width=_model.Width,height=_model.Height;using var raw=new Mat(height,width,MatType.CV_32FC3);Span<double> terms=stackalloc double[15];
        unsafe{for(int y=0;y<height;y++){float* row=(float*)raw.Ptr(y);for(int x=0;x<width;x++){
            LightBackground.FillTerms(2*(x+.5)/width-1,2*(y+.5)/height-1,terms);
            for(int b=0;b<3;b++){double value=0;for(int k=0;k<15;k++)value+=_model.Coefficients[b][k]*terms[k];double gain=_model.Mean[b]/value;if(!double.IsFinite(gain)||gain is <.25 or >4)throw new IOException("Invalid light background gain.");row[3*x+b]=(float)gain;}
        }}}
        _gain=Stitcher.Orient(raw,_settings);return _gain;
    }
    public unsafe void Apply(Mat image,CancellationToken ct=default)
    {
        CheckSize(image.Width,image.Height);if(image.Type()!=MatType.CV_8UC3)throw new ArgumentException("Light correction requires a color frame.");var gain=Gains();
        int width=image.Width,height=image.Height;
        for(int y=0;y<height;y++){if(y%32==0)ct.ThrowIfCancellationRequested();byte* pixels=(byte*)image.Ptr(y);float* gains=(float*)gain.Ptr(y);for(int x=0;x<width*3;x++)pixels[x]=(byte)Math.Clamp(Math.Floor(pixels[x]*gains[x]+.5),0,255);}
    }
    public void Dispose()=>_gain?.Dispose();
    public static Mat Read(string path,ImreadModes mode,LightCorrection? correction)
    {
        var image=Cv2.ImRead(path,correction==null?mode:ImreadModes.Color);if(image.Empty())return image;
        try{if(correction!=null){correction.Apply(image);if(mode==ImreadModes.Grayscale)Cv2.CvtColor(image,image,ColorConversionCodes.BGR2GRAY);}return image;}catch{image.Dispose();throw;}
    }
    public static string ViewPath(Tile t,LightCorrection? correction)=>"tile-views/"+(correction==null?"":correction.Key+"/")+Path.GetFileNameWithoutExtension(t.File)+".dzi";
}

public sealed partial class ScanEngine
{
    public Task<LightBackgroundState> CaptureLightBackgroundAsync(CancellationToken ct)=>EditAsync(async()=>
    {
        if(!camera.Info.Connected)throw new InvalidOperationException("Open the microscope camera first.");
        var info=camera.Info;var controls=await camera.PropertiesAsync();
        if(controls.Any(p=>p.Auto&&p.Name is "Exposure" or "WhiteBalance" or "Gain"))throw new InvalidOperationException("Lock automatic exposure, gain and white balance before capturing the light background.");
        using var sum=new Mat(info.Height,info.Width,MatType.CV_32FC3,Scalar.All(0));using var converted=new Mat();
        for(int i=0;i<8;i++){using var frame=await camera.FreshAsync(i==0?3:1,ct);if(frame.Width!=info.Width||frame.Height!=info.Height)throw new InvalidOperationException("Camera mode changed during background capture.");frame.ConvertTo(converted,MatType.CV_32FC3);Cv2.Add(sum,converted,sum);}
        using var average=new Mat();sum.ConvertTo(average,MatType.CV_8UC3,1/8d);
        return await Task.Run(()=>{ct.ThrowIfCancellationRequested();return LightBackgroundStore.Capture(average,info.Name,camera.DeviceId);},ct);
    });
    public Task<LightBackgroundState> EnableLightBackgroundAsync(bool enabled)=>EditAsync(()=>Task.FromResult(LightBackgroundStore.Enable(enabled)));
}
