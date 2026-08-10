using CaeManager.Domain.Centros;
using CaeManager.Domain.Visitas;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class VisitaConfiguration : IEntityTypeConfiguration<Visita>
{
    public void Configure(EntityTypeBuilder<Visita> builder)
    {
        builder.ToTable("Visitas");
        builder.HasKey(v => v.Id);

        builder.Property(v => v.Notas).HasMaxLength(Visita.LongitudMaximaNotas);

        // Sin navigation property hacia Centro a propósito: cada agregado se
        // consulta por su propio repositorio/query (ver ARCHITECTURE.md). La
        // FK sí se declara — ver P0-1 de docs/business/MATURITY_REVIEW.md.
        builder.HasIndex(v => v.CentroId);
        builder.HasIndex(v => v.FechaFin);

        // Horas de antelación: 2 decimales bastan para tramos y recargos, y fijar la
        // precisión evita que Postgres elija numeric sin escala.
        builder.Property(v => v.AntelacionNominalHoras).HasPrecision(10, 2);
        builder.Property(v => v.AntelacionEfectivaHoras).HasPrecision(10, 2);

        // Índice del barrido del evaluador: "visitas con solicitud, sin sellar y
        // todavía vigentes" es exactamente lo que filtra EvaluarPorDocumentoAsync.
        builder.HasIndex(v => new { v.TenantId, v.FechaFin })
            .HasFilter("\"FechaHoraSolicitudUtc\" IS NOT NULL AND \"FechaHoraExpedienteCompletoUtc\" IS NULL AND NOT \"EstaEliminado\"")
            .HasDatabaseName("IX_Visitas_ExpedientePendiente");

        // Sin FK hacia Conversacion a propósito: ConversacionOrigenId es una referencia
        // débil a otro agregado (mismo criterio que EventoConversacion.ReferenciaId), y
        // el módulo de Comunicaciones puede estar apagado por configuración.
        builder.HasIndex(v => new { v.TenantId, v.ConversacionOrigenId });

        builder.HasOne<Centro>().WithMany()
            .HasForeignKey(v => new { v.TenantId, v.CentroId })
            .HasPrincipalKey(c => new { c.TenantId, c.Id })
            .OnDelete(DeleteBehavior.Restrict);

        // Prerequisito de la FK que VisitaTrabajador declara hacia Visita.
        builder.HasIndex(v => new { v.TenantId, v.Id }).IsUnique();

        // Filtro global (soft delete + tenant) centralizado en CaeManagerDbContext.OnModelCreating.
    }
}
