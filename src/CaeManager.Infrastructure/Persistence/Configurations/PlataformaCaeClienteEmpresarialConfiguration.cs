using CaeManager.Domain.Empresas;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class PlataformaCaeClienteEmpresarialConfiguration : IEntityTypeConfiguration<PlataformaCaeClienteEmpresarial>
{
    public void Configure(EntityTypeBuilder<PlataformaCaeClienteEmpresarial> builder)
    {
        builder.ToTable("PlataformasCaeClienteEmpresarial");
        builder.HasKey(p => p.Id);

        builder.Property(p => p.Nombre).HasMaxLength(PlataformaCaeClienteEmpresarial.LongitudMaximaNombre).IsRequired();
        builder.Property(p => p.UrlPortal).HasMaxLength(PlataformaCaeClienteEmpresarial.LongitudMaximaUrlPortal);
        builder.Property(p => p.CodigoCliente).HasMaxLength(PlataformaCaeClienteEmpresarial.LongitudMaximaCodigoCliente);

        builder.HasIndex(p => new { p.TenantId, p.ClienteEmpresarialId }).IsUnique();

        // FK compuesta con el Tenant (mismo patrón que RelacionEmpresarial): la fila no puede
        // apuntar a una Empresa de otro Tenant aunque alguien conozca su Id.
        builder.HasOne<Empresa>().WithMany()
            .HasForeignKey(p => new { p.TenantId, p.ClienteEmpresarialId })
            .HasPrincipalKey(e => new { e.TenantId, e.Id })
            .OnDelete(DeleteBehavior.Restrict);
    }
}
