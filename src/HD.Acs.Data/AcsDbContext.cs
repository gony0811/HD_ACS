using HD.Acs.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace HD.Acs.Data;

/// <summary>
/// HD_ACS DbContext — db/schema.sql 대응 (네이밍 C안: PostgreSQL 스키마 + snake_case).
/// EF Core code-first, 폐쇄망 배포 시 마이그레이션 SQL export [ADR-009].
/// </summary>
public class AcsDbContext : DbContext
{
    public AcsDbContext(DbContextOptions<AcsDbContext> options) : base(options) { }

    // ref
    public DbSet<MapEntity> Maps => Set<MapEntity>();
    public DbSet<MapCalibrationEntity> MapCalibrations => Set<MapCalibrationEntity>();
    public DbSet<MapCalibrationPointEntity> MapCalibrationPoints => Set<MapCalibrationPointEntity>();
    public DbSet<NodeEntity> Nodes => Set<NodeEntity>();
    public DbSet<EdgeEntity> Edges => Set<EdgeEntity>();
    public DbSet<ZoneEntity> Zones => Set<ZoneEntity>();
    public DbSet<ZoneMemberEntity> ZoneMembers => Set<ZoneMemberEntity>();
    public DbSet<ActionCatalogEntity> ActionCatalog => Set<ActionCatalogEntity>();
    public DbSet<ScenarioEntity> Scenarios => Set<ScenarioEntity>();
    public DbSet<InspectionPointEntity> InspectionPoints => Set<InspectionPointEntity>();
    public DbSet<InspectionTaskEntity> InspectionTasks => Set<InspectionTaskEntity>();
    public DbSet<RobotEntity> Robots => Set<RobotEntity>();
    public DbSet<WeldSeamEntity> WeldSeams => Set<WeldSeamEntity>();
    public DbSet<TankGeometryEntity> TankGeometries => Set<TankGeometryEntity>();
    public DbSet<WallEntity> Walls => Set<WallEntity>();
    public DbSet<InspectionAreaEntity> InspectionAreas => Set<InspectionAreaEntity>();
    public DbSet<AreaTaskEntity> AreaTasks => Set<AreaTaskEntity>();
    // run
    public DbSet<ScenarioRunEntity> ScenarioRuns => Set<ScenarioRunEntity>();
    public DbSet<MissionEntity> Missions => Set<MissionEntity>();
    public DbSet<OrderNodeEntity> OrderNodes => Set<OrderNodeEntity>();
    public DbSet<OrderEdgeEntity> OrderEdges => Set<OrderEdgeEntity>();
    public DbSet<OrderActionEntity> OrderActions => Set<OrderActionEntity>();
    public DbSet<RobotContextEntity> RobotContexts => Set<RobotContextEntity>();
    public DbSet<WorkItemEntity> WorkItems => Set<WorkItemEntity>();
    // hist / alarm / sys
    public DbSet<TransitionLogEntity> TransitionLogs => Set<TransitionLogEntity>();
    public DbSet<InspectionResultEntity> InspectionResults => Set<InspectionResultEntity>();
    public DbSet<AlarmSpecEntity> AlarmSpecs => Set<AlarmSpecEntity>();
    public DbSet<AlarmEntity> Alarms => Set<AlarmEntity>();
    public DbSet<AppUserEntity> Users => Set<AppUserEntity>();
    public DbSet<AuditLogEntity> AuditLogs => Set<AuditLogEntity>();

