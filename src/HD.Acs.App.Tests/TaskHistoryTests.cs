using System.Text.Json;
using HD.Acs.App.Services;
using HD.Acs.Data;
using HD.Acs.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace HD.Acs.App.Tests;

/// <summary>선창 전체 누적 검사 기록 — 용접선마다 run과 무관하게 가장 최근 결과 한 건.</summary>
public class TaskHistoryTests
{
    [Fact]
    public async Task Latest_IsMostRecentResultAcrossRuns_PerTask()
    {
        var db = new AcsDbContext(new DbContextOptionsBuilder<AcsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        Guid area = Guid.NewGuid(), otherTankArea = Guid.NewGuid();
        Guid tFlip = Guid.NewGuid(), tFixed = Guid.NewGuid(), tAborted = Guid.NewGuid(),
             tNever = Guid.NewGuid(), tRunning = Guid.NewGuid(), tOther = Guid.NewGuid();
        Guid run1 = Guid.NewGuid(), run2 = Guid.NewGuid();
        Guid wi1 = Guid.NewGuid(), wi2 = Guid.NewGuid(), wiBack = Guid.NewGuid();

        db.InspectionAreas.Add(new InspectionAreaEntity { AreaId = area, TankId = "CT1", WallCode = "F", Level = 1, Name = "A", Corners = "[]" });
        db.InspectionAreas.Add(new InspectionAreaEntity { AreaId = otherTankArea, TankId = "CT2", WallCode = "F", Level = 1, Name = "B", Corners = "[]" });
        int seq = 0;
        foreach (var t in new[] { tFlip, tFixed, tAborted, tNever, tRunning })
            db.AreaTasks.Add(new AreaTaskEntity { TaskId = t, AreaId = area, Seq = ++seq, SectionDxfId = "D", ProfileId = "P" });
        db.AreaTasks.Add(new AreaTaskEntity { TaskId = tOther, AreaId = otherTankArea, Seq = 1, SectionDxfId = "D", ProfileId = "P" });

        db.WorkItems.Add(new WorkItemEntity { WorkItemId = wi1, RunId = run1, AreaId = area, MapId = "CT1-L1", Status = "DONE" });
        db.WorkItems.Add(new WorkItemEntity { WorkItemId = wi2, RunId = run2, AreaId = area, MapId = "CT1-L1", Status = "FAILED" });
        db.WorkItems.Add(new WorkItemEntity { WorkItemId = wiBack, RunId = run2, AreaId = area, MapId = "CT1-L1", Status = "PENDING" });

        var t0 = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
        OrderActionEntity Act(Guid task, Guid wi, string status, int day, string? desc = null) => new()
        {
            ActionId = Guid.NewGuid(), MissionId = Guid.NewGuid(), WorkItemId = wi, TaskId = task,
            ActionType = "startWeldInspection", Status = status, CreatedAt = t0.AddDays(day), Attempts = day + 1,
            Result = desc is null ? null : JsonSerializer.Serialize(new { ActionStatus = status, ResultDescription = desc }),
        };
        db.OrderActions.AddRange(
            Act(tFlip, wi1, "FINISHED", 0), Act(tFlip, wi2, "FAILED", 5, "IK 실패"),        // 지난번 성공 → 이번 실패
            Act(tFixed, wi1, "FAILED", 0, "vision timeout"), Act(tFixed, wi2, "FINISHED", 5), // 지난번 실패 → 이번 성공
            Act(tAborted, wi1, "FINISHED", 0), Act(tAborted, wiBack, "FAILED", 5, "cancelOrder"), // 중단으로 끊김 — 결과 아님
            Act(tRunning, wi2, "RUNNING", 5),                                                 // 아직 안 끝남
            Act(tOther, wi1, "FINISHED", 0));                                                 // 다른 선창
        await db.SaveChangesAsync();

        var rows = (await TaskHistoryQuery.LatestAsync(db, "CT1")).ToDictionary(r => r.TaskId);

        Assert.Equal(3, rows.Count);                       // 미검사·진행 중·다른 선창은 없음
        Assert.Equal(("FAILED", "IK 실패", (Guid?)run2), (rows[tFlip].Status, rows[tFlip].Reason, rows[tFlip].RunId));
        Assert.Equal(("FINISHED", (string?)null), (rows[tFixed].Status, rows[tFixed].Reason));
        Assert.Equal("FINISHED", rows[tAborted].Status);   // 중단된 시도는 세지 않고 그 전 결과 유지
        Assert.Equal(t0.AddDays(5), rows[tFlip].At);
        Assert.Equal(area, rows[tFlip].AreaId);
    }
}
