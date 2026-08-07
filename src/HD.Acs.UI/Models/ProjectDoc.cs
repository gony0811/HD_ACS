namespace HD.Acs.UI.Models;

// 프로젝트 파일(.hdacs) 스냅샷 모델 — 선창 지오메트리 + 영역 + 작업 전체.
// 이진 컨테이너(매직 헤더 + GZip + JSON)로 직렬화되며, 이 프로그램에서만 열 수 있다.

/// <summary>프로젝트 파일 최상위 문서 — 현재 선창의 지오메트리·영역·작업 전체 스냅샷.</summary>
public sealed record ProjectDoc(
    int Version,
    string TankId,
    GeometryDoc Geometry,
    AreaDoc[] Areas);

/// <summary>선창 3D 정의 파라미터 [SPEC v3 §2] — 면은 열기 시 재생성되므로 저장하지 않는다.</summary>
public sealed record GeometryDoc(
    double LengthL, double WFloor, double ThetaLowDeg, double HLow,
    double HWall, double ThetaUpDeg, double HUp,
    double[] LevelZ, double OriginOx, double OriginOy,
    double? ReachZMin = null, double? ReachZMax = null);   // v3.1 §5-A 도달 밴드(선택)

/// <summary>영역 스냅샷 (벽면-로컬 u,v) + 소속 검사 작업.</summary>
public sealed record AreaDoc(
    string WallCode, int Level, string Name,
    double UMin, double VMin, double UMax, double VMax,
    double? StationX, double? StationY, double? StationTheta,
    TaskDoc[] Tasks);

/// <summary>검사 작업 스냅샷 (벽면-로컬 u,v).</summary>
public sealed record TaskDoc(
    int Seq, string? Name, string SeamType,
    double StartU, double StartV, double EndU, double EndV,
    string SectionDxfId, string ProfileId);
