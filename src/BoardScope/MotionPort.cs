using System.IO.Ports;

namespace BoardScope;

internal interface IMotionPort : IDisposable
{
    bool IsOpen { get; }
    string PortName { get; }
    void Open();
    void Close();
    string ReadLine();
    void Write(string text);
    void WriteLine(string text);
}

internal sealed class SerialMotionPort(MotionConnect request) : IMotionPort
{
    // Native USB CDC needs DTR; RTS must not hold the ESP in reset.
    private readonly SerialPort _port = new(request.Port, request.Baud)
    {
        ReadTimeout = 250, WriteTimeout = 2000, NewLine = "\n",
        DtrEnable = true, RtsEnable = false
    };
    public bool IsOpen => _port.IsOpen;
    public string PortName => _port.PortName;
    public void Open() => _port.Open();
    public void Close() => _port.Close();
    public string ReadLine() => _port.ReadLine();
    public void Write(string text) => _port.Write(text);
    public void WriteLine(string text) => _port.WriteLine(text);
    public void Dispose() => _port.Dispose();
}
