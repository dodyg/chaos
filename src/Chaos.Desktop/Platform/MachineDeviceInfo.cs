using Chaos.Application.Abstractions;

namespace Chaos.Desktop.Platform;

internal sealed class MachineDeviceInfo : IDeviceInfo
{
    public string Name => Environment.MachineName;
}
