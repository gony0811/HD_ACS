using System.Reflection;
using HD.Acs.UI.Models;
using HD.Acs.UI.Services;
using HD.Acs.UI.ViewModels;
using Xunit;

namespace HD.Acs.UI.Core.Tests;

/// <summary>
/// 계획 › 리치·간섭 보고서 백데이터(CSV) 출력 — 영역마다 서버에 나눠 묻고, 영역 순서대로 TASK 요약·IK 해 목록 두 파일에 이어 쓴다.
/// </summary>
public class ReachReportExportTests
{
    public class ReportApi : DispatchProxy
    {
        public List<AreaDto> Areas { get; set; } = new();
        public List<(Guid? AreaId, bool Optimize)> Requests { get; } = new();
        public bool Unsupported { get; set; }
        public int CancelAfter { get; set; } = int.MaxValue;
        public Action? Cancel { get; set; }

        protected override object? Invoke(MethodInfo? m, object?[]? args) => m!.Name switch
        {
            nameof(IAcsApiClient.GetAreasAsync) => Task.FromResult<IReadOnlyList<AreaDto>>(Areas
                .Where(a => (args![1] is null || a.WallCode == (string?)args[1]) && (args[2] is null || a.Level == (int?)args[2])).ToList()),
            nameof(IAcsApiClient.GetReachReportAsync) => Report((Guid?)args![3], (bool)args[4]!, (CancellationToken)args[5]!),
            _ => m.ReturnType == typeof(Task) ? Task.CompletedTask : null,
        };

        private async Task<ReachReportDto?> Report(Guid? areaId, bool optimize, CancellationToken ct)
        {
            int n;
            lock (Requests) { Requests.Add((areaId, optimize)); n = Requests.Count; }
            var a = Areas.Single(x => x.AreaId == areaId);
            // 이름이 앞선 영역일수록 늦게 끝나게 해 순서 보장을 확인한다.
            if (n >= CancelAfter) Cancel?.Invoke();   // 이 요청이 시작될 때 중지 — 뒤 영역은 시작하지 않는다
            await Task.Delay(5 + 10 * (Areas.Count - Areas.IndexOf(a)), ct);
            ct.ThrowIfCancellationRequested();
            if (Unsupported) return null;
            return new ReachReportDto("영역,검사가능", $"{a.Name},가능\r\n{a.Name},불가\r\n", "영역,J1", $"{a.Name},1.5\r\n", 1, 2, 1, 0, 1);
        }
    }

    private sealed class Dialogs : IProjectDialogService
    {
        public string? Path { get; set; }
        public string? Suggested { get; private set; }
        public Task<bool> ShowNewProjectAsync() => Task.FromResult(false);
        public Task<string?> PickSavePathAsync(string? suggestedFileName = null) => Task.FromResult<string?>(null);
        public Task<string?> PickOpenPathAsync() => Task.FromResult<string?>(null);
        public Task<string?> PickCsvSavePathAsync(string title, string suggestedFileName)
        {
            Suggested = suggestedFileName;
            return Task.FromResult(Path);
        }
    }

    private static AreaDto Area(string name, int tasks, string wall = "PU", int level = 4) =>
        new(Guid.NewGuid(), "CT1", wall, level, name, 0, 0, 1, 1, null, null, null, 0, tasks);

