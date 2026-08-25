using CaeManager.Domain.Empresas;
using CaeManager.Domain.Subcontratas;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class SubcontrataClienteConfiguration : IEntityTypeConfiguration<SubcontrataCliente>
{
    public void Configure(EntityTypeBuilder<SubcontrataCliente> builder)
    {
        builder.ToTable("SubcontratasClientes");
        builder.HasKey(sc => sc.Id);

        builder.HasIndex(sc => new { sc.TenantId, sc.SubcontrataId, sc.ClienteId }).IsUnique();
        builder.HasIndex(sc => sc.ClienteId);

        // F3 (verificación del modelo real) — SubcontrataId y ClienteId
        // apuntan ahora ambas a la Empresas unificada. FKs reales — ver
        // P0-1 de docs/business/MATURITY_REVIEW.md.
        builder.HasOne<Empresa>().WithMany()
            .HasForeignKey(sc => new { sc.TenantId, sc.SubcontrataId })
            .HasPrincipalKey(e => new { e.TenantId, e.Id })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Empresa>().WithMany()
            .HasForeignKey(sc => new { sc.TenantId, sc.ClienteId })
            .HasPrincipalKey(e => new { e.TenantId, e.Id })
            .OnDelete(DeleteBehavior.Restrict);

        // Bloqueante de f3-revision-adversaria-2026-08-25.md punto 6 — ver
        // EmpresaClienteConfiguration para la justificación completa.
        builder.ToTable(t => t.HasCheckConstraint(
            "CK_SubcontratasClientes_NoAutorreferencia",
            @"""SubcontrataId"" <> ""ClienteId"""));
    }
}
