using CaeManager.Domain.Comunicaciones;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class MensajeCorreoConfiguration : IEntityTypeConfiguration<MensajeCorreo>
{
    public void Configure(EntityTypeBuilder<MensajeCorreo> builder)
    {
        builder.ToTable("MensajesCorreo");
        builder.HasKey(m => m.Id);

        builder.Property(m => m.RemitenteEmail).IsRequired().HasMaxLength(320);
        builder.Property(m => m.CuerpoHtml).IsRequired();
        builder.Property(m => m.MensajeExternoId).HasMaxLength(MensajeCorreo.LongitudMaximaMensajeExternoId);

        builder.HasIndex(m => m.ConversacionCorreoId);
        builder.HasIndex(m => m.FechaUtc);
        // Único por tenant: idempotencia ante reintentos de notificación de webhook (P3-33).
        builder.HasIndex(m => new { m.TenantId, m.MensajeExternoId }).IsUnique();

        // Colección de solo lectura respaldada por campo privado — mismo
        // patrón que ConversacionCorreo.Mensajes/Participantes.
        builder.HasMany(m => m.Adjuntos)
            .WithOne()
            .HasForeignKey(a => a.MensajeCorreoId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(m => m.Adjuntos).UsePropertyAccessMode(PropertyAccessMode.Field);

        // Filtro global de tenant centralizado en CaeManagerDbContext.OnModelCreating.
    }
}
