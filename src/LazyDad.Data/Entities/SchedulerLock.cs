namespace LazyDad.Data.Entities;

public class SchedulerLock
{
    public string LockKey { get; set; } = string.Empty;
    public DateTime AcquiredAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public string HolderInstanceId { get; set; } = string.Empty;
}
