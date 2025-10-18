using System.Text.Json.Serialization;

namespace PresentMon.BridgeContracts;

public enum BridgeMessageType
{
	Heartbeat,
	StartMonitoringRequest,
	StopMonitoringRequest,
	Metrics,
	Ack,
	Error
}

public sealed class BridgeMessage
{
	public BridgeMessageType Type { get; init; }

	public string? RequestId { get; init; }

	public StartMonitoringPayload? Start { get; init; }

	public StopMonitoringPayload? Stop { get; init; }

	public BridgeMetricsPayload? Metrics { get; init; }

	public string? Error { get; init; }

	[JsonIgnore]
	public DateTime TimestampUtc { get; init; } = DateTime.UtcNow;

	public static BridgeMessage Ack(string? requestId = null) => new()
	{
		Type = BridgeMessageType.Ack,
		RequestId = requestId
	};

	public static BridgeMessage CreateError(string message, string? requestId = null) => new()
	{
		Type = BridgeMessageType.Error,
		RequestId = requestId,
		Error = message
	};
}

public sealed class StartMonitoringPayload
{
	public uint ProcessId { get; init; }

	public string ProcessName { get; init; } = string.Empty;
}

public sealed class StopMonitoringPayload
{
	public uint ProcessId { get; init; }
}

public sealed class BridgeMetricsPayload
{
	public float AverageFrameTimeMs { get; init; }
	public float FramesPerSecond { get; init; }
	public float OnePercentLowFps { get; init; }
	public float ZeroPointOnePercentLowFps { get; init; }
	public float GpuLatencyMs { get; init; }
	public float GpuTimeMs { get; init; }
	public float GpuBusyMs { get; init; }
	public float GpuWaitMs { get; init; }
	public float DisplayLatencyMs { get; init; }
	public float CpuBusyMs { get; init; }
	public float CpuWaitMs { get; init; }
	public float GpuUtilizationPercent { get; init; }
}
