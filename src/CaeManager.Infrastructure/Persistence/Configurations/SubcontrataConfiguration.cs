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

        builder.HasIndex(s => s.RazonSocial).IsUnique();

        builder.HasQueryFilter(s => !s.EstaEliminado);
    }
}
