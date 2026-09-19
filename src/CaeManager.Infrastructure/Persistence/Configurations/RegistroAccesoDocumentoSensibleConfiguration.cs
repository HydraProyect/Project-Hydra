using CaeManager.Domain.Auditoria;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class RegistroAccesoDocumentoSensibleConfiguration : IEntityTypeConfiguration<RegistroAccesoDocumentoSensible>
{
    public void Configure(EntityTypeBuilder<RegistroAccesoDocumentoSensible> builder)
    {
        builder.ToTable("RegistrosAccesoDocumentoSensible");
        builder.HasKey(r => r.Id);

        builder.Property(r => r.DocumentoId).IsRequired();
        builder.Property(r => r.Sensibilidad).IsRequired().HasConversion<string>().HasMaxLength(30);
        builder.Property(r => r.TipoAcceso).IsRequired().HasConversion<string>().HasMaxLength(20);
        builder.Property(r => r.ViaAcceso).IsRequired().HasConversion<string>().HasMaxLength(30);

        // Nombre y no número, mismo criterio que las cuatro de arriba: un valor
        // intercalado en el futuro no debe reinterpretar filas existentes.
        // `HasDefaultValue` deja el defecto también en la BD, no solo en el
        // modelo: un INSERT que no nombre la columna —una siembra por SQL, un
        // arreglo manual— cae en Desconocido en vez de fallar o en vez de
        // heredar el primer valor del enum por casualidad.
        builder.Property(r => r.TipoActor).IsRequired().HasConversion<string>().HasMaxLength(30)
            .HasDefaultValue(TipoActorAuditoria.Desconocido);

        builder.Property(r => r.OcurridoEnUtc).IsRequired();

        // La consulta que importa: "enséñame los accesos a este Documento" y
        // "enséñame los últimos accesos del tenant" — mismo criterio de
        // índice compuesto que RegistroActividadSoporteConfiguration.
        builder.HasIndex(r => new { r.TenantId, r.OcurridoEnUtc });
        builder.HasIndex(r => new { r.DocumentoId, r.OcurridoEnUtc });

        // Sin FK hacia Documento (DocumentoId es un Guid suelto, ver el
        // comentario de la entidad): el rastro debe sobrevivir a la baja del
        // Documento que describe, igual que RegistroAuditoria.EntidadId.

        // El filtro global de tenant lo aplica CaeManagerDbContext como con
        // todas las entidades con TenantId, sin excepción. La segunda línea
        // de defensa (RLS) va en su propia migración — ver
        // HabilitarRlsRegistrosAccesoDocumentoSensible.
    }
}
