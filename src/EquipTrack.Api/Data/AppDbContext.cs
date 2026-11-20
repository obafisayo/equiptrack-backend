using System.Text.Json;
using EquipTrack.Api.Leads;
using EquipTrack.Api.Organizations;
using EquipTrack.Api.WorkOrders;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace EquipTrack.Api.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Lead> Leads => Set<Lead>();
    public DbSet<Organization> Organizations => Set<Organization>();
    public DbSet<OrganizationMember> OrganizationMembers => Set<OrganizationMember>();
    public DbSet<WorkOrder> WorkOrders => Set<WorkOrder>();
    public DbSet<WorkOrderEvent> WorkOrderEvents => Set<WorkOrderEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var lead = modelBuilder.Entity<Lead>();
        lead.ToTable("leads");
        lead.HasKey(l => l.Id);

        lead.Property(l => l.Kind).HasConversion<string>().HasMaxLength(20);
        lead.Property(l => l.SyncStatus).HasConversion<string>().HasMaxLength(20);
        lead.Property(l => l.Email).HasMaxLength(254).IsRequired();

        foreach (var name in new[]
                 {
                     nameof(Lead.ContactName), nameof(Lead.ContactPhone), nameof(Lead.JobTitle),
                     nameof(Lead.CompanyName), nameof(Lead.Industry), nameof(Lead.Country),
                     nameof(Lead.CompanySize), nameof(Lead.Website),
                 })
        {
            lead.Property(name).HasMaxLength(LeadValidation.ShortMax);
        }

        lead.Property(l => l.AdditionalInfo).HasMaxLength(LeadValidation.LongMax);
        lead.Property(l => l.LastSyncError).HasMaxLength(2000);

        // Stored as a JSON string so the model stays portable across providers.
        lead.Property(l => l.UseCases)
            .HasConversion(
                v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                v => JsonSerializer.Deserialize<List<string>>(v, (JsonSerializerOptions?)null) ?? new List<string>())
            .Metadata.SetValueComparer(new ValueComparer<List<string>>(
                (a, b) => (a ?? new List<string>()).SequenceEqual(b ?? new List<string>()),
                v => v.Aggregate(0, (h, s) => HashCode.Combine(h, s.GetHashCode())),
                v => v.ToList()));

        // One newsletter subscription per address; waitlist applications may repeat.
        lead.HasIndex(l => new { l.Kind, l.Email });
        lead.HasIndex(l => new { l.SyncStatus, l.NextSyncAttemptAt });

        ConfigureOrganizations(modelBuilder);
        ConfigureWorkOrders(modelBuilder);
    }

    private static void ConfigureOrganizations(ModelBuilder modelBuilder)
    {
        var org = modelBuilder.Entity<Organization>();
        org.ToTable("organizations");
        org.HasKey(o => o.Id);
        org.Property(o => o.Name).HasMaxLength(200);
        org.Property(o => o.Slug).HasMaxLength(50);
        org.HasIndex(o => o.Slug).IsUnique();

        var m = modelBuilder.Entity<OrganizationMember>();
        m.ToTable("organization_members");
        m.HasKey(x => x.Id);
        m.Property(x => x.Email).HasMaxLength(254);
        m.Property(x => x.UserId).HasMaxLength(100);
        m.Property(x => x.DisplayName).HasMaxLength(200);
        m.Property(x => x.Role).HasMaxLength(30);
        m.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        m.Property(x => x.InvitedByUserId).HasMaxLength(100);
        m.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        m.HasIndex(x => new { x.OrganizationId, x.Email }).IsUnique();
        m.HasIndex(x => x.UserId);
        m.HasIndex(x => new { x.Email, x.Status });
    }

    private void ConfigureWorkOrders(ModelBuilder modelBuilder)
    {
        var wo = modelBuilder.Entity<WorkOrder>();
        wo.ToTable("work_orders");
        wo.HasKey(w => w.Id);
        wo.Property(w => w.Number).HasMaxLength(30);
        wo.Property(w => w.RequestType).HasConversion<string>().HasMaxLength(20);
        wo.Property(w => w.Urgency).HasConversion<string>().HasMaxLength(20);
        wo.Property(w => w.Status).HasConversion<string>().HasMaxLength(20);
        wo.Property(w => w.Stage).HasConversion<string>().HasMaxLength(40);
        wo.Property(w => w.Destination).HasMaxLength(200);
        wo.Property(w => w.Entity).HasMaxLength(100);
        wo.Property(w => w.Notes).HasMaxLength(2000);
        wo.Property(w => w.TrDepartment).HasMaxLength(100);
        wo.Property(w => w.RequesterUserId).HasMaxLength(100);
        wo.Property(w => w.RequesterName).HasMaxLength(200);
        wo.Property(w => w.AssigneeUserId).HasMaxLength(100);
        wo.Property(w => w.AssigneeName).HasMaxLength(200);
        wo.Property(w => w.ContainerRef).HasMaxLength(100);
        wo.Property(w => w.WaybillNumber).HasMaxLength(50);
        wo.Property(w => w.VesselName).HasMaxLength(100);
        wo.Property(w => w.RejectionReason).HasMaxLength(Lifecycle.MaxTextLength);

        // Postgres xmin as an optimistic concurrency token: two people acting on
        // the same order at once → the second save fails instead of overwriting.
        if (Database.IsNpgsql()) wo.Property(w => w.Version).IsRowVersion();
        else wo.Ignore(w => w.Version);

        wo.HasOne<Organization>().WithMany().HasForeignKey(w => w.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        wo.HasIndex(w => new { w.OrganizationId, w.Number }).IsUnique();
        wo.HasIndex(w => new { w.OrganizationId, w.Status, w.Stage });
        wo.HasIndex(w => new { w.OrganizationId, w.AssigneeUserId });
        wo.HasIndex(w => new { w.OrganizationId, w.RequesterUserId });

        wo.OwnsMany(w => w.Items, item =>
        {
            item.ToTable("work_order_items");
            item.WithOwner().HasForeignKey("WorkOrderId");
            item.HasKey(i => i.Id);
            item.Property(i => i.Description).HasMaxLength(500);
            item.Property(i => i.Unit).HasMaxLength(20);
            item.Property(i => i.Quantity).HasPrecision(18, 3);
            item.Property(i => i.PartNumber).HasMaxLength(50);
            item.Property(i => i.Plant).HasMaxLength(50);
            item.Property(i => i.BinLocation).HasMaxLength(50);
        });

        wo.HasMany(w => w.Events).WithOne().HasForeignKey(e => e.WorkOrderId).OnDelete(DeleteBehavior.Restrict);

        var ev = modelBuilder.Entity<WorkOrderEvent>();
        ev.ToTable("work_order_events");
        ev.HasKey(e => e.Id);
        ev.Property(e => e.Action).HasConversion<string>().HasMaxLength(40);
        ev.Property(e => e.FromStage).HasConversion<string>().HasMaxLength(40);
        ev.Property(e => e.ToStage).HasConversion<string>().HasMaxLength(40);
        ev.Property(e => e.ActorUserId).HasMaxLength(100);
        ev.Property(e => e.ActorName).HasMaxLength(200);
        ev.Property(e => e.ActorRole).HasMaxLength(30);
        ev.Property(e => e.AssigneeUserId).HasMaxLength(100);
        ev.Property(e => e.AssigneeName).HasMaxLength(200);
        ev.Property(e => e.Note).HasMaxLength(Lifecycle.MaxTextLength);
        ev.HasIndex(e => new { e.WorkOrderId, e.OccurredAt });
        ev.HasIndex(e => new { e.ActorUserId, e.OccurredAt });
    }
}
