using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using BoardScope;

internal static class MotionStopChecks
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        Console.WriteLine("PASS " + message);
    }
    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("Simulated controller did not reach the expected state.");
            await Task.Delay(10);
        }
    }
    private static async Task Cancelled(Task task)
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (OperationCanceledException) { return; }
        throw new Exception("Stopped move did not report cancellation after finishing.");
    }
    private static MotionService Service(FakePort port) => new(_ => port, () => [port.PortName]);

    public static async Task Run()
    {
        foreach (bool delayAck in new[] { false, true })
        {
            using var port = new FakePort { DelayAcknowledgement = delayAck };
            using var motion = Service(port);
            await motion.ConnectAsync(new(port.PortName));
            int lost = 0; motion.PositionLost += () => lost++;
            using var stop = new CancellationTokenSource();
            Task<XY> moving = motion.MoveAsync(new(1, 2), 300, stop.Token);
            await Until(() => port.MoveCount == 1);
            stop.Cancel();
            await Task.Delay(150);
            Check(!moving.IsCompleted && motion.Connected, $"Stop waits for the accepted move, including a delayed acknowledgement ({delayAck})");
            if (delayAck) port.Acknowledge();
            await Until(() => port.QueryCount >= 3);
            Check(!moving.IsCompleted && port.State == "Run", "Acknowledgement alone does not release the job while the stage is moving");
            port.FinishMove();
            await Cancelled(moving);
            Check(await motion.IdlePositionAsync(CancellationToken.None) == new XY(1, 2), "Stopped move reaches its endpoint and leaves a fresh Idle position");
            Check(lost == 0 && !port.Sent.Any(x => x.Contains('!') || x.Contains('\u0018')), "Normal cancellation sends no feed hold or reset and preserves machine coordinates");
            port.DelayAcknowledgement = false; port.AutoCompleteMoves = true;
            Check(await motion.MoveAsync(new(2, 3), 300, CancellationToken.None) == new XY(2, 3), "Next move works without reset, reconnect or homing");
        }

        using (var port = new FakePort())
        using (var motion = Service(port))
        {
            await motion.ConnectAsync(new(port.PortName));
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            await Cancelled(motion.MoveAsync(new(1, 0), 300, cancelled.Token));
            Check(port.MoveCount == 0 && motion.Connected, "A stop before submission sends no move and keeps the connection usable");
        }

        string previousRoot = Files.Root;
        Files.Root = Path.Combine(Path.GetTempPath(), "boardscope-stop-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Files.Root);
        try
        {
            using var port = new FakePort();
            using var motion = Service(port);
            using var camera = new CameraService();
            var engine = new ScanEngine(camera, motion, new Stitcher()); engine.Init();
            await motion.ConnectAsync(new(port.PortName));
            await engine.ReferenceAsync("center");
            await engine.MoveBoardCenterAsync();
            await Until(() => port.MoveCount == 1);
            engine.Stop(); engine.Stop();
            Check(engine.Busy && engine.State.Stopping, "Repeated Stop requests keep the job busy while its current move finishes");
            try { await engine.MoveBoardCenterAsync(); throw new Exception("A second job started before the stopping move finished."); }
            catch (InvalidOperationException) { Check(port.MoveCount == 1, "No new job or extra move is accepted while stopping"); }
            port.FinishMove();
            await Until(() => !engine.Busy);
            var snapshot = JsonSerializer.SerializeToElement(engine.Snapshot, Files.Json);
            Check(engine.State.Phase == "stopped" && engine.State.Error == null && port.State == "Idle", "Job reports Stopped only after the controller becomes Idle");
            Check(snapshot.GetProperty("reference").ValueKind != JsonValueKind.Null && !port.Sent.Any(x => x.Contains('!') || x.Contains('\u0018')), "Job cancellation and its exception handler preserve the reference and send no stop bytes");
            port.AutoCompleteMoves = true;
            await engine.MoveBoardCenterAsync();
            await Until(() => !engine.Busy);
            Check(engine.State.Phase == "centered" && port.MoveCount == 2, "A new stage job starts normally after a graceful stop");
            await engine.ShutdownAsync();
        }
        finally { Files.Root = previousRoot; }
    }

    private sealed class FakePort : IMotionPort
    {
        private readonly BlockingCollection<string> _incoming = new();
        private readonly object _sync = new();
        private XY _position = new(0, 0);
        private XY? _target;
        private string _state = "Idle";
        private volatile bool _open;
        private int _moveCount, _queryCount;
        public ConcurrentQueue<string> Sent { get; } = new();
        public bool DelayAcknowledgement { get; set; }
        public bool AutoCompleteMoves { get; set; }
        public int MoveCount => Volatile.Read(ref _moveCount);
        public int QueryCount => Volatile.Read(ref _queryCount);
        public string State { get { lock (_sync) return _state; } }
        public bool IsOpen => _open;
        public string PortName => "COM-SIMULATED";
        public void Open() => _open = true;
        public void Close() => _open = false;
        public string ReadLine()
        {
            if (!_open) throw new IOException("Simulated port closed.");
            return _incoming.TryTake(out var line, 100) ? line : throw new TimeoutException();
        }
        public void Write(string text)
        {
            Sent.Enqueue(text);
            if (text == "?")
            {
                lock (_sync) _incoming.Add($"<{_state}|MPos:{Files.N(_position.X)},{Files.N(_position.Y)},0|FS:0,0>");
                Interlocked.Increment(ref _queryCount);
            }
        }
        public void WriteLine(string text)
        {
            Sent.Enqueue(text);
            if (text.StartsWith("G21 G90 G53 G1 "))
            {
                var match = Regex.Match(text, @"X([-\d.]+) Y([-\d.]+)");
                if (!match.Success) throw new Exception("Unexpected simulated motion command.");
                lock (_sync)
                {
                    _target = new(double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture), double.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture));
                    _state = "Run";
                }
                Interlocked.Increment(ref _moveCount);
                if (AutoCompleteMoves) FinishMove();
            }
            if (!DelayAcknowledgement) Acknowledge();
        }
        public void Acknowledge() => _incoming.Add("ok");
        public void FinishMove()
        {
            lock (_sync)
            {
                _position = _target ?? throw new Exception("No simulated move is pending.");
                _target = null; _state = "Idle";
            }
        }
        public void Dispose() => Close();
    }
}
