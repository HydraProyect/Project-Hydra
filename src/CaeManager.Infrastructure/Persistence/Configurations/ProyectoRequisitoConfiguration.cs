using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Proyectos;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class ProyectoRequisitoConfiguration : IEntityTypeConfiguration<ProyectoRequisito>
{
    public void Configure(EntityTypeBuilder<ProyectoRequisito> builder)
    {
        builder.ToTable("ProyectosRequisitos");
        builder.HasKey(r => r.Id);

        // Un Tipo de Documento figura una sola vez en los requisitos de un Proyecto.
        builder.HasIndex(r => new { r.TenantId, r.ProyectoId, r.TipoDocumentoId }).IsUnique();

        // FKs compuestas con TenantId, como ProyectoTecnico: un requisito no puede apuntar
        // a un Proyecto ni a un Tipo de Documento de otro Tenant propietario.
        builder.HasOne<Proyecto>().WithMany()
            .HasForeignKey(r => new { r.TenantId, r.ProyectoId })
            .HasPrincipalKey(p => new { p.TenantId, p.Id })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<TipoDocumento>().WithMany()
            .HasForeignKey(r => new { r.TenantId, r.TipoDocumentoId })
            .HasPrincipalKey(t => new { t.TenantId, t.Id })
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public class PlantillaRequisitosEventoConfiguration : IEntityTypeConfiguration<PlantillaRequisitosEvento>
{
    public void Configure(EntityTypeBuilder<PlantillaRequisitosEvento> builder)
    {
        builder.ToTable("PlantillasRequisitosEvento");
        builder.HasKey(p => p.Id);

        builder.Property(p => p.Nombre).HasMaxLength(PlantillaRequisitosEvento.LongitudMaximaNombre).IsRequired();
        builder.HasIndex(p => new { p.TenantId, p.ClienteId, p.Nombre }).IsUnique();

        builder.HasOne<Empresa>().WithMany()
            .HasForeignKey(p => new { p.TenantId, p.ClienteId })
            .HasPrincipalKey(c => new { c.TenantId, c.Id })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(p => p.Tipos).WithOne()
            .HasForeignKey(t => new { t.TenantId, t.PlantillaId })
            .HasPrincipalKey(p => new { p.TenantId, p.Id })
            .OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(p => p.Tipos).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

public class PlantillaRequisitosEventoTipoConfiguration : IEntityTypeConfiguration<PlantillaRequisitosEventoTipo>
{
    public void Configure(EntityTypeBuilder<PlantillaRequisitosEventoTipo> builder)
    {
        builder.ToTable("PlantillasRequisitosEventoTipos");
        builder.HasKey(t => t.Id);

        builder.HasIndex(t => new { t.TenantId, t.PlantillaId, t.TipoDocumentoId }).IsUnique();

        builder.HasOne<TipoDocumento>().WithMany()
            .HasForeignKey(t => new { t.TenantId, t.TipoDocumentoId })
            .HasPrincipalKey(d => new { d.TenantId, d.Id })
            .OnDelete(DeleteBehavior.Restrict);
    }
}
