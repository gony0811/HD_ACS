namespace HD.Acs.UI.Models;

// ── 리치·간섭 시뮬레이션 — 서버 ReachSimService 응답 미러 (/api/reach-sim/…) ──

/// <summary>조사 — 영역 하나와 그 용접선 판정.</summary>
public sealed record ReachSurveyAreaDto(Guid AreaId, string Name, string WallCode, int Level, double StationX, double StationY,
    List<ReachSurveySeamDto> Seams);

/// <summary>조사 — 용접선 하나. Z 는 층 바닥 위[m], Verdict = Clear | Collision | Unreachable.</summary>
public sealed record ReachSurveySeamDto(Guid TaskId, int Seq, double ZMin, double ZMax, string Verdict,
    double? StrokeMm, double? MinClearanceM, int YawIndex);

public sealed record ReachSeamLineDto(Guid TaskId, int Seq, double[] Start, double[] End);
public sealed record ReachBodyDto(double[][] Corners, double ClearanceM, string NearestPlane);
public sealed record ReachPartDto(string Name, double[] A, double[] B, double RadiusM, double ClearanceM, string NearestPlane);
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
    ReachBodyDto Body, List<ReachStageDto> Stages, double WarnClearanceM);

/// <summary>정차점 최적화 — 영역 하나의 지금 정차와 찾은 정차. Overridden = 이미 정차 덮어쓰기가 있음.</summary>
public sealed record ReachStationOptimumDto(Guid AreaId, string Name, string WallCode, int Level, bool Overridden,
    ReachStationCandidateDto Current, ReachStationCandidateDto Best, int Evaluated, long ElapsedMs,
    List<ReachOptimumSeamDto>? Seams = null);

/// <summary>용접선 하나의 지금·찾은 정차 판정(Clear | Collision | Unreachable)과 찾은 정차에서 고른 리프트.</summary>
public sealed record ReachOptimumSeamDto(Guid TaskId, int Seq, string Current, string Best, double? BestStrokeMm);

/// <summary>후보 정차 — 도면 x·y·방향, 영역 중심에서 안쪽 거리·좌측·방향 보정(°), 차체 여유, 판정 개수.</summary>
public sealed record ReachStationCandidateDto(double X, double Y, double Theta, double DistM, double LateralM, double YawDeg,
    double BodyClearanceM, bool Feasible, int Clear, int Collision, int Unreachable, double Robustness);
