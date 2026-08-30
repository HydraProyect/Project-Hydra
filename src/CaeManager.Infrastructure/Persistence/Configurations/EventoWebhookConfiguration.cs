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
        builder.Property(e => e.PayloadRedactado).IsRequired().HasDefaultValue(false);

        // Parcial sobre los NO redactados: como PoliticasRlsCubrenModeloTests
        // recuerda para el resto de índices de esta tabla, "sin redactar" es
        // transitorio (todo evento se acaba redactando pasada la retención),
        // así que un índice sobre el subconjunto vivo no crece con el
        // histórico completo — auditoría módulo 6, retención de PayloadCrudo.
        builder.HasIndex(e => new { e.Estado, e.FechaRecepcionUtc })
            .HasFilter($"\"{nameof(EventoWebhook.PayloadRedactado)}\" = false")
            .HasDatabaseName("IX_EventosWebhook_Estado_FechaRecepcionUtc_SinRedactar");

        // Parcial y con FechaRecepcionUtc: el consumidor filtra por pendiente
        // Y ordena por fecha de recepción (ver IngestaWebhookService). Un
        // índice (TenantId, Procesado) sin la fecha obliga a Postgres a
        // ordenar en memoria los pendientes de cada tenant; y como
        // "pendiente" es transitorio, un índice parcial sobre los pendientes
        // se queda pequeño en vez de crecer con el histórico completo
        // (auditoría Módulo 8). Filtro reescrito sobre Estado (auditoría de
        // colas, 2026-08-30): reemplaza al bool Procesado que existía cuando
        // se escribió este índice — ver EventoWebhook.Estado.
        builder.HasIndex(e => new { e.TenantId, e.FechaRecepcionUtc })
            .HasFilter($"\"{nameof(EventoWebhook.Estado)}\" = 'Pendiente'")
            .HasDatabaseName("IX_EventosWebhook_TenantId_FechaRecepcionUtc_Pendientes");

        builder.HasIndex(e => e.ConexionIntegracionId);
    }
}
