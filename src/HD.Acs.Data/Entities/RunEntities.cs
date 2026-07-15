namespace HD.Acs.Data.Entities;

// run 스키마 — 런타임 (미션/Order 스냅샷/로봇 컨텍스트)

public class ScenarioRunEntity
{
    public Guid RunId { get; set; }
    public Guid ScenarioId { get; set; }
    public int ScenarioVer { get; set; }
    public string RobotId { get; set; } = "";
    public string State { get; set; } = "RUNNING";    // RUNNING | WAITING_FLOOR_TRANSFER | COMPLETED | ABORTED
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public List<MissionEntity> Missions { get; set; } = new();
}

public class MissionEntity
{
    public Guid MissionId { get; set; }
    public Guid RunId { get; set; }
    public int Seq { get; set; }                      // run 내 층 순서
    public string MapId { get; set; } = "";           // 한 미션 = 한 층
    public string RobotId { get; set; } = "";
    public string OrderId { get; set; } = "";
    public int OrderUpdateId { get; set; }
    public string State { get; set; } = "CREATED";
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
}

public class OrderNodeEntity
{
    public Guid MissionId { get; set; }
    public int SequenceId { get; set; }               // 짝수
    public string NodeId { get; set; } = "";
    public double X { get; set; }
    public double Y { get; set; }
    public double? Theta { get; set; }
    public bool Released { get; set; } = true;
    public string Status { get; set; } = "PENDING";   // PENDING | PASSED
}

public class OrderEdgeEntity
{
    public Guid MissionId { get; set; }
    public int SequenceId { get; set; }               // 홀수
    public string EdgeId { get; set; } = "";
    public string StartNodeId { get; set; } = "";
    public string EndNodeId { get; set; } = "";
    public bool Released { get; set; } = true;
}

public class OrderActionEntity
{
    public Guid ActionId { get; set; }                // state.actionStates 대조 키
    public Guid MissionId { get; set; }
    public int NodeSequenceId { get; set; }
    public Guid? TaskId { get; set; }
    public string ActionType { get; set; } = "";
    public string BlockingType { get; set; } = "HARD";
    public string? Params { get; set; }               // jsonb
    public string Status { get; set; } = "WAITING";
    public string? Result { get; set; }               // jsonb — 촬영 성공/실패 [ADR-004]
    public int Attempts { get; set; }
}

public class RobotContextEntity
{
    public string RobotId { get; set; } = "";
    public string? ManualMapId { get; set; }          // 작업자 수동 지정 층 [Q9]
    public string? ManualUpdatedBy { get; set; }
    public DateTimeOffset? ManualUpdatedAt { get; set; }
    public string? ReportedMapId { get; set; }        // 로봇 보고 층 — 릴리즈 가드 근거
    public double? ReportedX { get; set; }
    public double? ReportedY { get; set; }
    public double? ReportedTheta { get; set; }
    public double? BatteryPct { get; set; }
    public string? ConnectionState { get; set; }      // ONLINE | OFFLINE | CONNECTIONBROKEN
    public DateTimeOffset? ReportedAt { get; set; }
}
