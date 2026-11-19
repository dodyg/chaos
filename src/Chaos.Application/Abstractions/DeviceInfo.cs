namespace Chaos.Application.Abstractions;

/// <summary>What the Application layer may know about the machine it runs on; the shell supplies the real answer.</summary>
public interface IDeviceInfo
{
    /// <summary>The name people recognise this device by (for example, the one shown as the GitHub sync runner).</summary>
    string Name { get; }
}
