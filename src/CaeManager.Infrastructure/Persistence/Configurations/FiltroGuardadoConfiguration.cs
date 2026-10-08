using CaeManager.Domain.Configuracion;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class FiltroGuardadoConfiguration : IEntityTypeConfiguration<FiltroGuardado>
{
    public void Configure(EntityTypeBuilder<FiltroGuardado> builder)
    {
        builder.ToTable("FiltrosGuardados");
        builder.HasKey(f => f.Id);

        builder.Property(f => f.Pantalla).HasMaxLength(50).IsRequired();
        builder.Property(f => f.Nombre).HasMaxLength(100).IsRequired();
        builder.Property(f => f.ValoresJson).IsRequired();

        // Un nombre por Tenant, usuario y pantalla. El Tenant va primero: es la
        // coordenada por la que aíslan el filtro global de EF y la política RLS
        // (aislamiento_tenant), como en el resto de tablas por Tenant.
        builder.HasIndex(f => new { f.TenantId, f.UsuarioId, f.Pantalla, f.Nombre }).IsUnique();
    }
}
