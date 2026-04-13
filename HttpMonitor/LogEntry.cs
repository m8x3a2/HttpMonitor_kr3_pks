namespace HttpMonitor;

public class LogEntry
{
    public DateTime Timestamp { get; set; }
    public string Method { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string StatusCode { get; set; } = string.Empty;
    public long ProcessingTimeMs { get; set; }
    public string? RequestBody { get; set; }
}
