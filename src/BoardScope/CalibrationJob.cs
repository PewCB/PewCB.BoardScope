using OpenCvSharp;

namespace BoardScope;

public sealed partial class ScanEngine
{
    private string? _calibrationId;
    private AutomaticCalibrationResult? _calibrationResult;
    private byte[][]? _calibrationImages;
    private bool _calibrationApproved;
    public object? CalibrationStatus {get {lock(_sync)return _calibrationId==null?null:new {id=_calibrationId,ready=_calibrationResult!=null,approved=_calibrationApproved};}}
    public AutomaticCalibrationResult CalibrationResult(string id){lock(_sync)return _calibrationResult is {} result && result.Id==id?result:throw new InvalidOperationException("Calibration result is not available.");}
    public byte[]? CalibrationImage(string id,int index){lock(_sync)return id==_calibrationId && _calibrationImages!=null && index is 0 or 1?_calibrationImages[index]:null;}
    public Task<string> StartAutomaticCalibrationAsync(AutomaticCalibrationRequest request) => EditAsync(async () =>
    {
        var settings=request.Settings;settings.Validate();
        if(request.Direction is not (1 or -1))throw new ArgumentException("Choose X+ or X− for calibration.");
        var info=camera.Info;if(!info.Connected || !motion.Connected)throw new InvalidOperationException("Connect the camera and FluidNC before calibrating.");
        var start=await motion.IdlePositionAsync(CancellationToken.None);
        var context=new CalibrationContext(camera.DeviceId,info.Width,info.Height,_positionEpoch,settings);
        XY? reference=_defaults.LoadPlatform().Center();
        AutomaticCalibration.Target(start,settings,-request.Direction*settings.RowApproachMm,reference);
        AutomaticCalibration.Target(start,settings,request.Direction*.5,reference);
        string id=Guid.NewGuid().ToString("N");
        _jobCancel?.Dispose();_jobCancel=new();var ct=_jobCancel.Token;
        lock(_sync){_calibrationId=id;_calibrationResult=null;_calibrationImages=null;_calibrationApproved=false;_state=new(true,"calibrating",0,6,"Taking up slack before the first calibration pair…",null,null);}
        _job=Task.Run(async () =>
        {
            try
            {
                void CheckContext(){ct.ThrowIfCancellationRequested();var c=camera.Info;lock(_sync)if(context.PositionEpoch!=_positionEpoch || !c.Connected || c.Width!=context.Width || c.Height!=context.Height || camera.DeviceId!=context.DeviceId)throw new InvalidOperationException("Camera or stage coordinates changed. Start calibration again.");}
                async Task<XY> Move(double distance){CheckContext();var target=AutomaticCalibration.Target(start,settings,distance,reference);return await motion.MoveAsync(target,Math.Min(500,settings.Feed),ct);}
                async Task<(Mat Frame,XY Position)> Snapshot()
                {
                    CheckContext();var before=await motion.IdlePositionAsync(ct);await Task.Delay(settings.SettleMs,ct);
                    using var raw=await camera.FreshAsync(settings.DiscardFrames,ct);var after=await motion.IdlePositionAsync(ct);CheckContext();
                    if(double.Hypot(before.X-after.X,before.Y-after.Y)>.025 || raw.Width!=context.Width || raw.Height!=context.Height)throw new InvalidOperationException("Stage or camera changed during the snapshot.");
                    return (Stitcher.Orient(raw,settings),after);
                }
                if(settings.RowApproachMm>0){await Move(-request.Direction*settings.RowApproachMm);await Move(0);}
                var first=await Snapshot();using var frame1=first.Frame;
                Progress("calibrating",1,6,"First image saved · moving 0.5 mm");
                await Move(request.Direction*.5);var second=await Snapshot();using var frame2=second.Frame;
                Progress("calibrating",2,6,"Phase correlation of the 0.5 mm pair at full resolution…");
                var coarse=AutomaticCalibration.Match(frame1,frame2,first.Position,second.Position,request.Direction,settings.StageRotationDegrees,ct);
                double secondMove=coarse.WidthMm/2;
                var fineSettings=settings with {StageRotationDegrees=coarse.RotationDegrees};
                async Task<XY> FineMove(double distance){CheckContext();return await motion.MoveAsync(AutomaticCalibration.Target(second.Position,fineSettings,distance,reference??start),Math.Min(500,settings.Feed),ct);}
                AutomaticCalibration.Target(second.Position,fineSettings,request.Direction*secondMove,reference??start);
                AutomaticCalibration.Target(second.Position,fineSettings,-request.Direction*settings.RowApproachMm,reference??start);
                Progress("calibrating",3,6,$"Initial frame width {coarse.WidthMm:F4} mm · taking the second pair");
                if(settings.RowApproachMm>0){await FineMove(-request.Direction*settings.RowApproachMm);await FineMove(0);}
                var third=await Snapshot();using var frame3=third.Frame;
                Progress("calibrating",4,6,$"Moving {secondMove:F4} mm for approximately 50% overlap");
                await FineMove(request.Direction*secondMove);var fourth=await Snapshot();using var frame4=fourth.Frame;
                Progress("calibrating",5,6,"Refining scale and angle with phase correlation of the second pair…");
                var fine=AutomaticCalibration.Match(frame3,frame4,third.Position,fourth.Position,request.Direction,coarse.RotationDegrees,ct,new XY(request.Direction*frame3.Width/2d,0));
                AutomaticCalibration.ValidateFine(coarse,fine);CheckContext();
                var result=new AutomaticCalibrationResult(id,DateTimeOffset.UtcNow,frame3.Width,frame3.Height,context,coarse,fine,third.Position,fourth.Position);
                byte[][] images=[frame3.ImEncode(".png"),frame4.ImEncode(".png")];CheckContext();
                lock(_sync){_calibrationResult=result;_calibrationImages=images;_state=new(false,"calibrated",6,6,"Calibration ready. Compare the second pair and approve the scale and angle.",null,null);}
            }
            catch(Exception error)
            {
                bool stopped=error is OperationCanceledException;
                lock(_sync)_state=new(false,stopped?"stopped":"failed",0,6,stopped?"Calibration stopped.":error.Message,null,stopped?null:error.Message);
            }
        });
        return id;
    });
    public Task<CalibrationMeasurement> ApproveCalibrationAsync(string id,CalibrationApproval request) => EditAsync(() =>
    {
        var result=CalibrationResult(id);var c=camera.Info;var s=request.Settings;var previous=result.Context.Settings;
        s.Validate();
        lock(_sync)if(result.Context.PositionEpoch!=_positionEpoch)throw new InvalidOperationException("Stage coordinates changed. Run calibration again.");
        if(!c.Connected || camera.DeviceId!=result.Context.DeviceId || c.Width!=result.Context.Width || c.Height!=result.Context.Height ||
            s.Rotation!=previous.Rotation || s.FlipX!=previous.FlipX || s.FlipY!=previous.FlipY || s.StageXSign!=previous.StageXSign || s.StageYSign!=previous.StageYSign || s.SwapStageAxes!=previous.SwapStageAxes || s.StageRotationDegrees!=previous.StageRotationDegrees)
            throw new InvalidOperationException("Camera orientation or axis settings changed. Run calibration again.");
        lock(_sync)_calibrationApproved=true;
        return Task.FromResult(result.Fine);
    });
}
