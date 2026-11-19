namespace HD.Acs.App.Services;

/// <summary>리프트 운영 방식.</summary>
public enum LiftMode
{
    /// <summary>리프트 없음 — BASE 는 <c>Acs:Reach:MountZMm</c> 고정, 지령 <c>liftStrokeMm</c> 은 항상 0, 리프트 기둥은 간섭에서 제외.</summary>
    Off,
    /// <summary>자동 — 시뮬레이션이 0~1000 mm 를 훑어 영역마다 높이를 고르고, 지령은 영역 저장값(<c>station_lift_mm</c>)을 싣는다.</summary>
    Auto,
    /// <summary>고정 — 리프트를 한 높이에 세워 두고 쓴다. 시뮬레이션 후보·지령 모두 그 높이(영역 저장값 무시).</summary>
    Fixed,
}

/// <summary>
/// 리프트 설정 — <c>Acs:Lift:Mode</c>(Off | Auto | Fixed) + <c>Acs:Lift:FixedStrokeMm</c>.
/// Mode 가 없으면 종전 키 <c>Acs:Lift:Enabled</c>(true = Auto, false = Off, 없으면 Auto)를 따른다.
/// Fixed 인데 고정 높이가 없거나 0~1000 밖이면 0 으로 둔다(가장 낮은 높이 — 엉뚱한 높이로 지령이 나가지 않게).
/// </summary>
public sealed record LiftSettings(LiftMode Mode, double? FixedStrokeMm, string? Warning = null)
{
    public const double MaxStrokeMm = 1000;

    /// <summary>리프트를 쓰는가(자동·고정) — 리프트 기둥이 간섭 판정에 들어가고 BASE 가 MountZMm + 높이.</summary>
    public bool Enabled => Mode != LiftMode.Off;

    /// <summary>지령에 실을 높이 — 미사용 0, 고정이면 고정 높이, 자동이면 영역 저장값(없으면 0).</summary>
    public double ForOrder(double? areaLiftMm) => Mode switch
    {
        LiftMode.Off => 0,
        LiftMode.Fixed => FixedStrokeMm ?? 0,
        _ => areaLiftMm ?? 0,
    };

    public static LiftSettings Auto { get; } = new(LiftMode.Auto, null);

    public static LiftSettings From(IConfiguration? config)
    {
        if (config is null) return Auto;
        var raw = config["Acs:Lift:Mode"];
        if (string.IsNullOrWhiteSpace(raw))
            return config.GetValue("Acs:Lift:Enabled", true) ? Auto : new LiftSettings(LiftMode.Off, null);
        if (!Enum.TryParse<LiftMode>(raw.Trim(), ignoreCase: true, out var mode) || !Enum.IsDefined(mode))
            return Auto with { Warning = $"Acs:Lift:Mode '{raw}' 는 Off·Auto·Fixed 가 아님 — Auto 로 동작" };
        if (mode != LiftMode.Fixed) return new LiftSettings(mode, null);
        // 숫자가 아닌 값도 예외 없이 받는다(GetValue<double> 는 형식 오류에 예외 — 서버 기동·배차가 멈춘다)
        var text = config["Acs:Lift:FixedStrokeMm"];
        return double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v)
               && double.IsFinite(v) && v >= 0 && v <= MaxStrokeMm
            ? new LiftSettings(LiftMode.Fixed, v)
            : new LiftSettings(LiftMode.Fixed, 0, $"Acs:Lift:FixedStrokeMm 가 없거나 0~{MaxStrokeMm:0} 밖({text ?? "없음"}) — 0 mm 로 고정");
    }
}