    private static (ReachSimViewModel Vm, ReportApi Api, Dialogs Dialogs, string Path) Setup(params AreaDto[] areas)
    {
        var api = DispatchProxy.Create<IAcsApiClient, ReportApi>();
        var fake = (ReportApi)(object)api;
        fake.Areas = areas.ToList();
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"hdacs-report-{Guid.NewGuid():N}.csv");
        var dialogs = new Dialogs { Path = path };
        var vm = new ReachSimViewModel(api, dialogs) { TankId = "CT1", SelectedLevel = 4, SelectedWall = "PU" };
        return (vm, fake, dialogs, path);
    }

    private static void Cleanup(string path)
    {
        File.Delete(path);
        File.Delete(ReachSimViewModel.IkReportPath(path));
    }

    [Fact]
    public async Task 선택_층_면의_영역마다_묻고_영역_순서로_두_파일에_쓴다()
    {
        var (vm, api, dialogs, path) = Setup(Area("L4-R03", 5), Area("L4-R01", 5), Area("L4-R02", 5),
            Area("L4-EMPTY", 0), Area("L4-OTHER", 5, wall: "SU"));
        try
        {
            vm.ReportOptimize = true;
            await vm.ExportReportCommand.ExecuteAsync(null);

            Assert.Equal(3, api.Requests.Count);                       // 용접선 없는 영역·다른 면은 묻지 않는다
            Assert.All(api.Requests, r => Assert.True(r.Optimize));
            var bytes = File.ReadAllBytes(path);
            Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes.Take(3));   // Excel 한글용 BOM
            Assert.Equal(new[] { "영역,검사가능", "L4-R01,가능", "L4-R01,불가", "L4-R02,가능", "L4-R02,불가", "L4-R03,가능", "L4-R03,불가" },
                File.ReadAllLines(path));
            Assert.Equal(new[] { "영역,J1", "L4-R01,1.5", "L4-R02,1.5", "L4-R03,1.5" }, File.ReadAllLines(ReachSimViewModel.IkReportPath(path)));
            Assert.Contains("TASK 6 — 가능 3 · 가능(주의) 0 · 불가 3", vm.ReportSummary);
            Assert.Contains(path, vm.ReportSummary);
            Assert.Contains("보고서 저장 완료", vm.StatusMessage);
            Assert.Contains("L4-PU", dialogs.Suggested);
            Assert.False(vm.IsReporting);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public async Task 선창_전체는_모든_층_면을_층_면_이름_순으로_쓴다()
    {
        var (vm, api, _, path) = Setup(Area("L4-R01", 5), Area("L1-R01", 2, wall: "SL", level: 1), Area("L4-S01", 5, wall: "SU"));
        try
        {
            vm.ReportWholeTank = true;
            await vm.ExportReportCommand.ExecuteAsync(null);
            Assert.Equal(3, api.Requests.Count);
            Assert.All(api.Requests, r => Assert.False(r.Optimize));
            Assert.Equal(new[] { "L1-R01", "L4-R01", "L4-S01" },
                File.ReadAllLines(path).Skip(1).Select(l => l.Split(',')[0]).Distinct());
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public async Task 저장_위치를_취소하면_묻지_않고_구서버면_이유를_알린다()
    {
        var (vm, api, dialogs, path) = Setup(Area("L4-R01", 5));
        try
        {
            dialogs.Path = null;
            await vm.ExportReportCommand.ExecuteAsync(null);
            Assert.Empty(api.Requests);

            dialogs.Path = path;
            api.Unsupported = true;
            await vm.ExportReportCommand.ExecuteAsync(null);
            Assert.Contains("보고서 출력 실패", vm.StatusMessage);
            Assert.Contains("재빌드", vm.StatusMessage);
            Assert.False(vm.IsReporting);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public async Task 중지하면_그때까지_쓴_영역만_남기고_알린다()
    {
        var areas = Enumerable.Range(1, 12).Select(i => Area($"L4-R{i:00}", 5)).ToArray();
        var (vm, api, _, path) = Setup(areas);
        try
        {
            api.CancelAfter = 6;
            api.Cancel = () => vm.CancelReportCommand.Execute(null);
            await vm.ExportReportCommand.ExecuteAsync(null);

            Assert.Contains("보고서 출력 중지", vm.StatusMessage);
            Assert.True(api.Requests.Count < areas.Length);            // 남은 영역은 묻지 않는다
            var names = File.ReadAllLines(path).Skip(1).Select(l => l.Split(',')[0]).Distinct().ToList();
            Assert.Equal(areas.Take(names.Count).Select(a => a.Name), names);   // 앞에서부터 빠짐없이
            Assert.False(vm.IsReporting);
        }
        finally { Cleanup(path); }
    }
}
