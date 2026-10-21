using BoardScope;
using OpenCvSharp;

static class CapturePreviewChecks
{
    public static void Run()
    {
        string folder=Path.Combine(Files.Root,"capture-preview-check");Directory.CreateDirectory(folder);
        using var frame=new Mat(2160,3840,MatType.CV_8UC3,new Scalar(30,80,180));
        byte[] before=frame.ImEncode(".png");
        var tile=new Tile {File="captures/tile-r000-c000.png",Width=3840,Height=2160};
        CapturePreview.Write(folder,tile,frame);
        using var preview=Cv2.ImRead(Path.Combine(folder,tile.PreviewFile!));
        if(preview.Width!=384 || preview.Height!=216 || !frame.ImEncode(".png").SequenceEqual(before))throw new Exception("Capture preview dimensions or source changed.");
        using var portrait=BoardScope.Stitcher.Orient(frame,new ScanSettings {Rotation=90});
        var rotated=new Tile {File="captures/tile-r000-c001.png"};CapturePreview.Write(folder,rotated,portrait);
        using var portraitPreview=Cv2.ImRead(Path.Combine(folder,rotated.PreviewFile!));
        if(portraitPreview.Width!=216 || portraitPreview.Height!=384)throw new Exception("Portrait preview aspect incorrect.");
        Console.WriteLine("PASS capture JPEG previews are small, preserve oriented aspect and leave original image data unchanged");
    }
}
