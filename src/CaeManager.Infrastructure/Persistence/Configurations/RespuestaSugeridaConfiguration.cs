using CaeManager.Domain.Comunicaciones;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class RespuestaSugeridaConfiguration : IEntityTypeConfiguration<RespuestaSugerida>
{
    public void Configure(EntityTypeBuilder<RespuestaSugerida> builder)
    {
        builder.ToTable("RespuestasSugeridas");
        builder.HasKey(r => r.Id);

        builder.Property(r => r.Texto).IsRequired().HasMaxLength(RespuestaSugerida.LongitudMaximaTexto);

        // Una conversación tiene como mucho una sugerencia activa (Propuesta o SinRevisar).
        builder.HasIndex(r => new { r.TenantId, r.ConversacionId })
            .IsUnique()
            .HasFilter("\"Estado\" IN (0, 1)")
            .HasDatabaseName("IX_RespuestasSugeridas_Activa_Unica");

        builder.HasIndex(r => new { r.ConversacionId, r.TenantId })
            .HasDatabaseName("IX_RespuestasSugeridas_ConversacionId_TenantId");
        builder.HasIndex(r => new { r.MensajeOrigenId, r.TenantId })
            .HasDatabaseName("IX_RespuestasSugeridas_MensajeOrigenId_TenantId");

        // Sin relaciones en el modelo a propósito, como NotaInternaConversacion: las FK compuestas con el Tenant hacia
        // Conversaciones y Mensajes las crea en SQL la migración AnadeRespuestaSugerida.
        // Filtro global de tenant centralizado en CaeManagerDbContext.OnModelCreating.
    }
}
