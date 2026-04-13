namespace HttpMonitor;

public class Message
{
    public string Text { get; set; } = string.Empty;
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime ReceivedAt { get; set; } = DateTime.Now;
}
