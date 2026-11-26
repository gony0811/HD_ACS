namespace HD.Acs.UI.ViewModels;

/// <summary>운영 화면 3D 뷰·전개도의 보기.</summary>
public enum TankDisplayMode
{
    /// <summary>선택한 미션의 검사 대상 영역을 강조하고, 대상이 아닌 영역은 흑백 음영으로 낮춘다.</summary>
    MissionFocus,
    /// <summary>선창 전체 영역의 진행·누적 검사 기록을 색으로 본다(종전 표시).</summary>
    Overview,
}

/// <summary>
/// 지금 화면이 기준으로 삼는 미션의 검사 대상 영역.
/// 진행 중 run이 있으면 그 run의 정차 영역(재검사 run 포함), 없으면 선택한 시나리오의 대상 영역(서버에 저장된 값).
/// All=true = 대상 미지정 시나리오(선창 전체).
/// </summary>
public sealed record MissionAreaScope(bool All, IReadOnlySet<Guid> AreaIds, string Source)
{
    public static MissionAreaScope Whole(string source) => new(true, new HashSet<Guid>(), source);

    public bool Contains(Guid areaId) => All || AreaIds.Contains(areaId);

    /// <summary>같은 범위인지(출처 문구는 보지 않음) — 같으면 화면을 다시 그리지 않는다.</summary>
    public bool SameAreas(MissionAreaScope? other) =>
        other is not null && All == other.All && (All || AreaIds.SetEquals(other.AreaIds));
}
