using CaeManager.Domain.Comunicaciones;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class ConversacionCorreoConfiguration : IEntityTypeConfiguration<ConversacionCorreo>
{
    public void Configure(EntityTypeBuilder<ConversacionCorreo> builder)
    {
        builder.ToTable("ConversacionesCorreo");
        builder.HasKey(c => c.Id);

        builder.Property(c => c.Asunto).IsRequired().HasMaxLength(ConversacionCorreo.LongitudMaximaAsunto);
        builder.Property(c => c.Etiquetas).HasMaxLength(ConversacionCorreo.LongitudMaximaEtiquetas);
        builder.Property(c => c.HiloExternoId).HasMaxLength(ConversacionCorreo.LongitudMaximaHiloExternoId);

        builder.Property(c => c.TelefonoContacto).HasMaxLength(ConversacionCorreo.LongitudMaximaTelefonoContacto);

        builder.HasIndex(c => new { c.TenantId, c.Estado });
        builder.HasIndex(c => new { c.TenantId, c.ClienteId });
        builder.HasIndex(c => c.FechaUltimoMensajeUtc);
        // Único por tenant: un HiloExternoId de Graph identifica un único hilo (P3-33).
        builder.HasIndex(c => new { c.TenantId, c.HiloExternoId }).IsUnique();
        // Canal WhatsApp: listado del Chat y lookup de hilo por teléfono en la ingesta.
        builder.HasIndex(c => new { c.TenantId, c.Canal, c.Estado });
        builder.HasIndex(c => new { c.TenantId, c.ConexionIntegracionId, c.TelefonoContacto });

        // Mensajes/Participantes son colecciones de solo lectura respaldadas
        // por un campo privado (ver ConversacionCorreo) — se le dice a EF Core
        // que lea/escriba directamente el campo, sin pasar por la propiedad
        // pública (que no expone Add). Mismo patrón estándar de EF Core para
        // agregados DDD con colecciones encapsuladas.
        builder.HasMany(c => c.Mensajes)
            .WithOne()
            .HasForeignKey(m => m.ConversacionCorreoId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(c => c.Mensajes).UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.HasMany(c => c.Participantes)
            .WithOne()
            .HasForeignKey(p => p.ConversacionCorreoId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(c => c.Participantes).UsePropertyAccessMode(PropertyAccessMode.Field);

        // Filtro global (soft delete + tenant) centralizado en CaeManagerDbContext.OnModelCreating.
    }
}
