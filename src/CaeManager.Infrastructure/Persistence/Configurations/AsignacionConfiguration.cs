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

        // Auditoría Módulo 5, hallazgo #5 / DEC-19: la invariante real —que
        // dos vigencias del mismo trío (tenant, trabajador, centro) nunca se
        // solapen, ni siquiera contra una fila ya cerrada— la impone ahora la
        // restricción EXCLUDE USING gist(daterange) de la migración
        // SolapeDeVigenciasEnAsignaciones (ver también
        // IAsignacionRepository.ExisteSolapeAsync y Asignacion.SeSolapaCon).
        // Antes de esa migración, este índice ÚNICO era toda la protección
        // que existía, y solo cubría DOS filas simultáneamente abiertas —
        // ver SolapamientoDeAsignacionesTests (IntegrationTests) para el
        // caso que se le escapaba. Sigue existiendo, pero SIN unicidad: la
        // invariante ya no depende de él, y se retira el HasFilter con
        // FechaAlta que en su día bloqueaba dar de baja y reasignar el mismo
        // trabajador al mismo centro el mismo día (bug real, reproducido con
        // datos: Shin Nohara, Terminal Ciudad Gotica 016 — 23505 de
        // Postgres); se conserva solo por rendimiento, porque
        // ExisteActivaAsync/ObtenerActivasPor* filtran exactamente por esta
        // combinación con FechaBaja IS NULL.
        builder.HasIndex(a => new { a.TenantId, a.TrabajadorId, a.CentroId })
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
