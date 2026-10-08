namespace CircuitRF.Core.Devices;

/// <summary>
/// A component's parameters cannot be read as the author meant them — two forms stated at once, a
/// key that would be ignored, a value outside its domain. Thrown by <see cref="ComponentModelFactory"/>,
/// which does not know which instance it is building; the elaborator, which does, rethrows it with the
/// instance path in front, so <c>check</c> and a run name the line as well as the keys.
/// </summary>
public sealed class ParameterRefusalException(string message) : InvalidOperationException(message);
