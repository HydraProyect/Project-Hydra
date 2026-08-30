using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Trabajadores;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class AsignacionConfiguration : IEntityTypeConfiguration<Asignacion>
{
    public void Configure(EntityTypeBuilder<Asignacion> builder)
    {
        builder.ToTable("Asignaciones");
        builder.HasKey(a => a.Id);

        // Único por (tenant, trabajador, centro) ENTRE LAS ACTIVAS — la invariante real
        // (ExisteActivaAsync ya la comprueba a nivel de aplicación: FechaBaja == null) es
        // "a lo sumo una asignación activa", no "a lo sumo una por fecha de alta". Incluir
        // FechaAlta en la clave bloqueaba dar de baja y reasignar el mismo trabajador al
        // mismo centro el mismo día: la fila inactiva con esa fecha seguía colisionando
        // contra el índice aunque ExisteActivaAsync ya la descartara (bug real, reproducido
        // con datos: Shin Nohara, Terminal Ciudad Gotica 016 — 23505 de Postgres).
        // Este índice impide DOS filas simultáneamente abiertas del mismo trío,
        // no que sus RANGOS de fecha (FechaAlta..FechaBaja) se solapen contra
        // una fila ya cerrada del mismo trío — ver
        // SolapamientoDeAsignacionesTests (IntegrationTests) para el caso
        // reproducido. Auditoría Módulo 5, hallazgo #5: no se cierra con
        // EXCLUDE USING gist(daterange) porque bloquear el solape es una
        // regla de negocio pendiente de decisión (¿el mismo Trabajador puede
        // tener presencia legítima y solapada en el mismo Centro por turnos o
        // proyectos distintos, o es siempre un error de datos?), no algo que
        // se pueda inventar en modo autónomo.
        builder.HasIndex(a => new { a.TenantId, a.TrabajadorId, a.CentroId })
               .IsUnique()
               .HasFilter($"\"{nameof(Asignacion.FechaBaja)}\" IS NULL")
               .HasDatabaseName("IX_Asignaciones_TenantId_TrabajadorId_CentroId_Activa");
        builder.HasIndex(a => a.CentroId);

        // FKs reales — ver P0-1 de docs/business/MATURITY_REVIEW.md.
        builder.HasOne<Trabajador>().WithMany()
            .HasForeignKey(a => new { a.TenantId, a.TrabajadorId })
            .HasPrincipalKey(t => new { t.TenantId, t.Id })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Centro>().WithMany()
            .HasForeignKey(a => new { a.TenantId, a.CentroId })
            .HasPrincipalKey(c => new { c.TenantId, c.Id })
            .OnDelete(DeleteBehavior.Restrict);
    }
}
