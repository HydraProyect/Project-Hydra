using CaeManager.Domain.Empresas;
using CaeManager.Domain.RelacionesEmpresariales;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class RelacionEmpresarialConfiguration : IEntityTypeConfiguration<RelacionEmpresarial>
{
    public void Configure(EntityTypeBuilder<RelacionEmpresarial> builder)
    {
        builder.ToTable("RelacionesEmpresariales", t =>
        {
            t.HasCheckConstraint("CK_RelacionesEmpresariales_NoAutorreferencia", "\"ProveedoraId\" <> \"ClienteId\"");
            t.HasCheckConstraint("CK_RelacionesEmpresariales_NoEnmarcadaEnSiMisma", "\"EnmarcadaEnId\" IS DISTINCT FROM \"Id\"");
            t.HasCheckConstraint("CK_RelacionesEmpresariales_VigenciaOrdenada", "\"VigenciaHasta\" IS NULL OR \"VigenciaHasta\" >= \"VigenciaDesde\"");
        });
        builder.HasKey(r => r.Id);

        builder.Property(r => r.OrigenVigencia)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(30);

        builder.Property(r => r.VigenciaDesde).IsRequired();
        builder.Property(r => r.CreadoEnUtc).IsRequired();

        // Prerequisito de la FK compuesta de EnmarcadaEnId — mismo patrón que
        // Empresas (P0-1 de docs/business/MATURITY_REVIEW.md).
        builder.HasIndex(r => new { r.TenantId, r.Id }).IsUnique();

        builder.HasIndex(r => new { r.TenantId, r.ClienteId }).IncludeProperties(r => r.ProveedoraId);
        builder.HasIndex(r => new { r.TenantId, r.ProveedoraId }).IncludeProperties(r => r.ClienteId);

        // Como mucho una relación VIGENTE por par proveedora×cliente — parcial,
        // no total: no impide reabrir un par tras cerrarlo (ver diseño físico
        // § 3). ProyectoId queda fuera del esquema de F4 a propósito (mismo
        // documento, § 2) — no complica esta unicidad sin evidencia de que
        // haga falta.
        builder.HasIndex(r => new { r.TenantId, r.ProveedoraId, r.ClienteId })
            .IsUnique()
            .HasFilter("\"VigenciaHasta\" IS NULL")
            .HasDatabaseName("IX_RelacionesEmpresariales_ParActivo");

        builder.HasOne<Empresa>().WithMany()
            .HasForeignKey(r => new { r.TenantId, r.ProveedoraId })
            .HasPrincipalKey(e => new { e.TenantId, e.Id })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Empresa>().WithMany()
            .HasForeignKey(r => new { r.TenantId, r.ClienteId })
            .HasPrincipalKey(e => new { e.TenantId, e.Id })
            .OnDelete(DeleteBehavior.Restrict);

        // Autorreferencia — la aciclicidad NO está garantizada aquí (ver
        // comentario de clase en RelacionEmpresarial.cs); esto solo impide
        // apuntar a una relación de otro tenant o inexistente.
        builder.HasOne<RelacionEmpresarial>().WithMany()
            .HasForeignKey(r => new { r.TenantId, r.EnmarcadaEnId })
            .HasPrincipalKey(r => new { r.TenantId, r.Id })
            .OnDelete(DeleteBehavior.Restrict);
    }
}
