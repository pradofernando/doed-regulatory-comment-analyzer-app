using Microsoft.EntityFrameworkCore;

namespace DoedRegulatoryComments.Web.Data;

public class AnalysisDbContext : DbContext
{
    public AnalysisDbContext(DbContextOptions<AnalysisDbContext> options) : base(options) { }

    public DbSet<StoredAnalysisRun> Runs => Set<StoredAnalysisRun>();
    public DbSet<StoredCategorization> Categorizations => Set<StoredCategorization>();
    public DbSet<StoredThemeGroup> ThemeGroups => Set<StoredThemeGroup>();
    public DbSet<StoredFollowUpTurn> FollowUpTurns => Set<StoredFollowUpTurn>();
    public DbSet<StoredWorkspaceDocument> WorkspaceDocuments => Set<StoredWorkspaceDocument>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<StoredAnalysisRun>(e =>
        {
            e.HasIndex(x => x.DocumentId);
            e.HasIndex(x => x.StartedAt);
            e.HasMany(x => x.Categorizations).WithOne(x => x.Run!).HasForeignKey(x => x.RunId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.ThemeGroups).WithOne(x => x.Run!).HasForeignKey(x => x.RunId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.FollowUpHistory).WithOne(x => x.Run!).HasForeignKey(x => x.RunId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<StoredWorkspaceDocument>(e =>
        {
            e.HasKey(x => new { x.Kind, x.Id });
            var collation = Database.IsSqlServer() ? "Latin1_General_100_BIN2" : "BINARY";
            e.Property(x => x.Kind).UseCollation(collation);
            e.Property(x => x.Id).UseCollation(collation);
            e.Property(x => x.Version).UseCollation(collation).IsConcurrencyToken();
        });
    }
}
