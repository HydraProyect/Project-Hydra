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
        builder.Property(e => e.ErrorProcesado).HasMaxLength(EventoWebhook.LongitudMaximaError);

        // Parcial y con FechaRecepcionUtc: el consumidor filtra por pendiente
        // Y ordena por fecha de recepción (ver IngestaWebhookService). Un
        // índice (TenantId, Procesado) sin la fecha obliga a Postgres a
        // ordenar en memoria los pendientes de cada tenant; y como
        // "procesado" es transitorio (false -> true), un índice parcial
        // sobre los pendientes se queda pequeño en vez de crecer con el
        // histórico completo (auditoría Módulo 8).
        builder.HasIndex(e => new { e.TenantId, e.FechaRecepcionUtc })
            .HasFilter($"NOT \"{nameof(EventoWebhook.Procesado)}\"")
            .HasDatabaseName("IX_EventosWebhook_TenantId_FechaRecepcionUtc_Pendientes");
        builder.HasIndex(e => e.ConexionIntegracionId);
    }
}
