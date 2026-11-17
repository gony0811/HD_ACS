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
    public int? TotalTasks { get; set; }              // 진행률 분모 — 시작 시 큐 전개 시점 고정 [SAIGE §6.1]. null=구버전 run
    public int ExcludedTasks { get; set; }            // 큐에서 제외된 TASK 수 (T_W_D 부재 등, 분모 미포함) [SAIGE §6.4]
    /// <summary>리페어 run의 검사 대상 용접선(taskId json 배열). null = 일반 run(시나리오 영역 전체).
    /// 큐 전개와 재개(resume) 시 정차 재계산이 이 목록의 용접선만 싣는다.</summary>
    public string? TaskIds { get; set; }
    public int? Round { get; set; }   // 가스검사 회차 1|2 → 지령 params.round (NULL = 미지정·용접검사)
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
    public Guid? WorkItemId { get; set; }             // greedy 배차 시 소속 작업항목 (완료/실패 집계 키)
    public string ActionType { get; set; } = "";
    public string BlockingType { get; set; } = "HARD";
    public string? Params { get; set; }               // jsonb
    public string Status { get; set; } = "WAITING";
    public string? Result { get; set; }               // jsonb — 촬영 성공/실패 [ADR-004]
    public int Attempts { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;   // 재시도 시 최신 액션 판별
}

/// <summary>
/// 코봇 이동 시험(moveToSeamStart) 발행 기록 [VDA §8.7]. 시험도 본검사와 같은 taskId 별 누적 attempt 를 받으므로
/// (작업자가 시험 자세에서 수동 촬영할 때 그 taskId·attempt 를 비전에 싣는다), 발급한 번호를 여기에 남겨
/// 다음 시도(본검사·시험 공통)가 그보다 큰 번호를 받게 한다. run·order_action 과 무관(검사 결과·진행률에 들어가지 않는다).
/// </summary>
public class SeamTestEntity
{
    public Guid TestId { get; set; }
    public Guid TaskId { get; set; }
    public string RobotId { get; set; } = "";
    public string OrderId { get; set; } = "";
    public int Attempt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// 검사 작업 큐 항목 [greedy 최근접 배차]. run 스코프 — 시작 시 선창 영역에서 전개.
/// 한 항목 = 영역 1개(정차 1곳) + 그 영역의 작업(용접선) 액션들. 좌표는 맵 프레임(로봇 보고와 동일).
/// </summary>
public class WorkItemEntity
{
    public Guid WorkItemId { get; set; }
    public Guid RunId { get; set; }
    public Guid AreaId { get; set; }                  // 원본 ref.inspection_area
    public string MapId { get; set; } = "";           // 층 (예: CT1-L2) — 큐 필터 키
    public double X { get; set; }                      // 맵 프레임 정차 x
    public double Y { get; set; }                      // 맵 프레임 정차 y
    public double? Theta { get; set; }                 // 맵 프레임 정차각
    public int Seq { get; set; }                       // 안정 정렬/동률 tiebreak (영역 sort_order)
    public string Status { get; set; } = "PENDING";    // PENDING | DISPATCHED | DONE | FAILED | SKIPPED
    public int Attempts { get; set; }
    public string? Actions { get; set; }               // jsonb — 이 정차의 액션 payload 배열(발행 시 사용)
    public string? OrderId { get; set; }               // 현재/최근 배차 orderId (state 대조)
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
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

/// <summary>
/// 가스 측정 결과 1건(용접선 1구간의 검사 1회) — 가스검사 AMR 이 액션 종료 때 보낸 본문과 세이지 전송 상태.
/// 세이지가 내려가 있어도 잃지 않도록 받은 즉시 저장하고, 전송은 따로 돈다(보낼 때까지 PENDING).
/// </summary>
public class GasResultEntity
{
    public Guid ResultId { get; set; }
    public Guid ActionId { get; set; }                // run.order_action — 액션 1개 = 결과 1건(중복 저장 방지 키)
    public Guid RunId { get; set; }
    public Guid? TaskId { get; set; }                 // ACS 내부 taskId(용접선)
    public string? TankId { get; set; }
    public string RobotId { get; set; } = "";
    public int? Round { get; set; }                   // 검사 회차 1|2
    public string Result { get; set; } = "";          // OK | NG | ERROR
    public int PointCount { get; set; }
    public string Body { get; set; } = "{}";          // jsonb — AMR 이 보낸 본문 그대로
    public string SendState { get; set; } = "PENDING";   // PENDING | SENT | REJECTED(세이지가 형식 거부 — 재전송 안 함)
    public int SendAttempts { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? SentAt { get; set; }
}
