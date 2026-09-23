using CaeManager.Domain.Comunicaciones;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class NotaInternaConversacionConfiguration : IEntityTypeConfiguration<NotaInternaConversacion>
{
    public void Configure(EntityTypeBuilder<NotaInternaConversacion> builder)
    {
        builder.ToTable("NotasInternasConversacion");
        builder.HasKey(n => n.Id);

        builder.Property(n => n.Texto).HasMaxLength(NotaInternaConversacion.LongitudMaximaTexto).IsRequired();

        builder.HasIndex(n => n.ConversacionId);

        // Sin relación en el modelo a propósito: la FK compuesta
        // (ConversacionId, TenantId) → Conversaciones (Id, TenantId) la crea
        // en SQL la migración AgregarNotasInternasConversacion, contra la
        // clave alternativa AK_Conversaciones_Id_TenantId que ya existe para
        // Mensajes y ParticipantesConversacion (ver ConversacionConfiguration).
        // Filtro global de tenant centralizado en CaeManagerDbContext.OnModelCreating.
    }
}
