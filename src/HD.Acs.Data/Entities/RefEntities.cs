namespace HD.Acs.Data.Entities;

// ref 스키마 — 마스터 데이터 (db/schema.sql 대응)

public class MapEntity
{
    public string MapId { get; set; } = "";       // 'CT1-L1' (층 = 맵)
    public string TankId { get; set; } = "";
    public int Level { get; set; }                // 1(바닥)~4
    public string Name { get; set; } = "";
    public int Version { get; set; } = 1;
    public bool IsActive { get; set; } = true;
}

// 도면→맵 캘리브레이션 [PHASE2 WP-1 · T_W_D] — 맵버전 바인딩(map_version == ref.map.version일 때만 유효)
public class MapCalibrationEntity
{
    public string MapId { get; set; } = "";
    public int MapVersion { get; set; }
    public double Tx { get; set; }                    // 평행이동 X [m]
    public double Ty { get; set; }                    // 평행이동 Y [m]
    public double YawRad { get; set; }                // 회전 [rad], 맵 X축 기준 CCW
    public double RmsM { get; set; }                  // 등록 잔차 RMS [m]
    public int PointCount { get; set; }
    public string? RegisteredBy { get; set; }
    public DateTimeOffset RegisteredAt { get; set; } = DateTimeOffset.UtcNow;
}

// 캡처된 기준점 대응쌍 (감사·재계산용 보존)
public class MapCalibrationPointEntity
{
    public Guid Id { get; set; }
    public string MapId { get; set; } = "";
    public int MapVersion { get; set; }
    public double DrawingXM { get; set; }             // 도면 좌표 (m로 정규화 저장)
    public double DrawingYM { get; set; }
    public double MapX { get; set; }                  // 캡처 시점 RobotContext.ReportedX
    public double MapY { get; set; }
    public DateTimeOffset CapturedAt { get; set; } = DateTimeOffset.UtcNow;
    public string? CapturedBy { get; set; }
}

