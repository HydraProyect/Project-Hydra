using CaeManager.Domain.Configuracion;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class OrdenCajasFichaConfiguration : IEntityTypeConfiguration<OrdenCajasFicha>
{
    public void Configure(EntityTypeBuilder<OrdenCajasFicha> builder)
    {
        builder.ToTable("OrdenesCajasFicha");
        builder.HasKey(o => o.Id);

        builder.Property(o => o.TipoFicha).HasMaxLength(OrdenCajasFicha.LongitudMaximaTipoFicha).IsRequired();
        builder.Property(o => o.Claves).HasColumnType("text[]").IsRequired();

        // Una sola fila por Tenant, usuario y tipo de ficha. El Tenant va primero:
        // es la coordenada por la que aíslan el filtro global de EF y la política
        // RLS (aislamiento_tenant), como en el resto de tablas por Tenant.
        builder.HasIndex(o => new { o.TenantId, o.UsuarioId, o.TipoFicha }).IsUnique();
    }
}
