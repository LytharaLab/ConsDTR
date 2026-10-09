using TorchSharp;
using static TorchSharp.torch;

namespace ConsDTR.Lib.Models;

public static class DtrDevice
{
    /// <summary>Explicit cuda:N never silently falls back to the CPU.</summary>
    public static Device Resolve(string selection)
    {
        if (string.Equals(selection, "cpu", StringComparison.OrdinalIgnoreCase))
            return CPU;
        if (string.Equals(selection, "auto", StringComparison.OrdinalIgnoreCase))
            return cuda.is_available() ? CUDA : CPU;
        var parts = selection.ToLowerInvariant().Split(':');
        if (parts[0] != "cuda" || parts.Length > 2 ||
            (parts.Length == 2 && (!int.TryParse(parts[1], out var parsed) || parsed < 0)))
            throw new ArgumentException("Device must be cpu, auto, cuda or cuda:N.", nameof(selection));
        var index = parts.Length == 2 ? int.Parse(parts[1]) : 0;
        if (!cuda.is_available() || index >= cuda.device_count())
            throw new InvalidOperationException($"CUDA device {index} is unavailable.");
        return new Device(DeviceType.CUDA, index);
    }
}
