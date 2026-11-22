namespace HD.Acs.UI.Services;

/// <summary>화물창 벽면 코드 정의 (docs/TANK_WALL_LAYOUT.md §2).</summary>
/// <param name="Code">벽면 코드 (A/F/T/B/SU/SM/SL/PU/PM/PL)</param>
/// <param name="Name">한글 명칭</param>
/// <param name="NormX">전개도 캔버스 정규화 X(0~1) — 중앙 A + 방사형 8벽면 + 외곽 F</param>
/// <param name="NormY">전개도 캔버스 정규화 Y(0~1)</param>
public sealed record WallCode(string Code, string Name, double NormX, double NormY);

/// <summary>화물창 층(건조 슬라이스). AMR은 엘리베이터로 층간 이동(수동, Q9).</summary>
/// <param name="Level">L1~L4</param>
/// <param name="MapId">VDA 5050 mapId 접미 (예: CT1-L1)</param>
public sealed record TankFloor(string Level, string MapId)
{
    /// <summary>화면 표기(<see cref="LevelLabel"/>). <see cref="Level"/> 은 맵 ID 를 만드는 내부 키(L1~L4)라 화면에 쓰지 않는다.</summary>
    public string Label => LevelLabel.OfKey(Level);
}

/// <summary>
/// TANK_WALL_LAYOUT 전개도의 정적 레이아웃 모델.
/// 중앙 Aft Bulkhead(A)를 팔각형 중심에 두고 8개 벽면을 방사형 배치, Fore Bulkhead(F)는 외곽에 둔다.
/// 3D 뷰와 전개도가 동일한 벽면 코드/좌표계를 공유하는 기준점이다(§3.2).
/// </summary>
public static class TankLayout
{
    /// <summary>기본 화물창 식별자(단일 화물창 가정). 다중 화물창 시 설정으로 이관.</summary>
    public const string DefaultTankId = "CT1";

    /// <summary>층 목록 (하부부터 L1~L4).</summary>
    public static readonly IReadOnlyList<TankFloor> Floors = new[]
    {
        new TankFloor("L1", $"{DefaultTankId}-L1"),
        new TankFloor("L2", $"{DefaultTankId}-L2"),
        new TankFloor("L3", $"{DefaultTankId}-L3"),
        new TankFloor("L4", $"{DefaultTankId}-L4"),
    };

    /// <summary>선창 ID 의 층 목록 — 층 맵 ID 는 `{선창}-L{층}`.</summary>
    public static IReadOnlyList<TankFloor> FloorsFor(string tankId) =>
        Floors.Select(f => new TankFloor(f.Level, $"{tankId}-{f.Level}")).ToArray();

    /// <summary>
    /// 층 목록을 다른 선창 ID 로 바꾼다(선창 ID 변경·다른 선창 프로젝트 열기). 항목을 제자리에서 갈아 끼우므로
    /// 호출한 쪽이 선택 항목을 같은 층으로 다시 잡아야 한다. 바뀐 것이 있으면 true.
    /// </summary>
    public static bool Retarget(System.Collections.ObjectModel.ObservableCollection<TankFloor> floors, string tankId)
    {
        var changed = false;
        for (var i = 0; i < floors.Count; i++)
        {
            var mapId = $"{tankId}-{floors[i].Level}";
            if (floors[i].MapId == mapId) continue;
            floors[i] = floors[i] with { MapId = mapId };
            changed = true;
        }
        return changed;
    }

    /// <summary>10개 벽면 코드 (중앙 A · 방사형 8 · 외곽 F).</summary>
    public static readonly IReadOnlyList<WallCode> Walls = new[]
    {
        new WallCode("A",  "Aft Bulkhead (선미)",   0.50, 0.50),
        new WallCode("T",  "Top (천장)",             0.50, 0.14),
        new WallCode("SU", "우현 상부 챔퍼",          0.78, 0.24),
        new WallCode("SM", "우현 중앙 수직",          0.90, 0.50),
        new WallCode("SL", "우현 하부 챔퍼",          0.78, 0.76),
        new WallCode("B",  "Bottom (바닥 멤브레인)",  0.50, 0.86),
        new WallCode("PL", "좌현 하부 챔퍼",          0.22, 0.76),
        new WallCode("PM", "좌현 중앙 수직",          0.10, 0.50),
        new WallCode("PU", "좌현 상부 챔퍼",          0.22, 0.24),
        new WallCode("F",  "Fore Bulkhead (선수)",   0.93, 0.09),
    };
}
