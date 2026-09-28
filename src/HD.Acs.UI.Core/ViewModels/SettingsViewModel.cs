using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HD.Acs.UI.Models;
using HD.Acs.UI.Services;

namespace HD.Acs.UI.ViewModels;

/// <summary>
/// 설정 화면 VM — 로봇 타입 기본 정보(이름/제조사/시리얼/VDA버전/활성)를 선택·기입·저장.
/// 로봇 목록에서 하나를 선택하면 편집 필드에 채워지고, 저장 시 PUT /api/robots/{id} 로 upsert 된다.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly IAcsApiClient _api;

    /// <summary>등록된 로봇 목록(선택란 원본).</summary>
    public ObservableCollection<RobotDto> Robots { get; } = new();

    [ObservableProperty] private RobotDto? _selectedRobot;

    // 선택 로봇의 편집 필드(입력 중 값 — 저장 전까지 목록에 반영하지 않음)
    [ObservableProperty] private string _robotId = "";
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _manufacturer = "";
    [ObservableProperty] private string _serialNumber = "";
    [ObservableProperty] private string _vdaVersion = "2.0";
    [ObservableProperty] private bool _isActive = true;
    [ObservableProperty] private string? _statusMessage;

    public SettingsViewModel(IAcsApiClient api) => _api = api;

    [RelayCommand]
    public async Task LoadAsync()
    {
        try
        {
            var prev = SelectedRobot?.RobotId;
            Robots.Clear();
            foreach (var r in await _api.GetRobotsAsync())
                Robots.Add(r);
            SelectedRobot = Robots.FirstOrDefault(r => r.RobotId == prev) ?? Robots.FirstOrDefault();
            StatusMessage = Robots.Count == 0 ? "등록된 로봇이 없습니다. '새 로봇'으로 추가하세요." : null;
        }
        catch (Exception ex)
        {
            StatusMessage = $"로봇 목록 조회 실패: {ex.Message}";
        }
    }

    partial void OnSelectedRobotChanged(RobotDto? value)
    {
        if (value is null) return;
        RobotId = value.RobotId;
        Name = value.Name;
        Manufacturer = value.Manufacturer;
        SerialNumber = value.SerialNumber;
        VdaVersion = value.VdaVersion;
        IsActive = value.IsActive;
    }

    /// <summary>편집 필드를 비워 새 로봇 입력 준비.</summary>
    [RelayCommand]
    private void NewRobot()
    {
        SelectedRobot = null;
        RobotId = "";
        Name = "";
        Manufacturer = "";
        SerialNumber = "";
        VdaVersion = "2.0";
        IsActive = true;
        StatusMessage = "새 로봇 정보를 기입 후 저장하세요.";
    }

    private bool CanSave() => !string.IsNullOrWhiteSpace(RobotId);

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        var dto = new RobotDto(RobotId.Trim(), Name?.Trim() ?? "", Manufacturer?.Trim() ?? "",
            SerialNumber?.Trim() ?? "", string.IsNullOrWhiteSpace(VdaVersion) ? "2.0" : VdaVersion.Trim(), IsActive);
        try
        {
            await _api.SaveRobotAsync(dto);
            await LoadAsync();
            SelectedRobot = Robots.FirstOrDefault(r => r.RobotId == dto.RobotId);
            StatusMessage = $"[{dto.RobotId}] 저장됨.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"저장 실패: {ex.Message}";
        }
    }

    partial void OnRobotIdChanged(string value) => SaveCommand.NotifyCanExecuteChanged();
}
