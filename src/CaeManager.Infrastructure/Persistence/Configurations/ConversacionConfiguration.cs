using CaeManager.Domain.Comunicaciones;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class ConversacionConfiguration : IEntityTypeConfiguration<Conversacion>
{
    public void Configure(EntityTypeBuilder<Conversacion> builder)
    {
        builder.ToTable("Conversaciones");
        builder.HasKey(c => c.Id);

        builder.Property(c => c.Asunto).IsRequired().HasMaxLength(Conversacion.LongitudMaximaAsunto);
        builder.Property(c => c.Etiquetas).HasMaxLength(Conversacion.LongitudMaximaEtiquetas);
        builder.Property(c => c.HiloExternoId).HasMaxLength(Conversacion.LongitudMaximaHiloExternoId);

        builder.Property(c => c.TelefonoContacto).HasMaxLength(Conversacion.LongitudMaximaTelefonoContacto);

        builder.HasIndex(c => new { c.TenantId, c.Estado });
        builder.HasIndex(c => new { c.TenantId, c.ClienteId });
        builder.HasIndex(c => c.FechaUltimoMensajeUtc);
        // Único por tenant: un HiloExternoId de Graph identifica un único hilo (P3-33).
        builder.HasIndex(c => new { c.TenantId, c.HiloExternoId }).IsUnique();
        // Canal WhatsApp: listado del Chat y lookup de hilo por teléfono en la ingesta.
        builder.HasIndex(c => new { c.TenantId, c.Canal, c.Estado });
        builder.HasIndex(c => new { c.TenantId, c.ConexionIntegracionId, c.TelefonoContacto });

        // Mensajes/Participantes son colecciones de solo lectura respaldadas
        // por un campo privado (ver Conversacion) — se le dice a EF Core
        // que lea/escriba directamente el campo, sin pasar por la propiedad
        // pública (que no expone Add). Mismo patrón estándar de EF Core para
        // agregados DDD con colecciones encapsuladas.
        builder.HasMany(c => c.Mensajes)
            .WithOne()
            .HasForeignKey(m => m.ConversacionId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(c => c.Mensajes).UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.HasMany(c => c.Participantes)
            .WithOne()
            .HasForeignKey(p => p.ConversacionId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(c => c.Participantes).UsePropertyAccessMode(PropertyAccessMode.Field);

        // Filtro global (soft delete + tenant) centralizado en CaeManagerDbContext.OnModelCreating.
    }
}
