using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HD.Acs.UI.Models;
using HD.Acs.UI.Services;

namespace HD.Acs.UI.ViewModels;

/// <summary>
/// AMR 티칭 참조 테이블 — ACS가 도면 Area→T_W_D로 산출한 STATION 노드(맵 pose)를 보여준다.
/// 작업자는 이 pose로 AMR을 수동 티칭한 뒤, 부여된 Job/Task 인덱스를 별도로 회수·등록한다.
/// (TARS-M은 좌표 goto가 없고 사전 티칭 인덱스로 이동 — VDA5050_SPEC_PLAN 부록 A.)
/// </summary>
public sealed partial class AmrTeachingViewModel : ObservableObject
{
    private readonly IAcsApiClient _api;
    private readonly IProjectDialogService _dialog;

    public ObservableCollection<AmrTeachingRowDto> Rows { get; } = new();

    /// <summary>대상 선창 — 시나리오와 동일 규약(기본 CT1). Shell이 프로젝트 열기 시 동기화 가능.</summary>
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
            foreach (var r in rows) Rows.Add(r);
            StatusMessage = Rows.Count == 0
                ? $"'{TankId}' 선창에 STATION 노드가 없습니다. (영역·작업 등록 후 다시 시도)"
                : $"{Rows.Count}개 스테이션 로드됨.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"조회 실패: {ex.Message}";
        }
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
                r.AmrJobIndex?.ToString() ?? ""));
        return sb.ToString();
    }
}
