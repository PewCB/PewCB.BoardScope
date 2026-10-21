using OpenCvSharp;

namespace BoardScope;

public static class CapturePreview
{
    public static void Write(string folder, Tile tile, Mat frame)
    {
        string relative = $"previews/{Path.GetFileNameWithoutExtension(tile.File)}.jpg";
        string path = Path.Combine(folder, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        double scale = Math.Min(1, 384.0 / Math.Max(frame.Width, frame.Height));
        using var small = new Mat();
        Cv2.Resize(frame, small, new Size(Math.Max(1, (int)Math.Round(frame.Width * scale)), Math.Max(1, (int)Math.Round(frame.Height * scale))), 0, 0, InterpolationFlags.Area);
        if (!Cv2.ImWrite(path, small, [new ImageEncodingParam(ImwriteFlags.JpegQuality, 75)]))
            throw new IOException("Unable to save capture preview.");
        tile.PreviewFile = relative;
    }
}