public class NodeEntity
{
    public string NodeId { get; set; } = "";
    public string MapId { get; set; } = "";
    public string? Name { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double? Theta { get; set; }
    public double? AllowedDevXy { get; set; }
    public double? AllowedDevTheta { get; set; }
    public string NodeType { get; set; } = "WAYPOINT";
    public string? Metadata { get; set; }         // jsonb
}

public class EdgeEntity
{
    public string EdgeId { get; set; } = "";
    public string MapId { get; set; } = "";
    public string StartNodeId { get; set; } = "";
    public string EndNodeId { get; set; } = "";
    public bool Bidirectional { get; set; } = true;
    public string EdgeType { get; set; } = "TRAVEL";  // TRAVEL | MANUAL_TRANSFER
    public double? MaxSpeed { get; set; }
    public double? Length { get; set; }
    public string? Metadata { get; set; }
}

public class ZoneEntity
{
    public string ZoneId { get; set; } = "";
    public string MapId { get; set; } = "";
    public string Name { get; set; } = "";
    public string ZoneType { get; set; } = "AREA";    // FLOOR | AREA | ELEVATOR_CELL | RESTRICTED
    public string? Geometry { get; set; }             // jsonb
}

public class ZoneMemberEntity
{
    public string ZoneId { get; set; } = "";
    public string NodeId { get; set; } = "";
}

public class ActionCatalogEntity
{
    public string ActionType { get; set; } = "";
    public string Scope { get; set; } = "NODE";       // NODE | EDGE | INSTANT
    public string BlockingType { get; set; } = "HARD";
    public string? ParamSchema { get; set; }          // jsonb
    public string? Description { get; set; }
}

public class ScenarioEntity
{
    public Guid ScenarioId { get; set; }
    public string Name { get; set; } = "";
    public int Version { get; set; }
    public string TankId { get; set; } = "";
    public string Policy { get; set; } = "{}";        // jsonb — 재시도/스킵 정책 외부화 [ADR-010]
    public string Status { get; set; } = "DRAFT";
    public List<InspectionPointEntity> Points { get; set; } = new();
}

public class InspectionPointEntity
{
    public Guid PointId { get; set; }
    public Guid ScenarioId { get; set; }
    public int Seq { get; set; }
    public string NodeId { get; set; } = "";          // 층은 node.map_id로 결정
    public List<InspectionTaskEntity> Tasks { get; set; } = new();
}

public class InspectionTaskEntity
{
    public Guid TaskId { get; set; }
    public Guid PointId { get; set; }
    public int Seq { get; set; }
    public string ActionType { get; set; } = "";
    public string? JobRef { get; set; }               // HD_AMR 검사 작업 식별자 [ADR-001]
    public string? Position { get; set; }             // jsonb {tank,level,wall_code,x,y,z} [ADR-004]
    public string? Params { get; set; }               // jsonb opaque
}

// 용접선 [PHASE2 WP-2] — 사람이 등록하는 유일한 입력(도면 좌표). SeamSlicer가 스테이션/TASK로 전개.
public class WeldSeamEntity
{
    public Guid SeamId { get; set; }
    public string TankId { get; set; } = "";
    public int Level { get; set; }
    public string WallCode { get; set; } = "";
    public string SeamType { get; set; } = "LINE";      // LINE | POLYLINE
    public string PathDrawing { get; set; } = "[]";     // jsonb [[x,y,z],...] m, 도면 좌표
    public string NormalDrawing { get; set; } = "[]";   // jsonb [nx,ny,nz] 벽면 법선(도면 좌표계)
    public string SectionDxfId { get; set; } = "";      // 단면 DXF 참조 (원문 미저장)
    public string ProfileId { get; set; } = "";         // 검사 프로파일 참조
    public string? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

// 선창 파라메트릭 정의 [SPEC v3 §2] — 팔각 단면 파라미터. 등록 시 §3 면 자동 생성.
public class TankGeometryEntity
{
    public string TankId { get; set; } = "";
    public double LengthL { get; set; }
    public double WFloor { get; set; }
    public double ThetaLow { get; set; }     // [rad] 저장
    public double HLow { get; set; }
    public double HWall { get; set; }
    public double ThetaUp { get; set; }
    public double HUp { get; set; }
    public string LevelZ { get; set; } = "[]";   // jsonb 층 경계 z 목록
    public double? ReachZMin { get; set; }        // (선택, v3.1) 코봇 도달 밴드 하한 상대높이
    public double? ReachZMax { get; set; }        // (선택, v3.1) 코봇 도달 밴드 상한 상대높이
    public double OriginOx { get; set; }
    public double OriginOy { get; set; }
    public string? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

// 벽면(Wall) LAYER [SPEC v3 §3] — 파라미터에서 자동 생성된 면. (tank_id, wall_code) 단위(level 없음, 통짜 면).
public class WallEntity
{
    public string TankId { get; set; } = "";
    public string WallCode { get; set; } = "";
    public string Origin { get; set; } = "[]";   // jsonb [x,y,z] 벽면-로컬 (0,0)의 도면 좌표
    public string UAxis { get; set; } = "[]";     // jsonb [x,y,z] +u축 단위벡터
    public string VAxis { get; set; } = "[]";     // jsonb [x,y,z] +v축 단위벡터(U와 직교)
    public string Normal { get; set; } = "[]";    // jsonb [x,y,z] 내부향 단위 법선
    public double ULen { get; set; }              // 면 가로 크기(m)
    public double VLen { get; set; }              // 면 세로 크기(m)
    public double? FacingYaw { get; set; }        // AMR이 면을 바라보는 도면 yaw [rad]. B/T는 NULL
    public bool Generated { get; set; } = true;
    public string? Description { get; set; }
}

// 영역(Area) LAYER [SPEC v3 §4] — 면(ref.wall) 위 로컬 (u,v) 사각형. 영역 1개 = STATION 1개 = anchorGroup 1개.
public class InspectionAreaEntity
{
    public Guid AreaId { get; set; }
    public string TankId { get; set; } = "";
    public string WallCode { get; set; } = "";
    public int Level { get; set; }                      // AMR 주행 층 (mapId 결정)
    public string Name { get; set; } = "";
    public string Corners { get; set; } = "[]";         // jsonb [[u1,v1]…[u4,v4]] 임의 4점 사각형
    public double UMin { get; set; }                    // corners의 bbox (서버 유도)
    public double VMin { get; set; }
    public double UMax { get; set; }
    public double VMax { get; set; }
    public double? StationX { get; set; }               // 정차 수동 오버라이드 (전역 x,y + theta). FL/CL 필수(§5)
    public double? StationY { get; set; }
    public double? StationTheta { get; set; }
    public int SortOrder { get; set; }
    public string? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<AreaTaskEntity> Tasks { get; set; } = new();
}

// 검사 작업 [SPEC v3 §4] — 영역 내 용접선. 시작/끝점은 면 로컬 (u,v). 작업 1개 = TASK 1개.
public class AreaTaskEntity
{
    public Guid TaskId { get; set; }
    public Guid AreaId { get; set; }
    public int Seq { get; set; }                        // 영역 내 실행 순서 → seqInGroup
    public string? Name { get; set; }
    public string SeamType { get; set; } = "LINE";
    public double StartU { get; set; }
    public double StartV { get; set; }
    public double EndU { get; set; }
    public double EndV { get; set; }
    public string SectionDxfId { get; set; } = "";
    public string ProfileId { get; set; } = "";
    public string? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class RobotEntity
{
    public string RobotId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Manufacturer { get; set; } = "";    // VDA5050 토픽 요소
    public string SerialNumber { get; set; } = "";
    public string VdaVersion { get; set; } = "2.0";
    public bool IsActive { get; set; } = true;
}
