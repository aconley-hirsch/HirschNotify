using EventAlertService.Models;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace EventAlertService.Data;

public class AppDbContext : IdentityDbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Recipient> Recipients => Set<Recipient>();
    public DbSet<FilterRule> FilterRules => Set<FilterRule>();
    public DbSet<FilterCondition> FilterConditions => Set<FilterCondition>();
    public DbSet<FilterRuleRecipient> FilterRuleRecipients => Set<FilterRuleRecipient>();
    public DbSet<ThrottleState> ThrottleStates => Set<ThrottleState>();
    public DbSet<AppSetting> AppSettings => Set<AppSetting>();
    public DbSet<RecipientGroup> RecipientGroups => Set<RecipientGroup>();
    public DbSet<RecipientGroupMember> RecipientGroupMembers => Set<RecipientGroupMember>();
    public DbSet<FilterRuleRecipientGroup> FilterRuleRecipientGroups => Set<FilterRuleRecipientGroup>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<Recipient>(e =>
        {
            e.HasKey(r => r.Id);
            e.Property(r => r.Name).HasMaxLength(100).IsRequired();
        });

        builder.Entity<FilterRule>(e =>
        {
            e.HasKey(r => r.Id);
            e.Property(r => r.Name).HasMaxLength(200).IsRequired();
            e.Property(r => r.Description).HasMaxLength(500);
            e.Property(r => r.LogicOperator).HasMaxLength(3).IsRequired();
            e.Property(r => r.MessageTemplate).HasMaxLength(1000);
        });

        builder.Entity<FilterCondition>(e =>
        {
            e.HasKey(c => c.Id);
            e.Property(c => c.FieldPath).HasMaxLength(200).IsRequired();
            e.Property(c => c.Operator).HasMaxLength(20).IsRequired();
            e.Property(c => c.Value).HasMaxLength(500).IsRequired();
            e.HasOne(c => c.FilterRule)
                .WithMany(r => r.Conditions)
                .HasForeignKey(c => c.FilterRuleId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<FilterRuleRecipient>(e =>
        {
            e.HasKey(fr => new { fr.FilterRuleId, fr.RecipientId });
            e.HasOne(fr => fr.FilterRule)
                .WithMany(r => r.FilterRuleRecipients)
                .HasForeignKey(fr => fr.FilterRuleId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(fr => fr.Recipient)
                .WithMany(r => r.FilterRuleRecipients)
                .HasForeignKey(fr => fr.RecipientId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<ThrottleState>(e =>
        {
            e.HasKey(t => t.Id);
            e.HasOne(t => t.FilterRule).WithMany().HasForeignKey(t => t.FilterRuleId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(t => t.Recipient).WithMany().HasForeignKey(t => t.RecipientId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(t => new { t.FilterRuleId, t.RecipientId });
        });

        builder.Entity<AppSetting>(e =>
        {
            e.HasKey(s => s.Key);
            e.Property(s => s.Key).HasMaxLength(100);
            e.Property(s => s.Value).HasMaxLength(500);
        });

        builder.Entity<RecipientGroup>(e =>
        {
            e.HasKey(g => g.Id);
            e.Property(g => g.Name).HasMaxLength(100).IsRequired();
            e.Property(g => g.Description).HasMaxLength(200);
        });

        builder.Entity<RecipientGroupMember>(e =>
        {
            e.HasKey(m => new { m.RecipientGroupId, m.RecipientId });
            e.HasOne(m => m.RecipientGroup)
                .WithMany(g => g.Members)
                .HasForeignKey(m => m.RecipientGroupId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(m => m.Recipient)
                .WithMany()
                .HasForeignKey(m => m.RecipientId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<FilterRuleRecipientGroup>(e =>
        {
            e.HasKey(fg => new { fg.FilterRuleId, fg.RecipientGroupId });
            e.HasOne(fg => fg.FilterRule)
                .WithMany(r => r.FilterRuleRecipientGroups)
                .HasForeignKey(fg => fg.FilterRuleId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(fg => fg.RecipientGroup)
                .WithMany(g => g.FilterRuleRecipientGroups)
                .HasForeignKey(fg => fg.RecipientGroupId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
