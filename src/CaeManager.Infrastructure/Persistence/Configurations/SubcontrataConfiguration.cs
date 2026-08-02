using CaeManager.Domain.Subcontratas;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class SubcontrataConfiguration : IEntityTypeConfiguration<Subcontrata>
{
    public void Configure(EntityTypeBuilder<Subcontrata> builder)
    {
        builder.ToTable("Subcontratas");
        builder.HasKey(s => s.Id);

        builder.Property(s => s.RazonSocial)
            .IsRequired()
            .HasMaxLength(Subcontrata.LongitudMaximaRazonSocial);

        builder.HasIndex(s => new { s.TenantId, s.RazonSocial }).IsUnique();

        // Prerequisito de FKs compuestas — ver P0-1 de docs/business/MATURITY_REVIEW.md.
        builder.HasIndex(s => new { s.TenantId, s.Id }).IsUnique();

        // Filtro global (soft delete + tenant) centralizado en CaeManagerDbContext.OnModelCreating.
    }
}
