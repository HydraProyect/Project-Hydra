using CaeManager.Domain.Plataforma;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class ConcesionPrivilegioConfiguration : IEntityTypeConfiguration<ConcesionPrivilegio>
{
    public void Configure(EntityTypeBuilder<ConcesionPrivilegio> builder)
    {
        builder.ToTable("ConcesionesPrivilegio");
        builder.HasKey(c => c.Id);

        builder.Property(c => c.UsuarioPlataformaId).IsRequired();
        builder.Property(c => c.EsAlcanceGlobal).IsRequired();
        builder.Property(c => c.VigenciaDesde).IsRequired();
        builder.Property(c => c.MotivoConcesion).HasMaxLength(ConcesionPrivilegio.LongitudMaximaMotivo);

        // Nombre del enum, no su número: un valor intercalado en el futuro no
        // debe reinterpretar filas existentes — y aquí eso convertiría una
        // concesión de solo lectura en una de escritura excepcional.
        builder.Property(c => c.Capacidad).IsRequired().HasConversion<string>().HasMaxLength(30);
        builder.Property(c => c.Estado).IsRequired().HasConversion<string>().HasMaxLength(20);

        // "¿Qué puede hacer hoy este usuario de plataforma?" — la consulta que
        // precede a cada apertura de sesión.
        builder.HasIndex(c => new { c.UsuarioPlataformaId, c.Estado });

        builder.Metadata
            .FindNavigation(nameof(ConcesionPrivilegio.TenantsAlcanzados))!
            .SetPropertyAccessMode(PropertyAccessMode.Field);

        // Sin HasQueryFilter: catálogo global de autorización de plataforma,
        // mismo tratamiento que Tenant y las asignaciones operativas. Estar
        // fuera del filtro NO la hace legible sin restricción — la política de
        // lectura vive en Application y la vigila un test de arquitectura.
    }
}
