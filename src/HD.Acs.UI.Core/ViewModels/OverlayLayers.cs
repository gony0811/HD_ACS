using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HD.Acs.UI.Rendering;

namespace HD.Acs.UI.ViewModels;

/// <summary>
/// 운영 화면 3D 뷰·전개도의 세부 표시 항목(보이기/숨기기). 두 뷰가 각자 인스턴스를 가진다
/// (<see cref="TankViewModel.View3DLayers"/> / <see cref="TankViewModel.FlatLayers"/>) — 우클릭 메뉴가 바인딩한다.
/// 끈 항목은 씬·전개도 목록에서 아예 만들지 않으므로(숨김이 아니라 생략) 영역·작업이 많을수록 렌더링이 빨라진다.
/// 기본값: 시작·끝점 마커와 작업 순번은 작업 수에 비례해 가장 많은 도형을 만드므로 꺼 둔다.
/// </summary>
public sealed partial class OverlayLayers : ObservableObject
{
    [ObservableProperty] private bool _areaFills = true;      // 영역 채움·윤곽
    [ObservableProperty] private bool _areaLabels = true;     // 영역 이름
    [ObservableProperty] private bool _weldLines = true;      // 용접선
    [ObservableProperty] private bool _weldEndpoints;         // 용접 시작(녹)·끝(적)점 마커
    [ObservableProperty] private bool _taskSeq;               // 작업 순번 라벨
    [ObservableProperty] private bool _floorGrid = true;      // 3D 층 바닥 1m 격자(격리 뷰) — 전개도 무관
    [ObservableProperty] private bool _groundGrid = true;     // 3D 지면 참조 격자 — 전개도 무관

    /// <summary>항목을 모두 켠다.</summary>
    [RelayCommand]
    private void ShowAll()
    {
        AreaFills = AreaLabels = WeldLines = WeldEndpoints = TaskSeq = FloorGrid = GroundGrid = true;
    }

    /// <summary>빠른 보기 — 영역 윤곽과 용접선만 남기고 글자·점·격자를 끈다(영역·작업이 많을 때).</summary>
    [RelayCommand]
    private void ShowMinimal()
    {
        AreaFills = WeldLines = true;
        AreaLabels = WeldEndpoints = TaskSeq = FloorGrid = GroundGrid = false;
    }

    /// <summary>렌더러 입력용 불변 스냅샷.</summary>
    public SceneLayers Snapshot() => new(AreaFills, AreaLabels, WeldLines, WeldEndpoints, TaskSeq, FloorGrid, GroundGrid);
}
