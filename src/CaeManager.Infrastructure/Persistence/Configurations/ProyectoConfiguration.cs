using CaeManager.Domain.Centros;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Proyectos;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class ProyectoConfiguration : IEntityTypeConfiguration<Proyecto>
{
    public void Configure(EntityTypeBuilder<Proyecto> builder)
    {
        builder.ToTable("Proyectos");
        builder.HasKey(p => p.Id);

        builder.Property(p => p.Nombre).HasMaxLength(Proyecto.LongitudMaximaNombre).IsRequired();
        builder.Property(p => p.Notas).HasMaxLength(Proyecto.LongitudMaximaNotas);

        // Nombre único por (tenant, cliente) — no se puede repetir aunque el
        // proyecto esté en otro centro del mismo cliente, ni tras cerrarse
        // (solo se libera si se elimina, ver DATABASE.md/ROADMAP.md).
        builder.HasIndex(p => new { p.TenantId, p.ClienteId, p.Nombre })
               .IsUnique()
               .HasFilter($"NOT \"{nameof(Proyecto.EstaEliminado)}\"");

        builder.HasIndex(p => p.CentroId);

        // FKs reales — ver P0-1 de docs/business/MATURITY_REVIEW.md.
        // F3b — ClienteId repunta contra Empresas (ver CentroConfiguration).
        builder.HasOne<Empresa>().WithMany()
            .HasForeignKey(p => new { p.TenantId, p.ClienteId })
            .HasPrincipalKey(c => new { c.TenantId, c.Id })
            .OnDelete(DeleteBehavior.Restrict);

        // Compuesta con ClienteId, no solo con CentroId (auditoría Módulo 5,
        // hueco arquitectónico): contra la clave alternativa de Centro
        // (TenantId, Id, ClienteId), un Proyecto cuyo ClienteId no case con
        // el del Centro es irrepresentable en la base — antes solo lo
        // comprobaba CrearProyectoCommand.
        builder.HasOne<Centro>().WithMany()
            .HasForeignKey(p => new { p.TenantId, p.CentroId, p.ClienteId })
            .HasPrincipalKey(c => new { c.TenantId, c.Id, c.ClienteId })
            .OnDelete(DeleteBehavior.Restrict);

        // Prerequisito de la FK que ProyectoTecnico declara hacia Proyecto.
        builder.HasIndex(p => new { p.TenantId, p.Id }).IsUnique();

        // Filtro global (soft delete + tenant) centralizado en CaeManagerDbContext.OnModelCreating.
    }
}
