namespace HD.Acs.UI.Models;

// 프로젝트(.hdacs) 일괄 가져오기·작업 일괄 조회 — 서버 ProjectImportService / GET /api/internal/area-tasks 미러.
// 대형 선창 열기를 요청 수천 건 → 1건으로(전부 또는 전무), 저장의 작업 조회를 영역 수만큼 → 1건으로 줄인다.

/// <summary>선창 파라미터(각도 deg) — 서버 CreateTankGeometryRequest와 같은 필드.</summary>
public sealed record ProjectImportGeometryDto(
    double LengthL, double WFloor, double ThetaLowDeg, double HLow,
    double HWall, double ThetaUpDeg, double HUp,
    double[] LevelZ, double OriginOx, double OriginOy,
    double? CheckHTotal, double? CheckBeam, double? CheckWCeil, string? UserId,
    double? ReachZMin, double? ReachZMax);

/// <summary>작업 1개 — 서버 CreateAreaTaskRequest와 같은 필드.</summary>
public sealed record ProjectImportTaskDto(int? Seq, string? Name, string? SeamType,
    double StartU, double StartV, double EndU, double EndV, string? SectionDxfId, string? ProfileId, string? UserId,
    Guid? TaskId, double[][]? Points);

/// <summary>영역 1개 + 소속 작업.</summary>
public sealed record ProjectImportAreaDto(string WallCode, string Name, double[][]? Corners,
    double UMin, double VMin, double UMax, double VMax,
    double? StationX, double? StationY, double? StationTheta, double? StationStandoffM,
    int? SortOrder, Guid? AreaId, ProjectImportTaskDto[] Tasks);

public sealed record ProjectImportRequestDto(ProjectImportGeometryDto Geometry, ProjectImportAreaDto[] Areas, string? UserId);

/// <summary>가져오기 결과 — AreaIds는 요청 순서(areaId 미지정 영역은 서버가 새로 발급한 값).</summary>
public sealed record ProjectImportResultDto(string TankId, int Areas, int Tasks, Guid[] AreaIds);

/// <summary>선창 전 작업 일괄 조회 행 — <see cref="AreaTaskDto"/> + 소속 영역.</summary>
public sealed record TankAreaTaskDto(Guid AreaId, Guid TaskId, int Seq, string? Name, string SeamType,
    double StartU, double StartV, double EndU, double EndV, string SectionDxfId, string ProfileId,
    double[][]? Points = null)
{
    public AreaTaskDto ToAreaTask() => new(TaskId, Seq, Name, SeamType, StartU, StartV, EndU, EndV, SectionDxfId, ProfileId, Points);
}
