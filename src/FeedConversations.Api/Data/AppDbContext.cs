using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace FeedConversations.Api.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : IdentityDbContext<Account>(options)
{
    public DbSet<Invite> Invites => Set<Invite>();
    public DbSet<Post> Posts => Set<Post>();
    public DbSet<Subscription> Subscriptions => Set<Subscription>();
    public DbSet<SavedView> Views => Set<SavedView>();
    public DbSet<InteractionReference> References => Set<InteractionReference>();
    public DbSet<MentionJob> Jobs => Set<MentionJob>();
    public DbSet<LocalChange> Changes => Set<LocalChange>();
    public DbSet<InstanceState> Instance => Set<InstanceState>();
    public DbSet<CommandReceipt> Receipts => Set<CommandReceipt>();
    public DbSet<ImportRun> ImportRuns => Set<ImportRun>();
    public DbSet<RestoredPostMapping> RestoredMappings => Set<RestoredPostMapping>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        base.OnModelCreating(model);
        model.Entity<Post>().HasOne(p => p.Owner).WithMany().HasForeignKey(p => p.OwnerId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<Post>().HasIndex(p => p.Guid).IsUnique();
        model.Entity<Post>().HasIndex(p => new { p.OwnerId, p.PublishedAt, p.Id });
        model.Entity<Post>().HasIndex(p => new { p.TargetKey, p.Kind });
        model.Entity<Post>().HasIndex(p => new { p.OwnerId, p.Kind, p.TargetKey }).IsUnique()
            .HasFilter("\"DeletedAt\" IS NULL AND \"Kind\" IN ('like', 'bookmark')");
        model.Entity<Post>().Property(p => p.Revision).IsConcurrencyToken();
        model.Entity<Post>().ToTable(t => t.HasCheckConstraint("CK_Post_Kind", "\"Kind\" IN ('post','reply','like','bookmark')"));
        model.Entity<Invite>().HasIndex(i => i.TokenHash).IsUnique();
        model.Entity<Subscription>().HasIndex(s => new { s.OwnerId, s.UrlKey }).IsUnique();
        model.Entity<Subscription>().Property(s => s.Revision).IsConcurrencyToken();
        model.Entity<SavedView>().HasIndex(v => new { v.OwnerId, v.Name }).IsUnique();
        model.Entity<SavedView>().Property(v => v.Revision).IsConcurrencyToken();
        model.Entity<Account>().Property(a => a.Revision).IsConcurrencyToken();
        model.Entity<InteractionReference>().HasIndex(r => new { r.TargetId, r.SourceKey }).IsUnique();
        model.Entity<MentionJob>().HasIndex(j => new { j.Direction, j.SourceUrl, j.TargetUrl }).IsUnique();
        model.Entity<MentionJob>().HasIndex(j => new { j.State, j.DueAt });
        model.Entity<LocalChange>().HasKey(c => c.Sequence);
        model.Entity<LocalChange>().Property(c => c.Sequence).ValueGeneratedOnAdd();
        model.Entity<LocalChange>().HasIndex(c => c.At);
        model.Entity<CommandReceipt>().HasIndex(r => new { r.OwnerId, r.Operation, r.Key }).IsUnique();
        model.Entity<ImportRun>().HasIndex(r => new { r.OwnerId, r.Checksum }).IsUnique();
        model.Entity<RestoredPostMapping>().HasIndex(m => new { m.OwnerId, m.SourceIdentity, m.OriginalGuid }).IsUnique();
        var utc = new ValueConverter<DateTime, DateTime>(value => value, value => DateTime.SpecifyKind(value, DateTimeKind.Utc));
        foreach (var entity in model.Model.GetEntityTypes())
            foreach (var property in entity.GetProperties())
                if (property.ClrType == typeof(DateTime) || property.ClrType == typeof(DateTime?))
                    property.SetValueConverter(utc);
    }
}
