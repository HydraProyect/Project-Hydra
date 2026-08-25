using CaeManager.Domain.Empresas;
using CaeManager.Domain.Subcontratas;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class SubcontrataEmpresaConfiguration : IEntityTypeConfiguration<SubcontrataEmpresa>
{
    public void Configure(EntityTypeBuilder<SubcontrataEmpresa> builder)
    {
        builder.ToTable("SubcontratasEmpresas");
        builder.HasKey(se => se.Id);

        builder.HasIndex(se => new { se.TenantId, se.SubcontrataId, se.EmpresaId }).IsUnique();
        builder.HasIndex(se => se.EmpresaId);

        // F3 (verificación del modelo real) — SubcontrataId y EmpresaId
        // apuntan ahora ambas a la Empresas unificada. FKs reales — ver
        // P0-1 de docs/business/MATURITY_REVIEW.md.
        builder.HasOne<Empresa>().WithMany()
            .HasForeignKey(se => new { se.TenantId, se.SubcontrataId })
            .HasPrincipalKey(e => new { e.TenantId, e.Id })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Empresa>().WithMany()
            .HasForeignKey(se => new { se.TenantId, se.EmpresaId })
            .HasPrincipalKey(e => new { e.TenantId, e.Id })
            .OnDelete(DeleteBehavior.Restrict);

        // Bloqueante de f3-revision-adversaria-2026-08-25.md punto 6 — ver
        // EmpresaClienteConfiguration para la justificación completa.
        builder.ToTable(t => t.HasCheckConstraint(
            "CK_SubcontratasEmpresas_NoAutorreferencia",
            @"""SubcontrataId"" <> ""EmpresaId"""));
    }
}
