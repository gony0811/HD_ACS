using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HD.Acs.UI.Models;
using HD.Acs.UI.Services;

namespace HD.Acs.UI.ViewModels;

/// <summary>편집 가능한 AMR 티칭 행 — 식별 컬럼은 읽기전용, JobIndex 만 편집 가능. IsDirty 로 변경분만 저장.</summary>
public sealed partial class AmrTeachingRowVm : ObservableObject
{
    public string NodeId { get; init; } = "";
    public string MapId { get; init; } = "";
    public string Name { get; init; } = "";
    public double MapX { get; init; }
    public double MapY { get; init; }
    public double ThetaRad { get; init; }
    public double ThetaDeg { get; init; }
    public double? AllowedDevXy { get; init; }
    public double? AllowedDevTheta { get; init; }

    [ObservableProperty] private int? _jobIndex;
    public int? OriginalJobIndex { get; private set; }
    public string? GotoMode { get; private set; }

    public bool IsDirty => JobIndex != OriginalJobIndex;

    public static AmrTeachingRowVm From(AmrTeachingRowDto d) => new()
    {
        NodeId = d.NodeId, MapId = d.MapId, Name = d.Name,
        MapX = d.MapX, MapY = d.MapY, ThetaRad = d.ThetaRad, ThetaDeg = d.ThetaDeg,
        AllowedDevXy = d.AllowedDevXy, AllowedDevTheta = d.AllowedDevTheta,
        _jobIndex = d.AmrJobIndex, OriginalJobIndex = d.AmrJobIndex, GotoMode = d.GotoMode,
    };

    /// <summary>저장 성공 후 기준값 갱신(=IsDirty 해제).</summary>
    public void MarkSaved(int? saved) { OriginalJobIndex = saved; JobIndex = saved; }
}

/// <summary>
/// AMR 티칭 참조 테이블 — ACS가 도면 Area→T_W_D로 산출한 STATION 노드(맵 pose)를 보여주고,
/// 작업자가 AMR을 그 pose로 수동 티칭한 뒤 회수한 Job 인덱스를 여기서 입력·저장한다.
/// 저장 값은 node.metadata.amr.jobIndex 로 보존되어, 이후 order 발행 시 HD_AMR로 전달된다.
/// (TARS-M은 좌표 goto가 없고 인덱스로 이동 — VDA5050_SPEC_PLAN 부록 A / NODE_INDEX_TRANSMISSION.)
/// </summary>
public sealed partial class AmrTeachingViewModel : ObservableObject
{
    private readonly IAcsApiClient _api;
    private readonly IProjectDialogService _dialog;

    public ObservableCollection<AmrTeachingRowVm> Rows { get; } = new();

    /// <summary>대상 선창 — 시나리오와 동일 규약(기본 CT1).</summary>
    [ObservableProperty] private string _tankId = "CT1";
    [ObservableProperty] private string? _statusMessage;

    public bool HasRows => Rows.Count > 0;

    public AmrTeachingViewModel(IAcsApiClient api, IProjectDialogService dialog)
    {
        _api = api;
        _dialog = dialog;
        Rows.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasRows));
            DownloadCsvCommand.NotifyCanExecuteChanged();
        };
    }

    [RelayCommand]
    private async Task LoadAsync()
    {
        try
        {
            var rows = await _api.GetAmrTeachingTableAsync(TankId);
            Rows.Clear();
            foreach (var r in rows) Rows.Add(AmrTeachingRowVm.From(r));
            StatusMessage = Rows.Count == 0
                ? $"'{TankId}' 선창에 STATION 노드가 없습니다. (영역·작업 등록 후 다시 시도)"
                : $"{Rows.Count}개 스테이션 로드됨.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"조회 실패: {ex.Message}";
        }
    }

    /// <summary>변경(IsDirty)된 행의 JobIndex를 서버에 등록.</summary>
    [RelayCommand]
    private async Task SaveAsync()
    {
        var dirty = Rows.Where(r => r.IsDirty).ToList();
        if (dirty.Count == 0) { StatusMessage = "변경된 항목이 없습니다."; return; }
        int ok = 0, fail = 0;
        foreach (var r in dirty)
        {
            try
            {
                var updated = await _api.SetAmrMappingAsync(r.NodeId, r.JobIndex);
                r.MarkSaved(updated?.AmrJobIndex ?? r.JobIndex);
                ok++;
            }
            catch (Exception ex)
            {
                fail++;
                StatusMessage = $"'{r.NodeId}' 저장 실패: {ex.Message}";
            }
        }
        if (fail == 0) StatusMessage = $"{ok}개 저장됨.";
        else StatusMessage = $"{ok}개 저장, {fail}개 실패 (마지막: {StatusMessage}).";
    }

    private bool CanDownload() => Rows.Count > 0;

    [RelayCommand(CanExecute = nameof(CanDownload))]
    private void DownloadCsv()
    {
        var path = _dialog.PickSavePath($"{TankId}_amr_teaching_table.csv");
        if (path is null) return;
        try
        {
            File.WriteAllText(path, BuildCsv(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            StatusMessage = $"CSV 저장 완료: {path}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"저장 실패: {ex.Message}";
        }
    }

    private string BuildCsv()
    {
        static string Csv(string? s)
        {
            s ??= "";
            return s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
        }
        var sb = new StringBuilder();
        sb.AppendLine("node_id,map_id,name,map_x,map_y,theta_rad,theta_deg,allowed_dev_xy,allowed_dev_theta,amr_job_index");
        foreach (var r in Rows)
            sb.AppendLine(string.Join(',',
                Csv(r.NodeId), Csv(r.MapId), Csv(r.Name),
                r.MapX.ToString("0.####"), r.MapY.ToString("0.####"),
                r.ThetaRad.ToString("0.#####"), r.ThetaDeg.ToString("0.##"),
                r.AllowedDevXy?.ToString("0.###") ?? "", r.AllowedDevTheta?.ToString("0.###") ?? "",
                r.JobIndex?.ToString() ?? ""));
        return sb.ToString();
    }
}
