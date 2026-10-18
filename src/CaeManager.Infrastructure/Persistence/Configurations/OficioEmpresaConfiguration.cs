using CaeManager.Domain.Empresas;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class OficioEmpresaConfiguration : IEntityTypeConfiguration<OficioEmpresa>
{
    public void Configure(EntityTypeBuilder<OficioEmpresa> builder)
    {
        builder.ToTable("OficiosEmpresa");
        builder.HasKey(o => o.Id);

        builder.Property(o => o.Nombre).HasMaxLength(OficioEmpresa.LongitudMaximaNombre).IsRequired();

        // FK compuesta con TenantId: un Oficio no puede colgar de una Empresa de otro Tenant.
        builder.HasOne<Empresa>().WithMany()
            .HasForeignKey(o => new { o.TenantId, o.EmpresaId })
            .HasPrincipalKey(e => new { e.TenantId, e.Id })
            .OnDelete(DeleteBehavior.Restrict);

        // Clave alterna que usa la FK de Trabajador (TenantId, EmpresaId, OficioEmpresaId): el Oficio de un
        // Trabajador tiene que ser de su misma Empresa, y lo garantiza la base de datos.
        builder.HasAlternateKey(o => new { o.TenantId, o.EmpresaId, o.Id });
        builder.HasIndex(o => new { o.TenantId, o.EmpresaId });
    }
}
