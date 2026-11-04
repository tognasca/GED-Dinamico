using Ged.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace Ged.Api.Data;

public sealed class GedDbContext(DbContextOptions<GedDbContext> options) : DbContext(options)
{
    public DbSet<GedDocument> Documents => Set<GedDocument>();
    public DbSet<DocumentType> DocumentTypes => Set<DocumentType>();
    public DbSet<ProcessingBatch> Batches => Set<ProcessingBatch>();
    public DbSet<PhysicalLocation> Locations => Set<PhysicalLocation>();
    public DbSet<PhysicalBox> Boxes => Set<PhysicalBox>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        base.OnModelCreating(model);

        // Field definitions are serialized inside DocumentType.FieldsJson,
        // not stored as independent EF entities.
        model.Ignore<FieldDefinition>();
        model.Ignore<FieldResult>();

        model.Entity<DocumentType>().Ignore(x => x.Fields);
        model.Entity<DocumentType>().Ignore(x => x.Keywords);

        model.Entity<GedDocument>().Property(x => x.ValuesJson).HasColumnType("jsonb");
        model.Entity<GedDocument>().Property(x => x.SuggestionsJson).HasColumnType("jsonb");
        model.Entity<DocumentType>().Property(x => x.FieldsJson).HasColumnType("jsonb");
        model.Entity<DocumentType>().Property(x => x.KeywordsJson).HasColumnType("jsonb");
        model.Entity<GedDocument>().HasIndex(x => new { x.TenantId, x.CreatedAt });
        model.Entity<GedDocument>().HasIndex(x => new { x.TenantId, x.DocumentTypeId });
        model.Entity<DocumentType>().HasIndex(x => new { x.TenantId, x.Name }).IsUnique();
        model.Entity<PhysicalBox>().HasIndex(x => new { x.TenantId, x.Code }).IsUnique();
        model.Entity<ProcessingBatch>().HasIndex(x => new { x.TenantId, x.Name });
        model.Entity<PhysicalLocation>().HasIndex(x => new { x.TenantId, x.ParentId });
    }
}
