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
        // Sin HasMaxLength a propósito, a diferencia de los demás campos
        // acotados de esta entidad: forzar aquí un varchar(N) generaría una
        // migración que Postgres podría rechazar si ya existe en producción
        // alguna fila más larga que el nuevo tope (que este cambio introduce
        // solo hacia delante — ver Mensaje.LongitudMaximaCuerpoHtml, aplicado
        // en el constructor). La columna sigue siendo "text"; el límite real
        // lo impone el dominio, no el esquema.
        builder.Property(m => m.CuerpoHtml).IsRequired();
        builder.Property(m => m.MensajeExternoId).HasMaxLength(Mensaje.LongitudMaximaMensajeExternoId);
        builder.Property(m => m.ErrorEntrega).HasMaxLength(Mensaje.LongitudMaximaErrorEntrega);

        builder.HasIndex(m => m.ConversacionId);
        builder.HasIndex(m => m.FechaUtc);
        // Único por tenant: idempotencia ante reintentos de notificación de webhook (P3-33).
        builder.HasIndex(m => new { m.TenantId, m.MensajeExternoId }).IsUnique();

        // Colección de solo lectura respaldada por campo privado — mismo
        // patrón que Conversacion.Mensajes/Participantes.
        builder.HasMany(m => m.Adjuntos)
            .WithOne()
            .HasForeignKey(a => a.MensajeId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(m => m.Adjuntos).UsePropertyAccessMode(PropertyAccessMode.Field);

        // Filtro global de tenant centralizado en CaeManagerDbContext.OnModelCreating.
    }
}
