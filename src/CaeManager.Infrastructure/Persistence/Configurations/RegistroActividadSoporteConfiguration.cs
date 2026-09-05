using CaeManager.Domain.Soporte;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class RegistroActividadSoporteConfiguration : IEntityTypeConfiguration<RegistroActividadSoporte>
{
    public void Configure(EntityTypeBuilder<RegistroActividadSoporte> builder)
    {
        // REC-208: DelegacionTenantId y SesionPrivilegiadaId son anulables
        // desde que el agregado admite los dos agrupadores posibles, y esta
        // constraint es la que hace la invariante "exactamente uno" visible
        // también para lo que no pasa por el dominio (siembras, SQL directo,
        // migraciones futuras) — mismo patrón que
        // CK_Documentos_PropietarioXor (DocumentoConfiguration, REC-101), que
        // es el precedente que este incremento sigue.
        builder.ToTable("RegistrosActividadSoporte", t => t.HasCheckConstraint(
            "CK_RegistrosActividadSoporte_UnSoloAgrupador",
            "(\"DelegacionTenantId\" IS NULL) <> (\"SesionPrivilegiadaId\" IS NULL)"));
        builder.HasKey(r => r.Id);

        builder.Property(r => r.UsuarioSoporteId).IsRequired();
        builder.Property(r => r.OcurridaEnUtc).IsRequired();

        // Nombre y no número, mismo criterio que PropositoDelegacion.
        builder.Property(r => r.Tipo).IsRequired().HasConversion<string>().HasMaxLength(30);
        builder.Property(r => r.Detalle).HasMaxLength(RegistroActividadSoporte.LongitudMaximaDetalle);

        // La consulta que importa es "enséñame todo lo que hizo soporte en
        // esta visita, en orden" — para responder a un cliente que pregunta.
        // Se conserva el índice por delegación aunque la columna pase a
        // anulable: el histórico anterior a REC-208 se sigue consultando por
        // él, y retirarlo lo dejaría sin índice justo cuando más filas tiene.
        // La vía de sesión privilegiada estrena el suyo.
        builder.HasIndex(r => new { r.DelegacionTenantId, r.OcurridaEnUtc });
        builder.HasIndex(r => new { r.SesionPrivilegiadaId, r.OcurridaEnUtc });

        // El filtro global de tenant lo aplica CaeManagerDbContext como con
        // todas las entidades con TenantId, sin excepción.
    }
}
