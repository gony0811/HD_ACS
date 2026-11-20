using HD.Acs.App.Services;
using Xunit;

namespace HD.Acs.App.Tests;

/// <summary>
/// SchemaEnsure 문장은 EF ExecuteSqlRaw 로 실행된다 — EF 가 SQL 을 string.Format 으로 다뤄 중괄호 리터럴('{}')이 있으면
/// 기동 시 FormatException 으로 그 문장(과 뒤 문장)이 적용되지 않는다(2026-11-20 ref.amr_teaching 미생성 사고).
/// </summary>
public class SchemaEnsureTests
{
    [Fact]
    public void Statements_HaveNoUnescapedBraces()
    {
        foreach (var sql in SchemaEnsure.Statements)
        {
            var formatted = string.Format(sql, Array.Empty<object>());
            Assert.Equal(sql, formatted);
        }
    }
}
