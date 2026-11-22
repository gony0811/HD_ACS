namespace HD.Acs.UI.Models;

/// <summary>
/// 검사 종류 선택 항목 — 서버 Core InspectionKinds 와 같은 값(UI.Core 는 도메인 Core 를 참조하지 않아 미러).
/// 값이 없으면(구서버·구파일) 용접검사로 본다.
/// </summary>
public sealed record InspectionKindOption(string Code, string Label)
{
    public const string Weld = "WELD";
    public const string Gas = "GAS";

    public static readonly IReadOnlyList<InspectionKindOption> All = new[]
    {
        new InspectionKindOption(Weld, "용접검사"),
        new InspectionKindOption(Gas, "가스검사"),
    };

    public static InspectionKindOption Of(string? code) =>
        All.FirstOrDefault(o => string.Equals(o.Code, code?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? All[0];

    public override string ToString() => Label;
}
