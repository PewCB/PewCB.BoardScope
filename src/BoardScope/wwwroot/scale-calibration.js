const ScaleCalibration = {
  calculate(first, second, rotation = 0) {
    if (!first?.point || !second?.point) throw Error('Confirm the same feature with the crosshair in both snapshots.');
    if (first.deviceId !== second.deviceId || first.width !== second.width || first.height !== second.height || first.epoch !== second.epoch)
      throw Error('Camera or stage coordinates changed. Take a new first snapshot.');
    const mm = Math.hypot(second.position.x-first.position.x, second.position.y-first.position.y);
    const pixels = Math.hypot(second.point.x-first.point.x, second.point.y-first.point.y);
    if (!Number.isFinite(mm) || mm < .05) throw Error('Jog at least 0.05 mm between snapshots.');
    if (!Number.isFinite(pixels) || pixels < 10) throw Error('Feature displacement is too small. Aim for at least 100 pixels for accuracy.');
    const mmPerPixel=mm/pixels;
    const width=(rotation%180?first.height:first.width)*mmPerPixel;
    const height=(rotation%180?first.width:first.height)*mmPerPixel;
    if(width<.05 || width>1000 || height<.05 || height>1000)throw Error('Calculated frame size is outside the supported range. Check the selected feature.');
    return {mm,pixels,mmPerPixel,width,height};
  }
};
if(typeof module !== 'undefined')module.exports=ScaleCalibration;
