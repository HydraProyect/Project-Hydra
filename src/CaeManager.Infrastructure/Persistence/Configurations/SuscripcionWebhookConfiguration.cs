using CaeManager.Domain.Integraciones;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class SuscripcionWebhookConfiguration : IEntityTypeConfiguration<SuscripcionWebhook>
{
    public void Configure(EntityTypeBuilder<SuscripcionWebhook> builder)
    {
        builder.ToTable("SuscripcionesWebhook");
        builder.HasKey(s => s.Id);

        builder.Property(s => s.GraphSubscriptionId).IsRequired().HasMaxLength(SuscripcionWebhook.LongitudMaximaGraphSubscriptionId);

        // El cifrado de ClientState se configura en CaeManagerDbContext.OnModelCreating
        // — sin HasMaxLength aquí a propósito: el texto cifrado es más largo que el plano.

        builder.HasIndex(s => new { s.TenantId, s.ConexionIntegracionId }).IsUnique();
        builder.HasIndex(s => s.FechaExpiracionUtc);
    }
}
