using CRM.Models;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace CRM.Data;

public class ApplicationDbContext : IdentityUserContext<User, int>
{
    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
        : base(options)
    {
    }

    public DbSet<Role> Roles { get; set; } = null!;
    public DbSet<Contact> Contacts { get; set; } = null!;
    public DbSet<PipelineStage> PipelineStages { get; set; } = null!;
    public DbSet<Opportunity> Opportunities { get; set; } = null!;
    public DbSet<Activity> Activities { get; set; } = null!;
    public DbSet<Note> Notes { get; set; } = null!;
    public DbSet<Property> Properties { get; set; } = null!;
    public DbSet<Document> Documents { get; set; } = null!;

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        StampAuditedEntries();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        StampAuditedEntries();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void StampAuditedEntries()
    {
        var now = DateTime.UtcNow;
        foreach (var entry in ChangeTracker.Entries<IAuditableEntity>())
        {
            if (entry.State == EntityState.Added)
            {
                entry.Entity.CreatedAt = now;
                entry.Entity.UpdatedAt = null;
                entry.Entity.IsDeleted = false;
            }
            else if (entry.State is EntityState.Modified or EntityState.Deleted)
            {
                if (entry.Entity is Activity && entry.OriginalValues.GetValue<bool>(nameof(Activity.IsSystemGenerated)))
                    throw new InvalidOperationException("Generated activity history cannot be changed or deleted.");
                if (entry.State == EntityState.Deleted)
                {
                    if (entry.Entity is User)
                        throw new InvalidOperationException("Users must be deactivated, not deleted.");
                    entry.State = EntityState.Modified;
                    entry.Entity.IsDeleted = true;
                }
                entry.Property(nameof(IAuditableEntity.CreatedAt)).IsModified = false;
                entry.Entity.CreatedAt = entry.OriginalValues.GetValue<DateTime>(nameof(IAuditableEntity.CreatedAt));
                entry.Entity.UpdatedAt = now;
            }
            if (entry.State != EntityState.Unchanged && entry.Entity is Contact contact)
            {
                contact.Email = string.IsNullOrWhiteSpace(contact.Email) ? null : contact.Email.Trim().ToLowerInvariant();
                contact.Phone = string.IsNullOrWhiteSpace(contact.Phone) ? null : contact.Phone.Trim();
            }
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Keep existing CRM keys and relationships while adding Identity credential fields.
        modelBuilder.Entity<User>().ToTable("Users");
        modelBuilder.Entity<User>().Property(u => u.Email).IsRequired().HasMaxLength(150);
        modelBuilder.Entity<User>().HasIndex(u => u.NormalizedEmail).IsUnique();
        modelBuilder.Entity<User>().Property(u => u.PhoneNumber).HasColumnName("Phone").HasMaxLength(30);
        modelBuilder.Entity<User>().Property(u => u.IsActive).HasDefaultValue(true);
        modelBuilder.Entity<User>().ToTable(t => t.HasCheckConstraint("CK_Users_NotDeleted", "\"IsDeleted\" = false"));

        modelBuilder.Entity<Contact>().HasQueryFilter(e => !e.IsDeleted);
        modelBuilder.Entity<Property>().HasQueryFilter(e => !e.IsDeleted);
        modelBuilder.Entity<Opportunity>().HasQueryFilter(e => !e.IsDeleted);
        modelBuilder.Entity<Activity>().HasQueryFilter(e => !e.IsDeleted);
        modelBuilder.Entity<Note>().HasQueryFilter(e => !e.IsDeleted);
        modelBuilder.Entity<Document>().HasQueryFilter(e => !e.IsDeleted);
        foreach (var entity in modelBuilder.Model.GetEntityTypes().Where(e => typeof(IAuditableEntity).IsAssignableFrom(e.ClrType)))
        {
            modelBuilder.Entity(entity.ClrType).Property(nameof(IAuditableEntity.IsDeleted)).HasDefaultValue(false);
            modelBuilder.Entity(entity.ClrType).Property(nameof(IAuditableEntity.CreatedAt)).HasDefaultValueSql("CURRENT_TIMESTAMP");
        }

        modelBuilder.Entity<Contact>().HasIndex(c => c.Email).IsUnique().HasFilter("\"IsDeleted\" = false AND \"Email\" IS NOT NULL");
        modelBuilder.Entity<Contact>().HasIndex(c => c.Phone).IsUnique().HasFilter("\"IsDeleted\" = false AND \"Phone\" IS NOT NULL");
        modelBuilder.Entity<Contact>().ToTable(t =>
        {
            t.HasCheckConstraint("CK_Contacts_Identity", "length(trim(\"FirstName\")) > 0 AND length(trim(\"LastName\")) > 0 AND (NULLIF(trim(\"Email\"), '') IS NOT NULL OR NULLIF(trim(\"Phone\"), '') IS NOT NULL)");
            t.HasCheckConstraint("CK_Contacts_Type", "\"Type\" IN ('Buyer', 'Seller', 'Both')");
            t.HasCheckConstraint("CK_Contacts_Status", "\"Status\" IN ('Lead', 'Active', 'Client', 'Inactive')");
            t.HasCheckConstraint("CK_Contacts_Source", "\"Source\" IN ('Walk-in', 'Referral', 'Website', 'Facebook', 'Phone', 'Other')");
        });
        modelBuilder.Entity<Property>().ToTable(t =>
        {
            t.HasCheckConstraint("CK_Properties_RequiredDetails", "length(trim(\"Address\")) > 0 AND length(trim(\"City\")) > 0 AND length(trim(\"PropertyType\")) > 0");
            t.HasCheckConstraint("CK_Properties_Status", "\"Status\" IN ('Draft', 'Available', 'Reserved', 'Sold', 'Withdrawn')");
            t.HasCheckConstraint("CK_Properties_Price", "\"Status\" IN ('Draft', 'Withdrawn') OR (\"Price\" IS NOT NULL AND \"Price\" > 0)");
            t.HasCheckConstraint("CK_Properties_Bedrooms", "\"Bedrooms\" IS NULL OR \"Bedrooms\" >= 0");
            t.HasCheckConstraint("CK_Properties_Retention", "\"IsDeleted\" = false OR \"Status\" NOT IN ('Reserved', 'Sold')");
        });
        modelBuilder.Entity<Opportunity>().Property(o => o.FinalPrice).HasPrecision(18, 2);
        modelBuilder.Entity<Opportunity>().Property(o => o.CommissionRate).HasPrecision(9, 6);
        modelBuilder.Entity<Opportunity>().HasIndex(o => new { o.BuyerContactId, o.PropertyId }).IsUnique()
            .HasFilter("\"IsDeleted\" = false AND \"Status\" = 'Open'");
        modelBuilder.Entity<Opportunity>().ToTable(t =>
        {
            t.HasCheckConstraint("CK_Opportunities_Rate", "\"CommissionRate\" >= 0 AND \"CommissionRate\" <= 1");
            t.HasCheckConstraint("CK_Opportunities_Offer", "\"OfferAmount\" IS NULL OR \"OfferAmount\" > 0");
            t.HasCheckConstraint("CK_Opportunities_Retention", "\"IsDeleted\" = false OR \"Status\" <> 'Open'");
            t.HasCheckConstraint("CK_Opportunities_Outcome", "(\"Status\" = 'Open' AND \"FinalPrice\" IS NULL AND \"LostReason\" IS NULL AND \"ClosedAt\" IS NULL) OR (\"Status\" = 'Won' AND \"FinalPrice\" IS NOT NULL AND \"FinalPrice\" > 0 AND \"LostReason\" IS NULL AND \"ClosedAt\" IS NOT NULL) OR (\"Status\" = 'Lost' AND \"FinalPrice\" IS NULL AND \"LostReason\" IS NOT NULL AND \"LostReason\" IN ('Price', 'Bought elsewhere', 'Financing', 'Changed mind', 'Property sold', 'No response', 'Other') AND \"ClosedAt\" IS NOT NULL)");
        });
        modelBuilder.Entity<PipelineStage>().HasIndex(s => s.Code).IsUnique();
        modelBuilder.Entity<PipelineStage>().HasIndex(s => s.SortOrder).IsUnique();
        modelBuilder.Entity<PipelineStage>().ToTable(t => t.HasCheckConstraint("CK_PipelineStages_Seed",
            "(\"Code\" = 'INQUIRY' AND \"SortOrder\" = 1) OR (\"Code\" = 'VIEWING' AND \"SortOrder\" = 2) OR (\"Code\" = 'OFFER' AND \"SortOrder\" = 3) OR (\"Code\" = 'NEGOTIATION' AND \"SortOrder\" = 4) OR (\"Code\" = 'RESERVATION' AND \"SortOrder\" = 5) OR (\"Code\" = 'CLOSING' AND \"SortOrder\" = 6)"));
        modelBuilder.Entity<PipelineStage>().HasData(
            new PipelineStage { Id = 1, Code = "INQUIRY", Name = "Inquiry", SortOrder = 1 },
            new PipelineStage { Id = 2, Code = "VIEWING", Name = "Viewing", SortOrder = 2 },
            new PipelineStage { Id = 3, Code = "OFFER", Name = "Offer", SortOrder = 3 },
            new PipelineStage { Id = 4, Code = "NEGOTIATION", Name = "Negotiation", SortOrder = 4 },
            new PipelineStage { Id = 5, Code = "RESERVATION", Name = "Reservation", SortOrder = 5 },
            new PipelineStage { Id = 6, Code = "CLOSING", Name = "Closing", SortOrder = 6 });
        modelBuilder.Entity<Activity>().ToTable(t =>
        {
            t.HasCheckConstraint("CK_Activities_Type", "\"Type\" IN ('Call', 'Email', 'Meeting', 'Viewing', 'Task', 'Stage Change', 'System')");
            t.HasCheckConstraint("CK_Activities_Subject", "length(trim(\"Subject\")) > 0");
            t.HasCheckConstraint("CK_Activities_Time", "\"ScheduledAt\" IS NOT NULL OR \"CompletedAt\" IS NOT NULL");
            t.HasCheckConstraint("CK_Activities_Viewing", "\"Type\" <> 'Viewing' OR \"OpportunityId\" IS NOT NULL");
            t.HasCheckConstraint("CK_Activities_Generated", "(\"IsSystemGenerated\" = false AND \"Type\" NOT IN ('Stage Change', 'System') AND \"FromStageId\" IS NULL AND \"ToStageId\" IS NULL) OR (\"IsSystemGenerated\" = true AND \"Type\" IN ('Stage Change', 'System') AND \"CompletedAt\" IS NOT NULL AND \"ScheduledAt\" IS NULL AND NULLIF(trim(\"Details\"), '') IS NOT NULL AND \"IsDeleted\" = false AND ((\"Type\" = 'Stage Change' AND \"FromStageId\" IS NOT NULL AND \"ToStageId\" IS NOT NULL) OR (\"Type\" = 'System' AND \"FromStageId\" IS NULL AND \"ToStageId\" IS NULL)))");
        });
        modelBuilder.Entity<Note>().ToTable(t => t.HasCheckConstraint("CK_Notes_Target", "\"EntityType\" IN ('Contact', 'Property', 'Opportunity') AND \"EntityId\" > 0"));
        modelBuilder.Entity<Document>().ToTable(t =>
        {
            t.HasCheckConstraint("CK_Documents_Target", "\"EntityType\" IN ('Contact', 'Property', 'Opportunity') AND \"EntityId\" > 0");
            t.HasCheckConstraint("CK_Documents_Category", "\"Category\" IN ('ID', 'Offer', 'Contract', 'Title', 'Photo', 'Other')");
        });

        modelBuilder.Entity<User>()
            .HasIndex(u => u.Email)
            .IsUnique();

        modelBuilder.Entity<Opportunity>()
            .Property(o => o.OfferAmount)
            .HasPrecision(18, 2);

        modelBuilder.Entity<Property>()
            .Property(p => p.Price)
            .HasPrecision(18, 2);

        modelBuilder.Entity<Role>()
            .HasMany(r => r.Users)
            .WithOne(u => u.Role)
            .HasForeignKey(u => u.RoleId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<User>()
            .HasMany(u => u.OwnedOpportunities)
            .WithOne(o => o.Agent)
            .HasForeignKey(o => o.AgentId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<User>()
            .HasMany(u => u.OwnedContacts)
            .WithOne(c => c.Owner)
            .HasForeignKey(c => c.OwnerId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<User>()
            .HasMany(u => u.Notes)
            .WithOne(n => n.Author)
            .HasForeignKey(n => n.AuthorId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<Contact>()
            .HasMany(c => c.Activities)
            .WithOne(a => a.Contact)
            .HasForeignKey(a => a.ContactId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<PipelineStage>()
            .HasMany(p => p.Opportunities)
            .WithOne(o => o.Stage)
            .HasForeignKey(o => o.StageId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<Property>()
            .HasOne(p => p.SellerContact)
            .WithMany(c => c.ListedAsSeller)
            .HasForeignKey(p => p.SellerContactId)
            .IsRequired()
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<Property>()
            .HasOne(p => p.ListingAgent)
            .WithMany(u => u.ListedProperties)
            .HasForeignKey(p => p.ListingAgentId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<Opportunity>()
            .HasOne(o => o.BuyerContact)
            .WithMany(c => c.Purchases)
            .HasForeignKey(o => o.BuyerContactId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<Opportunity>()
            .HasOne(o => o.Property)
            .WithMany(p => p.Opportunities)
            .HasForeignKey(o => o.PropertyId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<Opportunity>()
            .HasMany(o => o.Activities)
            .WithOne(a => a.Opportunity)
            .HasForeignKey(a => a.OpportunityId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<Activity>().HasOne(a => a.Property).WithMany(p => p.Activities)
            .HasForeignKey(a => a.PropertyId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<Activity>().HasOne(a => a.User).WithMany(u => u.Activities)
            .HasForeignKey(a => a.UserId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<Activity>().HasOne(a => a.CreatedBy).WithMany(u => u.CreatedActivities)
            .HasForeignKey(a => a.CreatedById).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<Activity>().HasOne(a => a.FromStage).WithMany(s => s.FromActivities)
            .HasForeignKey(a => a.FromStageId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<Activity>().HasOne(a => a.ToStage).WithMany(s => s.ToActivities)
            .HasForeignKey(a => a.ToStageId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<Document>().HasOne(d => d.UploadedBy).WithMany(u => u.Documents)
            .HasForeignKey(d => d.UploadedById).OnDelete(DeleteBehavior.Restrict);
    }
}


