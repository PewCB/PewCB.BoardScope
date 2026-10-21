using BoardScope;
using System.Reflection;
using System.Text.Json;

static class PlatformChecks
{
    private static void Check(bool condition,string message){if(!condition)throw new Exception(message);Console.WriteLine("PASS "+message);}
    private static void Reject(Action action){try{action();}catch(ArgumentException){return;}throw new Exception("Invalid platform/scan settings accepted.");}
    public static async Task Run()
    {
        var center=new PlatformSettings();
        Check(center.Center()==new XY(100,60),"initial platform center is machine X100 Y60");
        foreach(double value in new[]{double.NaN,double.PositiveInfinity,100001.0})
        {
            Reject(()=>(center with{BoardCenterX=value}).Validate());
            Reject(()=>(center with{BoardCenterY=value}).Validate());
        }
        var region=new ScanSettings{BoardWidth=20,BoardHeight=16,FovWidth=8,FovHeight=6,RegionOffsetX=7,RegionOffsetY=-4,MaxTravelX=100,MaxTravelY=100};
        Check(center.ScanCenter(region)==new XY(107,64),"scan offsets use image right/down while the platform center stays in machine coordinates");
        foreach(double angle in new[]{-35.0,0,27.0,90.0})foreach(bool swap in new[]{false,true})foreach(int xs in new[]{-1,1})foreach(int ys in new[]{-1,1})
        {
            var s=region with{StageRotationDegrees=angle,SwapStageAxes=swap,StageXSign=xs,StageYSign=ys};
            var origin=center.ScanCenter(s);var mapping=PlacementSettings.From(s);
            var offset=mapping.Pixels(origin.X-100,origin.Y-60,800,600);
            Check(Math.Abs(offset.X-700)<1e-7&&Math.Abs(offset.Y+400)<1e-7,"scan-center offset round-trips the configured axis mapping");
            foreach(var p in Planner.Create(s).Points)
            {
                var image=mapping.Pixels(origin.X+p.StageDx-100,origin.Y+p.StageDy-60,800,600);
                if(Math.Abs(image.X-(p.U-10+7)*100)>1e-7||Math.Abs(image.Y-(p.V-8-4)*100)>1e-7)throw new Exception("Scan target applied the center or offset twice.");
            }
        }
        var tiny=region with{BoardWidth=1,BoardHeight=1,RegionOffsetX=0,RegionOffsetY=0,MaxTravelX=1,MaxTravelY=1,RowApproachMm=.5};
        Check(Planner.Create(tiny with{RegionOffsetX=1}).Points.Count==1,"offset inside the platform travel envelope is allowed");
        Reject(()=>Planner.Create(tiny with{RegionOffsetX=1.01}));
        Reject(()=>Planner.Create(tiny with{RegionOffsetX=-.6}));
        Reject(()=>Planner.Create(tiny with{RegionOffsetY=1.01}));
        Reject(()=>Planner.Create(tiny with{RegionOffsetX=double.NaN}));
        Check(true,"travel limits include the shifted scan region and row preload");

        string previous=Files.Root;Files.Root=Path.Combine(Path.GetTempPath(),"boardscope-platform-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(Files.Root);
        try
        {
            var store=new DefaultsStore();
            Check(store.LoadPlatform()==center,"missing defaults use the initial board center");
            var prefs=new AppDefaults{Scan=region,JogStep=5,CameraDeviceId="kept-camera",Port="COM7"};store.Save(prefs);
            var custom=center with{BoardCenterX=98.25,BoardCenterY=61.125};store.SavePlatform(custom);
            var restored=new DefaultsStore().Load()!;
            Check(restored.Platform==custom&&restored.Scan==region&&restored.JogStep==5&&restored.CameraDeviceId=="kept-camera"&&restored.Port=="COM7","saving the platform center survives restart and preserves other preferences");
            Reject(()=>store.SavePlatform(center with{BoardCenterX=double.NaN}));
            Check(store.LoadPlatform()==custom,"invalid center cannot overwrite the saved destination");
            var legacy=JsonSerializer.Deserialize<AppDefaults>("{\"scan\":{\"referencePoint\":\"top-right\"}}",Files.Json)!;
            Check(legacy.Platform==center&&legacy.Scan.ReferencePoint=="top-right","existing defaults and reference conventions remain readable");
            var project=new ScanProject{Platform=custom,Reference=custom.ScanCenter(region),Settings=region};
            var copy=JsonSerializer.Deserialize<ScanProject>(JsonSerializer.Serialize(project,Files.Json),Files.Json)!;
            Check(copy.Platform==custom&&copy.Reference==project.Reference&&copy.Settings.RegionOffsetX==7,"dataset metadata retains the platform and computed scan center independently");
            using var camera=new CameraService();using var motion=new MotionService();
            var engine=new ScanEngine(camera,motion,new Stitcher(),store);engine.Init();
            try{await engine.MoveBoardCenterAsync();throw new Exception("Disconnected center move accepted.");}
            catch(InvalidOperationException){Check(!engine.Busy,"disconnected center move is rejected without starting a job");}
            try{await engine.MoveBoardCenterAsync(double.NaN);throw new Exception("Invalid feed accepted.");}catch(ArgumentException){}
            var epoch=typeof(ScanEngine).GetField("_positionEpoch",BindingFlags.Instance|BindingFlags.NonPublic)!;
            int before=(int)epoch.GetValue(engine)!;
            typeof(MotionService).GetMethod("LosePosition",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(motion,null);
            Check((int)epoch.GetValue(engine)! == before+1,"coordinate loss still invalidates in-progress position-dependent operations");
        }
        finally{Files.Root=previous;}
    }
}
