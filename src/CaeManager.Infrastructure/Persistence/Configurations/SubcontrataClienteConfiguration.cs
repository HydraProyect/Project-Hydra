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

        // FKs reales — ver P0-1 de docs/business/MATURITY_REVIEW.md.
        // F3b-Subcontrata — SubcontrataId repunta contra Empresas, mismo
        // patrón que ClienteId de abajo (ambas F3b, ver
        // f3b-decision-d2-transicion-acotada-2026-08-25.md §2).
        builder.HasOne<Empresa>().WithMany()
            .HasForeignKey(sc => new { sc.TenantId, sc.SubcontrataId })
            .HasPrincipalKey(s => new { s.TenantId, s.Id })
            .OnDelete(DeleteBehavior.Restrict);

        // F3b — ClienteId repunta contra Empresas (ver CentroConfiguration).
        builder.HasOne<Empresa>().WithMany()
            .HasForeignKey(sc => new { sc.TenantId, sc.ClienteId })
            .HasPrincipalKey(c => new { c.TenantId, c.Id })
            .OnDelete(DeleteBehavior.Restrict);
    }
}
