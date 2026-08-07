using CaeManager.Domain.Integraciones;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class LineaWhatsAppConfiguration : IEntityTypeConfiguration<LineaWhatsApp>
{
    public void Configure(EntityTypeBuilder<LineaWhatsApp> builder)
    {
        builder.ToTable("LineasWhatsApp");
        builder.HasKey(l => l.Id);

        builder.Property(l => l.PhoneNumberId).IsRequired().HasMaxLength(LineaWhatsApp.LongitudMaximaPhoneNumberId);
        builder.Property(l => l.WabaId).IsRequired().HasMaxLength(LineaWhatsApp.LongitudMaximaWabaId);
        builder.Property(l => l.NumeroTelefono).IsRequired().HasMaxLength(LineaWhatsApp.LongitudMaximaNumeroTelefono);
        builder.Property(l => l.MensajeAutoTriage).HasMaxLength(LineaWhatsApp.LongitudMaximaMensajeAutoTriage);
        // TokenAcceso sin HasMaxLength a propósito: se cifra con ValueConverter
        // en CaeManagerDbContext y el texto cifrado es más largo que el plano
        // (mismo criterio que CredencialIntegracion.RefreshToken).
        builder.Property(l => l.TokenAcceso).IsRequired();

        // Índice único GLOBAL, sin TenantId, a propósito: el webhook de Meta
        // (una URL de callback única para todas las líneas) identifica la
        // línea receptora por phone_number_id y con ella se RESUELVE el
        // tenant — mismo criterio que el índice global de ClaveApi.HashClave.
        builder.HasIndex(l => l.PhoneNumberId).IsUnique();
        builder.HasIndex(l => l.ConexionIntegracionId).IsUnique();

        builder.HasOne<ConexionIntegracion>()
            .WithOne()
            .HasForeignKey<LineaWhatsApp>(l => l.ConexionIntegracionId)
            .OnDelete(DeleteBehavior.Cascade);

        // Colección de solo lectura respaldada por campo privado — mismo
        // patrón que Conversacion.Mensajes.
        builder.HasMany(l => l.MiembrosPool)
            .WithOne()
            .HasForeignKey(m => m.LineaWhatsAppId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(l => l.MiembrosPool).UsePropertyAccessMode(PropertyAccessMode.Field);

        // Filtro global de tenant centralizado en CaeManagerDbContext.OnModelCreating.
    }
}
