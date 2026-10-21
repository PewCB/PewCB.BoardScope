using System.Globalization;
using System.IO.Ports;

namespace BoardScope;

public sealed class MotionService : IDisposable
{
    private IMotionPort? _port;
    private readonly Func<MotionConnect, IMotionPort> _createPort;
    private readonly Func<string[]> _portNames;
    private CancellationTokenSource? _readerCancel;
    private Task? _reader;
    private readonly SemaphoreSlim _commandGate = new(1);
    private readonly object _sync = new();
    private TaskCompletionSource<string[]>? _ack;
    private List<string> _responses = [];
    private string _state = "Disconnected";
    private XY? _position;
    private XY? _wco;
    private long _statusSequence;
    private bool _inches;
    private bool _ready;
    private readonly Queue<string> _log = new();
    public MotionService() : this(request => new SerialMotionPort(request), SerialPort.GetPortNames) { }
    internal MotionService(Func<MotionConnect, IMotionPort> createPort, Func<string[]> portNames)
    {
        _createPort = createPort;
        _portNames = portNames;
    }
    public event Action? PositionLost;
    public bool Connected => _port?.IsOpen == true && _ready;
    public object Snapshot { get { lock (_sync) return new { connected = Connected, port = _port?.PortName, state = _state, position = _position, log = _log.ToArray() }; } }
    private void LosePosition()=>PositionLost?.Invoke();
    public async Task ConnectAsync(MotionConnect request)
    {
        Disconnect();
        if (!_portNames().Contains(request.Port, StringComparer.OrdinalIgnoreCase)) throw new ArgumentException("Serial port was not found. Refresh the port list.");
        if (request.Baud is < 9600 or > 921600) throw new ArgumentException("Unsupported baud rate.");
        _inches = request.StatusInches;
        _port = _createPort(request);
        _port.Open();
        _readerCancel = new();
        var readerToken = _readerCancel.Token;
        _reader = Task.Run(() => ReadLoop(readerToken));
        // USB serial opening may reboot an ESP32. Wait for a position report, not a fixed boot delay.
        try { await QueryCoreAsync(CancellationToken.None, 250); _ready = true; }
        catch { Disconnect(); throw; }
    }
    private void ReadLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var line = _port!.ReadLine().Trim();
                bool lost = false;
                lock (_sync)
                {
                    if (!line.StartsWith('<')) { _log.Enqueue(line); while (_log.Count > 50) _log.Dequeue(); }
                    if (line.StartsWith('<') && line.EndsWith('>'))
                    {
                        bool wasAlarm=_state.StartsWith("Alarm",StringComparison.OrdinalIgnoreCase);
                        ParseStatus(line);
                        if (!wasAlarm && _state.StartsWith("Alarm", StringComparison.OrdinalIgnoreCase)) lost=true;
                    }
                    else if (line == "ok") _ack?.TrySetResult(_responses.ToArray());
                    else if (line.StartsWith("error:") || line.StartsWith("ALARM:"))
                    {
                        _ack?.TrySetException(new InvalidOperationException("FluidNC: " + line));
                        if (line.StartsWith("ALARM:")) { _state = line; _position = null; lost = true; }
                    }
                    else if (line.StartsWith("Grbl") || line.Contains("FluidNC v")) { _position = null; _wco = null; _ready = false; _ack?.TrySetException(new IOException("Controller restarted. Reconnect and home the stage again.")); lost = true; }
                    else if (_ack != null) _responses.Add(line);
                }
                if (lost) LosePosition();
            }
            catch (TimeoutException) { }
            catch (Exception e) when (e is IOException or InvalidOperationException or UnauthorizedAccessException or OperationCanceledException)
            {
                if (!ct.IsCancellationRequested)
                {
                    lock (_sync) { _ready = false; _state = "Disconnected"; _position = null; _ack?.TrySetException(e); }
                    LosePosition();
                }
                break;
            }
        }
    }
    private XY ReadXY(string value)
    {
        var v = value.Split(',');
        double scale = _inches ? 25.4 : 1;
        return new(double.Parse(v[0], CultureInfo.InvariantCulture) * scale, double.Parse(v[1], CultureInfo.InvariantCulture) * scale);
    }
    private void ParseStatus(string line)
    {
        var parts = line[1..^1].Split('|'); _state = parts[0];
        var mpos = parts.FirstOrDefault(p => p.StartsWith("MPos:"));
        var wpos = parts.FirstOrDefault(p => p.StartsWith("WPos:"));
        var offset = parts.FirstOrDefault(p => p.StartsWith("WCO:"));
        if (offset != null) _wco = ReadXY(offset[4..]);
        if (mpos != null) _position = ReadXY(mpos[5..]);
        else if (wpos != null && _wco != null) { var p = ReadXY(wpos[5..]); _position = new(p.X + _wco.X, p.Y + _wco.Y); }
        _statusSequence++;
    }
    public void Realtime(string value)
    {
        if (_port?.IsOpen == true) { lock (_sync) _port.Write(value); }
    }
    public async Task<string[]> CommandAsync(string command, CancellationToken ct, TimeSpan? timeout = null, bool finishOnCancellation = false)
    {
        if (!Connected) throw new InvalidOperationException("Connect FluidNC first.");
        await _commandGate.WaitAsync(ct);
        try
        {
            Task<string[]> response;
            lock (_sync)
            {
                ct.ThrowIfCancellationRequested();
                if (!Connected) throw new InvalidOperationException("Connect FluidNC first.");
                _responses = [];
                _ack = new(TaskCreationOptions.RunContinuationsAsynchronously);
                response = _ack.Task;
                _log.Enqueue("> " + command);
                _port!.WriteLine(command);
            }
            // Once a move is transmitted, drain its acknowledgement even if
            // the scan stops. Abandoning it would desynchronise later commands.
            try { return await response.WaitAsync(timeout ?? TimeSpan.FromSeconds(10), finishOnCancellation ? CancellationToken.None : ct); }
            catch (Exception e) when (e is TimeoutException or OperationCanceledException)
            {
                // A late ack must never be assigned to a subsequent command.
                _ready = false; LosePosition(); throw;
            }
        }
        finally { lock (_sync) _ack = null; _commandGate.Release(); }
    }
    public async Task<XY> QueryAsync(CancellationToken ct)
    {
        if (!Connected) throw new InvalidOperationException("Connect FluidNC first.");
        return await QueryCoreAsync(ct, 30);
    }
    private async Task<XY> QueryCoreAsync(CancellationToken ct, int attempts)
    {
        long sequence; lock (_sync) sequence = _statusSequence;
        for (int attempt = 0; attempt < attempts; attempt++)
        {
            Realtime("?");
            await Task.Delay(100, ct);
            lock (_sync) if (_statusSequence > sequence && _position != null) return _position;
        }
        throw new TimeoutException("No machine position received. FluidNC must report MPos, or WPos plus WCO. Check serial settings and status units.");
    }
    public async Task<XY> IdlePositionAsync(CancellationToken ct)
    {
        var position = await QueryAsync(ct);
        lock (_sync) if (_state != "Idle") throw new InvalidOperationException("Controller must be Idle. Current state: " + _state);
        return position;
    }
    public async Task<XY> HomeAsync(CancellationToken ct)
    {
        await QueryAsync(ct);
        lock (_sync) if (_state != "Idle" && !_state.StartsWith("Alarm", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Stop the current move before homing. Current state: " + _state);
        // Homing replaces machine coordinates. Do not retain a board reference or WCO cache.
        lock (_sync) { _position = null; _wco = null; }
        LosePosition();
        try
        {
            var deadline = DateTimeOffset.UtcNow.AddMinutes(5);
            await CommandAsync("$H", ct, TimeSpan.FromMinutes(5));
            while (DateTimeOffset.UtcNow < deadline)
            {
                long sequence; lock (_sync) sequence = _statusSequence;
                Realtime("?");
                await Task.Delay(200, ct);
                lock (_sync)
                {
                    if (_statusSequence <= sequence) continue;
                    if (Connected && _state == "Idle" && _position != null) return _position;
                    if (_state.StartsWith("Alarm", StringComparison.OrdinalIgnoreCase) || _state.StartsWith("Hold"))
                        throw new InvalidOperationException("Homing interrupted: " + _state);
                }
            }
            throw new TimeoutException("Homing did not finish within five minutes. Reconnect FluidNC before continuing.");
        }
        catch
        {
            // FluidNC ignores feed hold during homing; soft reset aborts it.
            if (!ct.IsCancellationRequested) Realtime("\u0018"); // Stop already sent reset before cancelling.
            LosePosition();
            throw;
        }
    }
    public async Task<XY> MoveAsync(XY target, double feed, CancellationToken ct)
    {
        if (!double.IsFinite(target.X) || !double.IsFinite(target.Y) || !double.IsFinite(feed) || feed is < 1 or > 3000) throw new ArgumentException("Invalid motion target/feed.");
        await IdlePositionAsync(ct);
        ct.ThrowIfCancellationRequested();
        await CommandAsync($"G21 G90 G53 G1 X{Files.N(target.X)} Y{Files.N(target.Y)} F{Files.N(feed)}", ct, finishOnCancellation: true);
        var deadline = DateTimeOffset.UtcNow.AddMinutes(3);
        while (DateTimeOffset.UtcNow < deadline)
        {
            // Stop means no further moves, not a feed hold halfway through
            // this one. Keep ownership until the accepted move reaches Idle.
            var actual = await QueryAsync(CancellationToken.None);
            lock (_sync)
            {
                if (_state == "Idle" && Math.Abs(actual.X - target.X) <= .025 && Math.Abs(actual.Y - target.Y) <= .025)
                {
                    ct.ThrowIfCancellationRequested();
                    return actual;
                }
                if (_state.StartsWith("Alarm", StringComparison.OrdinalIgnoreCase) || _state.StartsWith("Hold")) throw new InvalidOperationException("Movement interrupted: " + _state);
            }
        }
        Realtime("!"); LosePosition();
        throw new TimeoutException("Stage did not reach the target. Feed hold sent; home the stage before scanning.");
    }
    public void Disconnect()
    {
        _readerCancel?.Cancel();
        if (_port?.IsOpen == true) _port.Close();
        try { _reader?.Wait(1000); } catch (AggregateException) { /* Closing a Windows serial read can cancel its native operation. */ }
        _port?.Dispose(); _port = null; _readerCancel?.Dispose(); _readerCancel = null; _reader = null;
        lock (_sync) { _ready = false; _state = "Disconnected"; _position = null; _wco = null; _ack?.TrySetException(new IOException("Controller disconnected.")); }
        LosePosition();
    }
    public void Dispose() => Disconnect();
}
