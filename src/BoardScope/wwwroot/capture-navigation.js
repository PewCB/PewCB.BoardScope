const CaptureStageMapping=typeof module!=='undefined'?require('./jog-mapping.js'):JogMapping;
const CaptureNavigation = {
  pan(dx, dy, displayedWidth, displayedHeight, settings) {
    if (![dx,dy,displayedWidth,displayedHeight,settings.fovWidth,settings.fovHeight].every(Number.isFinite) || displayedWidth <= 0 || displayedHeight <= 0 || settings.fovWidth <= 0 || settings.fovHeight <= 0)
      throw Error('Calibrate frame width before dragging the image.');
    // Dragging the image right moves the board opposite to grid-right.
    const u = -dx * settings.fovWidth / displayedWidth;
    const v = -dy * settings.fovHeight / displayedHeight;
    let {x,y}=CaptureStageMapping.imageToStage(u,v,settings);
    const ratio = Math.min(1, 10 / Math.max(Math.abs(x), Math.abs(y)));
    return {x: x * ratio || 0, y: y * ratio || 0};
  },
  fit(rawWidth, rawHeight, rotation, availableWidth, availableHeight) {
    const rotated = rotation % 180 !== 0;
    const factor = Math.min(availableWidth / (rotated ? rawHeight : rawWidth), availableHeight / (rotated ? rawWidth : rawHeight));
    return {width: rawWidth * factor, height: rawHeight * factor};
  },
  tileRect(project, tile) {
    const s = project.settings;
    let x = tile.nominalX * s.fovWidth / tile.width;
    let y = tile.nominalY * s.fovHeight / tile.height;
    if (project.reference) {
      const position=CaptureStageMapping.stageToImage(tile.stageX-project.reference.x,tile.stageY-project.reference.y,s);
      const refX = s.referencePoint === 'center' ? s.boardWidth/2 : s.referencePoint.endsWith('right') ? s.boardWidth : 0;
      const refY = s.referencePoint === 'center' ? s.boardHeight/2 : s.referencePoint.startsWith('bottom') ? s.boardHeight : 0;
      x = position.x + refX - s.fovWidth/2;
      y = position.y + refY - s.fovHeight/2;
    }
    return {x:x/s.boardWidth, y:y/s.boardWidth, width:s.fovWidth/s.boardWidth, height:s.fovHeight/s.boardWidth};
  }
};

// Keep only the latest pointer target; never queue hardware commands.
class StagePanController {
  constructor(move, changed, failed) { this.move=move;this.changed=changed;this.failed=failed;this.session=null;this.inFlight=false; }
  get busy() { return !!this.session || this.inFlight; }
  start() { if(this.busy)return false;this.session={target:{x:0,y:0},sent:{x:0,y:0},released:false};this.changed();return true; }
  target(value) { if(!this.session || this.session.released)return;this.session.target=value;this.flush(); }
  release() { if(!this.session)return;this.session.released=true;this.flush(); }
  cancel() { this.session=null;this.changed(); }
  async flush() {
    const s=this.session;if(!s || this.inFlight)return;
    const dx=s.target.x-s.sent.x,dy=s.target.y-s.sent.y;
    if(Math.max(Math.abs(dx),Math.abs(dy))<.005) { if(s.released){this.session=null;this.changed();}return; }
    // Short moves keep the camera responsive and bound any in-flight motion.
    const ratio=Math.min(1, 1/Math.max(Math.abs(dx),Math.abs(dy)));
    const delta={x:dx*ratio,y:dy*ratio};
    this.inFlight=true;this.changed();
    try { await this.move(delta);s.sent.x+=delta.x;s.sent.y+=delta.y; }
    catch(error) { this.session=null;this.failed(error); }
    finally { this.inFlight=false;this.changed(); }
    if(this.session===s)this.flush();
  }
}
if(typeof module!=='undefined')module.exports={CaptureNavigation,StagePanController};
