using CaeManager.Domain.Comunicaciones;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class PropuestaRecordatorioConversacionConfiguration : IEntityTypeConfiguration<PropuestaRecordatorioConversacion>
{
    public void Configure(EntityTypeBuilder<PropuestaRecordatorioConversacion> builder)
    {
        builder.ToTable("PropuestasRecordatorioConversacion");
        builder.HasKey(p => p.Id);

        // Una espera (la conversación y el instante en que entró en ella) recibe como mucho una propuesta, se apruebe o se
        // descarte: el proceso de fondo no la repite.
        builder.HasIndex(p => new { p.TenantId, p.ConversacionId, p.EsperaDesdeUtc })
            .IsUnique()
            .HasDatabaseName("IX_PropuestasRecordatorioConversacion_Espera_Unica");

        // Y una conversación tiene como mucho una propuesta Pendiente a la vez.
        builder.HasIndex(p => new { p.TenantId, p.ConversacionId })
            .IsUnique()
            .HasFilter("\"Estado\" = 0")
            .HasDatabaseName("IX_PropuestasRecordatorioConversacion_Pendiente_Unica");

        // Sin relación en el modelo a propósito, como NotaInternaConversacion: la FK compuesta
        // (ConversacionId, TenantId) → Conversaciones (Id, TenantId) la crea en SQL la migración
        // AnadePropuestasRecordatorioConversacion, contra la clave alternativa AK_Conversaciones_Id_TenantId.
        // Filtro global de tenant centralizado en CaeManagerDbContext.OnModelCreating.
    }
}
