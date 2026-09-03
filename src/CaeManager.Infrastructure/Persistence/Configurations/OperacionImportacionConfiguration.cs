using CaeManager.Domain.Importacion;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class OperacionImportacionConfiguration : IEntityTypeConfiguration<OperacionImportacion>
{
    public void Configure(EntityTypeBuilder<OperacionImportacion> builder)
    {
        builder.ToTable("OperacionesImportacion");
        builder.HasKey(o => o.Id);

        // La unicidad que hace idempotente confirmar la misma operación dos veces,
        // incluso bajo confirmaciones concurrentes (REC-108, DEC-20) — ver el
        // comentario de OperacionImportacion.cs. Nombre explícito porque
        // OperacionImportacionRepository lo compara literalmente para distinguir
        // ESTA violación de unicidad de cualquier otro 23505 — dejarlo a la
        // convención por defecto de EF lo volvería frágil ante un futuro rename.
        builder.HasIndex(o => new { o.TenantId, o.OperacionId })
            .IsUnique()
            .HasDatabaseName("IX_OperacionesImportacion_TenantId_OperacionId");
    }
}
