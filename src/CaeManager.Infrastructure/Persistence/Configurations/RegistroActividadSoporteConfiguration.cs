using CaeManager.Domain.Soporte;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class RegistroActividadSoporteConfiguration : IEntityTypeConfiguration<RegistroActividadSoporte>
{
    public void Configure(EntityTypeBuilder<RegistroActividadSoporte> builder)
    {
        builder.ToTable("RegistrosActividadSoporte");
        builder.HasKey(r => r.Id);

        builder.Property(r => r.UsuarioSoporteId).IsRequired();
        builder.Property(r => r.DelegacionTenantId).IsRequired();
        builder.Property(r => r.OcurridaEnUtc).IsRequired();

        // Nombre y no número, mismo criterio que PropositoDelegacion.
        builder.Property(r => r.Tipo).IsRequired().HasConversion<string>().HasMaxLength(30);
        builder.Property(r => r.Detalle).HasMaxLength(RegistroActividadSoporte.LongitudMaximaDetalle);

        // La consulta que importa es "enséñame todo lo que hizo soporte en
        // esta visita, en orden" — para responder a un cliente que pregunta.
        builder.HasIndex(r => new { r.DelegacionTenantId, r.OcurridaEnUtc });

        // El filtro global de tenant lo aplica CaeManagerDbContext como con
        // todas las entidades con TenantId, sin excepción.
    }
}
