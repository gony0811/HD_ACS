using System.Collections.Concurrent;
using HD.Acs.Core.Reach;

namespace HD.Acs.App.Services;

/// <summary>
/// HD_AMR 이 프로필 전송 때 함께 알려 주는 자기 코봇 모델 값 — ACS 리치 시뮬레이터가 같은 값을 쓰는지 대조하는 데만 쓴다
/// (ACS 는 이 값으로 자기 설정을 바꾸지 않는다). 모르는 값은 null.
/// </summary>
/// <param name="LinksMm">링크 치수 [d1, a2, a3, d4, d5, d6] mm (AMR <c>cobot.kinematics.linksMm</c> — 컨트롤러 DH 보정 반영).</param>
/// <param name="WristMarginDeg">경로 손목 특이점 여유 |J5| [도] (AMR <c>PostureLimits.DefaultWristMarginDeg</c>).</param>
/// <param name="ToolLengthMm">플랜지 면 → 검사 초점 [mm] (AMR <c>Fr5Kinematics.InspectionTool</c> z).</param>
public sealed record AmrRobotModel(double[]? LinksMm, double? WristMarginDeg, double? ToolLengthMm);

/// <summary>AMR 이 알려 준 값과 ACS 설정의 차이.</summary>
public static class RobotModelCheck
{
    /// <summary>다른 항목마다 한 줄(같으면 빈 목록). ACS 의 초점 거리 = 공구 z + 초점 자세 이격.</summary>
    public static IReadOnlyList<string> Compare(ReachRobotModel acs, AmrRobotModel amr)
    {
        var list = new List<string>();
        if (amr.LinksMm is { Length: 6 } links)
        {
            var mine = acs.Kinematics.LinksMm;
            var names = new[] { "d1", "a2", "a3", "d4", "d5", "d6" };
            var diff = Enumerable.Range(0, 6).Where(i => Math.Abs(links[i] - mine[i]) > 0.01)
                .Select(i => $"{names[i]} AMR {links[i]:0.###} ≠ ACS {mine[i]:0.###}").ToList();
            if (diff.Count > 0) list.Add($"링크 치수[mm]: {string.Join(", ", diff)} (Acs:Reach:LinksMm)");
        }
        if (amr.WristMarginDeg is { } w && Math.Abs(w - acs.PlanarWristMarginDeg) > 0.05)
            list.Add($"손목 여유 |J5|: AMR {w:0.#}° ≠ ACS {acs.PlanarWristMarginDeg:0.#}° (Acs:Reach:WristMarginDeg)");
        var focus = acs.ToolZMm + (acs.StageStandoffsMm.Length > 0 ? acs.StageStandoffsMm[^1] : 0);
        if (amr.ToolLengthMm is { } t && Math.Abs(t - focus) > 0.5)
            list.Add($"플랜지→초점 거리: AMR {t:0.#} mm ≠ ACS {focus:0.#} mm (Acs:Reach:ToolZMm + FocusStandoffMm)");
        return list;
    }
}

/// <summary>AMR 별 마지막 보고와 대조 결과(메모리 — 서버를 다시 띄우면 다음 프로필 전송 때 다시 채워진다).</summary>
public sealed class AmrRobotModelStore
{
    public sealed record Entry(string Source, AmrRobotModel Model, IReadOnlyList<string> Mismatch, DateTimeOffset ReceivedAt);

    private readonly ConcurrentDictionary<string, Entry> _bySource = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<Entry> All => _bySource.Values.OrderBy(e => e.Source).ToList();

    /// <summary>보고를 기록한다. 대조 결과가 직전과 달라졌으면 true(로그를 한 번만 남기려고).</summary>
    public bool Report(string source, AmrRobotModel model, IReadOnlyList<string> mismatch)
    {
        var changed = !_bySource.TryGetValue(source, out var old) || !old.Mismatch.SequenceEqual(mismatch);
        _bySource[source] = new Entry(source, model, mismatch, DateTimeOffset.UtcNow);
        return changed;
    }
}
