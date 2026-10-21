namespace BoardScope;

public record PlatformSettings
{
    public double BoardCenterX { get; init; } = 100;
    public double BoardCenterY { get; init; } = 60;
    public void Validate()
    {
        if(!double.IsFinite(BoardCenterX)||!double.IsFinite(BoardCenterY)||Math.Abs(BoardCenterX)>100000||Math.Abs(BoardCenterY)>100000)
            throw new ArgumentException("Enter finite board-center machine coordinates in millimetres.");
    }
    public XY Center(){Validate();return new(BoardCenterX,BoardCenterY);}
    public XY ScanCenter(ScanSettings settings)
    {
        Validate();settings.Validate();
        var offset=Planner.GridToStage(settings,settings.RegionOffsetX,settings.RegionOffsetY);
        return new(BoardCenterX+offset.X,BoardCenterY+offset.Y);
    }
}
public record MoveBoardCenterRequest(double Feed=500);
