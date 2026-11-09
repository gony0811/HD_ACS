namespace HD.Acs.UI.Models;

// ── 리치·간섭 시뮬레이션 — 서버 ReachSimService 응답 미러 (/api/reach-sim/…) ──

/// <summary>조사 — 영역 하나와 그 용접선 판정. 리프트 고정 조사면 고정 높이·용접선별 최적의 간섭 없음/도달 수(아니면 null).</summary>
public sealed record ReachSurveyAreaDto(Guid AreaId, string Name, string WallCode, int Level, double StationX, double StationY,
    List<ReachSurveySeamDto> Seams, double? FixedStrokeMm = null, int? BestClear = null, int? BestReachable = null);

/// <summary>조사 — 용접선 하나. Z 는 층 바닥 위[m], Verdict = Clear | Collision | Unreachable.
/// BestVerdict·BestStrokeMm = 리프트 고정 조사에서 이 용접선만 볼 때의 판정·높이.
/// TraverseMarginDeg = 도면 순회의 가장 빡빡한 관절 여유[도](구서버·순회 판정 꺼짐·도달 불가면 null).</summary>
public sealed record ReachSurveySeamDto(Guid TaskId, int Seq, double ZMin, double ZMax, string Verdict,
    double? StrokeMm, double? MinClearanceM, int YawIndex, string? Blocker = null,
    string? BestVerdict = null, double? BestStrokeMm = null, double? TraverseMarginDeg = null);

public sealed record ReachSeamLineDto(Guid TaskId, int Seq, double[] Start, double[] End);
public sealed record ReachBodyDto(double[][] Corners, double ClearanceM, string NearestPlane);
/// <summary>부품 — 캡슐(A·B·반경) 또는 상자(Corners 8개: 아래 4 → 위 4, 반경 0). 도면 m.</summary>
public sealed record ReachPartDto(string Name, double[] A, double[] B, double RadiusM, double ClearanceM, string NearestPlane,
    double[][]? Corners = null);
public sealed record ReachJointDto(string Name, double[] Position);
public sealed record ReachSolutionDto(double[] JointsDeg, bool PostureOk, double MarginDeg, string Limiting, bool WristDown,
    double MinClearanceM, double[] Focus, List<ReachPartDto> Parts, List<ReachJointDto> Joints);

/// <summary>자세 하나 — Stage = Approach(② 400) | Align(정렬 준비 300) | Focus(초점 검사 0).</summary>
public sealed record ReachStageDto(string Stage, double StandoffMm, double[] TcpTarget, int ChosenIndex,
    List<ReachSolutionDto> Solutions);

/// <summary>상세 — 정차(도면)·리프트·정차 이동·대상 점·영역 용접선·차체·세 자세. 좌표는 도면 m.</summary>
public sealed record ReachDetailDto(
    Guid AreaId, string AreaName, string WallCode, int Level, double FloorZ,
    double StationX, double StationY, double StationYaw, int YawCount, int YawIndex,
    double StrokeMm, double OffsetForwardMm, double OffsetLeftMm,
    Guid TaskId, int Point, double[] Target, List<ReachSeamLineDto> Seams,
    ReachBodyDto Body, List<ReachStageDto> Stages, double WarnClearanceM,
    ReachEntryDto? Entry = null, string? BranchChange = null, double[]? StartJointsDeg = null, double MountYawDeg = 0,
    bool ScanOnly = false, ReachTraversalDto? Traversal = null);

/// <summary>
/// 도면 순회 판정(HD_AMR ⑱ 경유점 사슬 검증과 같은 규칙) — 구서버·순회 판정 꺼짐이면 상세에 없다(null).
/// MinMarginDeg = 가장 빡빡한 관절 여유(해가 없으면 null), TiltDeg = 포괄 근사 ±기울기(실제 경유점을 쓰면 null).
/// </summary>
public sealed record ReachTraversalDto(bool Ok, string? Blocker, double? MinMarginDeg, string Limiting, double TwistDeg,
    double StandoffMm, double? TiltDeg, ReachTraversalPointDto? Worst, int States);

/// <summary>순회 판정 자세 하나 — 시작점에서의 거리 mm·기울기 도·TCP(도면 m)·관절각·여유.</summary>
public sealed record ReachTraversalPointDto(double AlongMm, double TiltDeg, double[] Tcp, double[] JointsDeg, double MarginDeg, string Limiting);

/// <summary>운영 ② 진입 경로(후퇴 평면 경유) 판정 — 출발 자세(홈 위치_2·ready)가 설정된 면만. 좌표 도면 m.</summary>
public sealed record ReachEntryDto(bool Ok, string Summary, double InPlaneMm, int Candidates,
    double[] StartTcp, double[]? Entry, double[] Plunge, List<string> Rejects);

/// <summary>
/// 영역 재분할 제안 — 묶음(작업·정차 후보·새 사각형·용접선 판정). Unsafe = 나눈 뒤에도 순회 여유를 확보 못 한 용접선 수(가망 없는 것 제외),
/// Hopeless = 어느 정차에서도 여유를 확보 못 한 작업. 묶음이 1개면 나누지 않아도 된다.
/// </summary>
public sealed record ReachSplitPlanDto(Guid AreaId, string Name, string WallCode, int Level, int Unsafe, List<Guid> Hopeless,
    int GridCells, long ElapsedMs, List<ReachSplitGroupDto> Groups);
public sealed record ReachSplitGroupDto(List<Guid> TaskIds, List<int> Seqs, ReachStationCandidateDto Station, double[][] Corners,
    List<ReachSplitSeamDto> Seams);
public sealed record ReachSplitSeamDto(Guid TaskId, int Seq, string Verdict, double? TraverseMarginDeg, string? Blocker);
/// <summary>재분할 적용 결과 — 영역마다 id·이름·층·작업 수·새로 만들었는지(첫 항목 = 원래 영역).</summary>
public sealed record AreaSplitResultDto(List<AreaSplitResultAreaDto> Areas);
public sealed record AreaSplitResultAreaDto(Guid AreaId, string Name, int Level, int TaskCount, bool Created);

/// <summary>정차점 최적화 — 영역 하나의 지금 정차와 찾은 정차. Overridden = 이미 정차 덮어쓰기가 있음.</summary>
public sealed record ReachStationOptimumDto(Guid AreaId, string Name, string WallCode, int Level, bool Overridden,
    ReachStationCandidateDto Current, ReachStationCandidateDto Best, int Evaluated, long ElapsedMs,
    List<ReachOptimumSeamDto>? Seams = null);

/// <summary>용접선 하나의 지금·찾은 정차 판정(Clear | Collision | Unreachable)과 찾은 정차에서 고른 리프트·도면 순회 관절 여유[도]·막힘 사유.</summary>
public sealed record ReachOptimumSeamDto(Guid TaskId, int Seq, string Current, string Best, double? BestStrokeMm,
    double? CurrentTraverseMarginDeg = null, double? BestTraverseMarginDeg = null, string? BestBlocker = null);

/// <summary>후보 정차 — 도면 x·y·방향, 영역 중심에서 안쪽 거리·좌측·방향 보정(°), 차체 여유, 판정 개수, 공통 리프트.</summary>
public sealed record ReachStationCandidateDto(double X, double Y, double Theta, double DistM, double LateralM, double YawDeg,
    double BodyClearanceM, bool Feasible, int Clear, int Collision, int Unreachable, double Robustness,
    double? StrokeMm = null, int? SafeClear = null, double? TraverseMarginDeg = null, double? MarginTargetDeg = null);
