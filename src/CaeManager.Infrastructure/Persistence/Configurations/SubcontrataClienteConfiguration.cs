using CaeManager.Domain.Clientes;
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
        builder.HasOne<Subcontrata>().WithMany()
            .HasForeignKey(sc => new { sc.TenantId, sc.SubcontrataId })
            .HasPrincipalKey(s => new { s.TenantId, s.Id })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Cliente>().WithMany()
            .HasForeignKey(sc => new { sc.TenantId, sc.ClienteId })
            .HasPrincipalKey(c => new { c.TenantId, c.Id })
            .OnDelete(DeleteBehavior.Restrict);
    }
}
