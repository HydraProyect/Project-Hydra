using CaeManager.Domain.Proyectos;
using CaeManager.Domain.Trabajadores;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class ProyectoTecnicoConfiguration : IEntityTypeConfiguration<ProyectoTecnico>
{
    public void Configure(EntityTypeBuilder<ProyectoTecnico> builder)
    {
        builder.ToTable("ProyectosTecnicos");
        builder.HasKey(pt => pt.Id);

        // Único por (tenant, proyecto, trabajador) ENTRE LOS ACTIVOS, no por
        // fecha de alta — auditoría Módulo 5, hallazgo crítico 11/9. Con
        // FechaAlta en la clave, la comprobación ExisteActivoAsync (SELECT)
        // seguida del INSERT es una carrera: dos peticiones concurrentes con
        // fechas de alta distintas para el mismo proyecto-trabajador pasan
        // las dos, dejando dos filas activas. Mismo defecto y misma
        // corrección que ya tiene AsignacionConfiguration.
        builder.HasIndex(pt => new { pt.TenantId, pt.ProyectoId, pt.TrabajadorId })
               .IsUnique()
               .HasFilter($"\"{nameof(ProyectoTecnico.FechaBaja)}\" IS NULL")
               .HasDatabaseName("IX_ProyectosTecnicos_TenantId_ProyectoId_TrabajadorId_Activo");
        builder.HasIndex(pt => pt.TrabajadorId);

        // FKs reales — ver P0-1 de docs/business/MATURITY_REVIEW.md.
        builder.HasOne<Proyecto>().WithMany()
            .HasForeignKey(pt => new { pt.TenantId, pt.ProyectoId })
            .HasPrincipalKey(p => new { p.TenantId, p.Id })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Trabajador>().WithMany()
            .HasForeignKey(pt => new { pt.TenantId, pt.TrabajadorId })
            .HasPrincipalKey(t => new { t.TenantId, t.Id })
            .OnDelete(DeleteBehavior.Restrict);
    }
}
