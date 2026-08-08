using CaeManager.Domain.Cumplimiento;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class AceptacionTerminosConfiguration : IEntityTypeConfiguration<AceptacionTerminos>
{
    public void Configure(EntityTypeBuilder<AceptacionTerminos> builder)
    {
        builder.ToTable("AceptacionesTerminos");
        builder.HasKey(a => a.Id);

        builder.Property(a => a.VersionDocumento).IsRequired().HasMaxLength(AceptacionTerminos.LongitudMaximaVersionDocumento);

        builder.HasIndex(a => new { a.UsuarioId, a.VersionDocumento });

        // Sin HasQueryFilter: catálogo global de autorización, mismo
        // tratamiento que DelegacionTenant — no pertenece a un tenant.
    }
}