    protected override void OnModelCreating(ModelBuilder mb)
    {
        // ═══ ref ═══
        mb.Entity<MapEntity>(e => { e.ToTable("map", "ref"); e.HasKey(x => x.MapId);
            e.HasIndex(x => new { x.TankId, x.Level, x.Version }).IsUnique(); });

        mb.Entity<MapCalibrationEntity>(e => { e.ToTable("map_calibration", "ref");
            e.HasKey(x => new { x.MapId, x.MapVersion }); });

        mb.Entity<MapCalibrationPointEntity>(e => { e.ToTable("map_calibration_point", "ref");
            e.HasKey(x => x.Id); e.HasIndex(x => new { x.MapId, x.MapVersion });
            // snake_case 컨벤션은 DrawingXM→drawing_xm 으로 만들지만 DDL은 drawing_x_m (단위 접미사 _m 분리) → 명시 오버라이드
            e.Property(x => x.DrawingXM).HasColumnName("drawing_x_m");
            e.Property(x => x.DrawingYM).HasColumnName("drawing_y_m"); });

        mb.Entity<NodeEntity>(e => { e.ToTable("node", "ref"); e.HasKey(x => x.NodeId);
            e.HasIndex(x => x.MapId);
            e.Property(x => x.Metadata).HasColumnType("jsonb"); });

        mb.Entity<EdgeEntity>(e => { e.ToTable("edge", "ref"); e.HasKey(x => x.EdgeId);
            e.HasIndex(x => x.MapId);
            e.Property(x => x.Metadata).HasColumnType("jsonb"); });

        mb.Entity<ZoneEntity>(e => { e.ToTable("zone", "ref"); e.HasKey(x => x.ZoneId);
            e.Property(x => x.Geometry).HasColumnType("jsonb"); });

        mb.Entity<ZoneMemberEntity>(e => { e.ToTable("zone_member", "ref");
            e.HasKey(x => new { x.ZoneId, x.NodeId }); });

        mb.Entity<ActionCatalogEntity>(e => { e.ToTable("action_catalog", "ref");
            e.HasKey(x => x.ActionType);
            e.Property(x => x.ParamSchema).HasColumnType("jsonb"); });

        mb.Entity<ScenarioEntity>(e => { e.ToTable("scenario", "ref"); e.HasKey(x => x.ScenarioId);
            e.HasIndex(x => new { x.Name, x.Version }).IsUnique();
            e.Property(x => x.Policy).HasColumnType("jsonb");
            e.HasMany(x => x.Points).WithOne().HasForeignKey(p => p.ScenarioId)
                .OnDelete(DeleteBehavior.Cascade); });

        mb.Entity<InspectionPointEntity>(e => { e.ToTable("inspection_point", "ref");
            e.HasKey(x => x.PointId);
            e.HasIndex(x => new { x.ScenarioId, x.Seq }).IsUnique();
            e.HasMany(x => x.Tasks).WithOne().HasForeignKey(t => t.PointId)
                .OnDelete(DeleteBehavior.Cascade); });

        mb.Entity<InspectionTaskEntity>(e => { e.ToTable("inspection_task", "ref");
            e.HasKey(x => x.TaskId);
            e.HasIndex(x => new { x.PointId, x.Seq }).IsUnique();
            e.Property(x => x.Position).HasColumnType("jsonb");
            e.Property(x => x.Params).HasColumnType("jsonb"); });

        mb.Entity<RobotEntity>(e => { e.ToTable("robot", "ref"); e.HasKey(x => x.RobotId);
            e.HasIndex(x => new { x.Manufacturer, x.SerialNumber }).IsUnique(); });

        mb.Entity<WeldSeamEntity>(e => { e.ToTable("weld_seam", "ref"); e.HasKey(x => x.SeamId);
            e.HasIndex(x => new { x.TankId, x.Level });
            e.Property(x => x.PathDrawing).HasColumnType("jsonb");
            e.Property(x => x.NormalDrawing).HasColumnType("jsonb"); });

        mb.Entity<TankGeometryEntity>(e => { e.ToTable("tank_geometry", "ref"); e.HasKey(x => x.TankId);
            e.Property(x => x.LevelZ).HasColumnType("jsonb"); });

        mb.Entity<WallEntity>(e => { e.ToTable("wall", "ref"); e.HasKey(x => new { x.TankId, x.WallCode });
            e.Property(x => x.Origin).HasColumnType("jsonb");
            e.Property(x => x.UAxis).HasColumnType("jsonb");
            e.Property(x => x.VAxis).HasColumnType("jsonb");
            e.Property(x => x.Normal).HasColumnType("jsonb"); });

        mb.Entity<InspectionAreaEntity>(e => { e.ToTable("inspection_area", "ref"); e.HasKey(x => x.AreaId);
            e.HasIndex(x => new { x.TankId, x.WallCode, x.Name }).IsUnique();
            e.Property(x => x.Corners).HasColumnType("jsonb");
            e.HasOne<WallEntity>().WithMany().HasForeignKey(x => new { x.TankId, x.WallCode });
            e.HasMany(x => x.Tasks).WithOne().HasForeignKey(t => t.AreaId).OnDelete(DeleteBehavior.Cascade); });

        mb.Entity<AreaTaskEntity>(e => { e.ToTable("area_task", "ref"); e.HasKey(x => x.TaskId);
            e.HasIndex(x => new { x.AreaId, x.Seq }).IsUnique(); });

        // ═══ run ═══
        mb.Entity<ScenarioRunEntity>(e => { e.ToTable("scenario_run", "run");
            e.HasKey(x => x.RunId);
            e.HasMany(x => x.Missions).WithOne().HasForeignKey(m => m.RunId); });

        mb.Entity<MissionEntity>(e => { e.ToTable("mission", "run"); e.HasKey(x => x.MissionId);
            e.HasIndex(x => new { x.RunId, x.Seq }).IsUnique();
            e.HasIndex(x => new { x.RobotId, x.State }); });

        mb.Entity<OrderNodeEntity>(e => { e.ToTable("order_node", "run");
            e.HasKey(x => new { x.MissionId, x.SequenceId }); });

        mb.Entity<OrderEdgeEntity>(e => { e.ToTable("order_edge", "run");
            e.HasKey(x => new { x.MissionId, x.SequenceId }); });

        mb.Entity<OrderActionEntity>(e => { e.ToTable("order_action", "run");
            e.HasKey(x => x.ActionId);
            e.HasIndex(x => new { x.MissionId, x.NodeSequenceId });
            e.Property(x => x.Params).HasColumnType("jsonb");
            e.Property(x => x.Result).HasColumnType("jsonb"); });

        mb.Entity<RobotContextEntity>(e => { e.ToTable("robot_context", "run");
            e.HasKey(x => x.RobotId); });

        mb.Entity<WorkItemEntity>(e => { e.ToTable("work_item", "run"); e.HasKey(x => x.WorkItemId);
            e.HasIndex(x => new { x.RunId, x.MapId, x.Status });
            e.Property(x => x.Actions).HasColumnType("jsonb"); });

        // ═══ hist / alarm / sys ═══
        mb.Entity<TransitionLogEntity>(e => { e.ToTable("transition_log", "hist");
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.MissionId, x.OccurredAt });
            e.Property(x => x.Payload).HasColumnType("jsonb"); });

        mb.Entity<InspectionResultEntity>(e => { e.ToTable("inspection_result", "hist");
            e.HasKey(x => x.ResultId);
            e.HasIndex(x => x.OccurredAt);
            e.HasIndex(x => x.RunId);
            e.Property(x => x.Position).HasColumnType("jsonb"); });

        mb.Entity<AlarmSpecEntity>(e => { e.ToTable("spec", "alarm"); e.HasKey(x => x.AlarmCode); });

        mb.Entity<AlarmEntity>(e => { e.ToTable("alarm", "alarm"); e.HasKey(x => x.AlarmId);
            e.Property(x => x.Detail).HasColumnType("jsonb"); });

        mb.Entity<AppUserEntity>(e => { e.ToTable("app_user", "sys"); e.HasKey(x => x.UserId); });

        mb.Entity<AuditLogEntity>(e => { e.ToTable("audit_log", "sys"); e.HasKey(x => x.Id);
            e.Property(x => x.Detail).HasColumnType("jsonb"); });
    }
}
