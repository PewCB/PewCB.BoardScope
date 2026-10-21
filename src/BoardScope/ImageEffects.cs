using OpenCvSharp;

namespace BoardScope;

public record ImageEffects
{
    public double Brightness { get; init; }
    public double Contrast { get; init; }
    public double Shadows { get; init; }
    public double Highlights { get; init; }
    public double Saturation { get; init; }
    public double Temperature { get; init; }
    public double Tint { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public bool Neutral=>Brightness==0&&Contrast==0&&Shadows==0&&Highlights==0&&Saturation==0&&Temperature==0&&Tint==0;
    public void Validate()
    {
        if(new[]{Brightness,Contrast,Shadows,Highlights,Saturation,Temperature,Tint}.Any(v=>!double.IsFinite(v)||v is < -100 or > 100))throw new ArgumentException("Image effects must be between −100 and 100.");
    }
}

// Keep this pixel transform identical to wwwroot/effect-math.js. Lookup tables
// make both the browser preview and bounded full-resolution blocks inexpensive.
public sealed class ImageEffectTransform
{
    private const int Steps=4096;
    private readonly double[] _red=new double[256],_green=new double[256],_blue=new double[256],_tone=new double[Steps+1];
    private readonly double _saturation,_contrast;
    public ImageEffectTransform(ImageEffects effects)
    {
        effects.Validate();double exposure=Math.Pow(2,effects.Brightness/50),temperature=effects.Temperature/100,tint=effects.Tint/100;
        double red=exposure*Math.Pow(2,.35*temperature+.12*tint),green=exposure*Math.Pow(2,-.24*tint),blue=exposure*Math.Pow(2,-.35*temperature+.12*tint);
        _saturation=1+effects.Saturation/100;_contrast=Math.Pow(2,effects.Contrast/100);
        for(int k=0;k<256;k++){_red[k]=k/255d*red;_green[k]=k/255d*green;_blue[k]=k/255d*blue;}
        for(int k=0;k<=Steps;k++)
        {
            double l=k/(double)Steps;
            _tone[k]=(l-.5)*_contrast+.5+.5*(effects.Shadows/100)*Math.Pow(1-l,3)+.5*(effects.Highlights/100)*Math.Pow(l,3);
        }
    }
    private static byte Byte(double value)=>(byte)Math.Clamp(Math.Floor(value*255+.5),0,255);
    public Vec3b Pixel(byte blue,byte green,byte red)
    {
        double r=_red[red],g=_green[green],b=_blue[blue],l=.2126*r+.7152*g+.0722*b;
        double index=Math.Clamp(l,0,1)*Steps;int k=(int)index;
        double target=_tone[k]+(_tone[Math.Min(k+1,Steps)]-_tone[k])*(index-k);
        // Extrapolate contrast outside the LUT rather than clipping bright
        // color channels before saturation and white-balance have been applied.
        if(l>1)target+=_contrast*(l-1);
        double offset=target-_saturation*l;
        return new(Byte(_saturation*b+offset),Byte(_saturation*g+offset),Byte(_saturation*r+offset));
    }
    public unsafe void Apply(Mat image,CancellationToken ct=default)
    {
        if(image.Type()!=MatType.CV_8UC3)throw new ArgumentException("Effects require an 8-bit color image.");
        int width=image.Width,height=image.Height;
        for(int y=0;y<height;y++)
        {
            if(y%32==0)ct.ThrowIfCancellationRequested();byte* row=(byte*)image.Ptr(y);
            for(int x=0;x<width;x++){int k=x*3;var color=Pixel(row[k],row[k+1],row[k+2]);row[k]=color.Item0;row[k+1]=color.Item1;row[k+2]=color.Item2;}
        }
    }
}
public record ApplyEffectsRequest(string StitchId,string MapId,ImageEffects Effects);
