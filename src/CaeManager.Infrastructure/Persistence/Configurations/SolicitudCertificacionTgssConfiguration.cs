using CaeManager.Domain.Blindaje42;
using CaeManager.Domain.Empresas;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class SolicitudCertificacionTgssConfiguration : IEntityTypeConfiguration<SolicitudCertificacionTgss>
{
    public void Configure(EntityTypeBuilder<SolicitudCertificacionTgss> builder)
    {
        builder.ToTable("SolicitudesCertificacionTgss");
        builder.HasKey(s => s.Id);

        builder.Property(s => s.Observaciones)
            .HasMaxLength(SolicitudCertificacionTgss.LongitudMaximaObservaciones);

        builder.Property(s => s.EvidenciaNombreArchivo)
            .HasMaxLength(SolicitudCertificacionTgss.LongitudMaximaNombreArchivo);

        // La pestaña "Blindaje 42.1" pide "todas las solicitudes de (empresa, cliente)" y "la última por cliente".
        builder.HasIndex(s => new { s.TenantId, s.EmpresaId, s.ClienteId, s.FechaSolicitud });

        // FKs compuestas con TenantId — ver P0-1 de docs/business/MATURITY_REVIEW.md:
        // un Id ajeno de otro tenant no puede referenciarse ni por accidente.
        //
        // ClienteId apunta a Empresa, no a Cliente — igual que RelacionEmpresarial
        // (ADR-011, F4 cerrado): Cliente está congelada desde F3b (#279) y el
        // modelo aprobado la retira; que hoy el Id de un Cliente coincida con
        // el de su Empresa espejo es un accidente de la migración F3, no un
        // contrato en el que apoyarse.
        // Restrict en ambas, no Cascade — mismo criterio que RelacionEmpresarialConfiguration:
        // dos FKs distintas a Empresa desde la misma fila.
        builder.HasOne<Empresa>().WithMany()
            .HasForeignKey(s => new { s.TenantId, s.EmpresaId })
            .HasPrincipalKey(e => new { e.TenantId, e.Id })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Empresa>().WithMany()
            .HasForeignKey(s => new { s.TenantId, s.ClienteId })
            .HasPrincipalKey(e => new { e.TenantId, e.Id })
            .OnDelete(DeleteBehavior.Restrict);

        // Filtro global (soft delete + tenant) centralizado en CaeManagerDbContext.OnModelCreating.
    }
}
