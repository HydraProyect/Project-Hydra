using CaeManager.Domain.Comunicaciones;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class MensajeConfiguration : IEntityTypeConfiguration<Mensaje>
{
    public void Configure(EntityTypeBuilder<Mensaje> builder)
    {
        builder.ToTable("Mensajes");
        builder.HasKey(m => m.Id);

        builder.Property(m => m.Remitente).IsRequired().HasMaxLength(320);
        builder.Property(m => m.CuerpoHtml).IsRequired();
        builder.Property(m => m.MensajeExternoId).HasMaxLength(Mensaje.LongitudMaximaMensajeExternoId);
        builder.Property(m => m.ErrorEntrega).HasMaxLength(Mensaje.LongitudMaximaErrorEntrega);

        builder.HasIndex(m => m.ConversacionId);
        builder.HasIndex(m => m.FechaUtc);
        // Único por tenant: idempotencia ante reintentos de notificación de webhook (P3-33).
        builder.HasIndex(m => new { m.TenantId, m.MensajeExternoId }).IsUnique();

        // Colección de solo lectura respaldada por campo privado — mismo
        // patrón que Conversacion.Mensajes/Participantes. FK de una sola
        // columna, no compuesta con TenantId — ver el comentario de
        // ConversacionConfiguration sobre por qué (navegación de colección
        // real + TenantSelladoInterceptor, auditoría Módulo 8).
        builder.HasMany(m => m.Adjuntos)
            .WithOne()
            .HasForeignKey(a => a.MensajeId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(m => m.Adjuntos).UsePropertyAccessMode(PropertyAccessMode.Field);

        // Filtro global de tenant centralizado en CaeManagerDbContext.OnModelCreating.
    }
}
