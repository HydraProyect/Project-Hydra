using CaeManager.Domain.Integraciones;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class EventoWebhookConfiguration : IEntityTypeConfiguration<EventoWebhook>
{
    public void Configure(EntityTypeBuilder<EventoWebhook> builder)
    {
        builder.ToTable("EventosWebhook");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.PayloadCrudo).IsRequired();
        builder.Property(e => e.Estado).IsRequired().HasConversion<string>();
        builder.Property(e => e.ErrorProcesado).HasMaxLength(EventoWebhook.LongitudMaximaError);

        builder.HasIndex(e => new { e.TenantId, e.Estado });
        builder.HasIndex(e => e.ConexionIntegracionId);
    }
}
