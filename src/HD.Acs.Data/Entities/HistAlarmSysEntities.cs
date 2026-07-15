namespace HD.Acs.Data.Entities;

// hist / alarm / sys 스키마

public class TransitionLogEntity
{
    public long Id { get; set; }
    public Guid MissionId { get; set; }
    public string FromState { get; set; } = "";
    public string ToState { get; set; } = "";
    public string Trigger { get; set; } = "";
    public string? Payload { get; set; }              // jsonb
    public DateTimeOffset OccurredAt { get; set; } = DateTimeOffset.UtcNow;
}

public class InspectionResultEntity
{
    public Guid ResultId { get; set; }
    public Guid RunId { get; set; }
    public Guid MissionId { get; set; }
    public Guid? PointId { get; set; }
    public Guid? TaskId { get; set; }
    public string RobotId { get; set; } = "";
    public string NodeId { get; set; } = "";
    public string ActionType { get; set; } = "";
    public string Position { get; set; } = "{}";      // 검사 S/W 대조 키 [ADR-004, Q2]
    public string Status { get; set; } = "";          // SUCCESS | FAILED | SKIPPED
    public int Attempts { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
}

public class AlarmSpecEntity
{
    public string AlarmCode { get; set; } = "";
    public string Severity { get; set; } = "WARNING"; // INFO | WARNING | CRITICAL
    public string Title { get; set; } = "";
    public string? Description { get; set; }
}

public class AlarmEntity
{
    public Guid AlarmId { get; set; }
    public string AlarmCode { get; set; } = "";
    public string? RobotId { get; set; }
    public Guid? MissionId { get; set; }
    public string? Detail { get; set; }               // jsonb
    public DateTimeOffset RaisedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ClearedAt { get; set; }    // NULL = 활성
    public string? ClearedBy { get; set; }
}

public class AppUserEntity
{
    public string UserId { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string Role { get; set; } = "VIEWER";      // ADMIN | OPERATOR | VIEWER
    public bool IsActive { get; set; } = true;
    public bool MustChangePassword { get; set; } = true;
}

public class AuditLogEntity
{
    public long Id { get; set; }
    public string UserId { get; set; } = "";
    public string Action { get; set; } = "";          // MANUAL_ZONE_CHANGE | EMERGENCY_STOP …
    public string? Target { get; set; }
    public string? Detail { get; set; }               // jsonb
    public DateTimeOffset OccurredAt { get; set; } = DateTimeOffset.UtcNow;
}
