using CaeManager.Domain.Plataforma;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class ConcesionPrivilegioConfiguration : IEntityTypeConfiguration<ConcesionPrivilegio>
{
    public void Configure(EntityTypeBuilder<ConcesionPrivilegio> builder)
    {
        // El invariante de alcance global de ConcesionPrivilegio (ADR-011 § 8.8
        // y § 8.9), repetido en la base. No es redundancia: el WITH CHECK de RLS
        // admite cualquier fila que nombre a app.usuario_id como beneficiario,
        // así que sin esta restricción una escritura que no pase por el dominio
        // podría acuñar una concesión global de escritura. Y la llave universal
        // de lectura nunca es perpetua: caduca y se renueva.
        builder.ToTable("ConcesionesPrivilegio", t =>
        {
            t.HasCheckConstraint(
                "CK_ConcesionesPrivilegio_AlcanceGlobalSoloCapacidadesAdmitidas",
                "NOT \"EsAlcanceGlobal\" OR \"Capacidad\" IN ('AdminPlataforma', 'SoporteLectura')");
            t.HasCheckConstraint(
                "CK_ConcesionesPrivilegio_SoporteGlobalConVigenciaFinita",
                "NOT (\"EsAlcanceGlobal\" AND \"Capacidad\" = 'SoporteLectura') OR \"VigenciaHasta\" IS NOT NULL");
        });
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

        // Como texto y no como entero, igual que capacidad y estado: una fila de
        // privilegio se lee a menudo desde psql en una investigación, y "1" no
        // dice si es la fundacional.
        builder.Property(c => c.Origen).IsRequired().HasConversion<string>().HasMaxLength(25);

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
