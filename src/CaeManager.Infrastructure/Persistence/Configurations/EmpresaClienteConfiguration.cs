using CaeManager.Domain.Clientes;
using CaeManager.Domain.Empresas;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class EmpresaClienteConfiguration : IEntityTypeConfiguration<EmpresaCliente>
{
    public void Configure(EntityTypeBuilder<EmpresaCliente> builder)
    {
        builder.ToTable("EmpresasClientes");
        builder.HasKey(ec => ec.Id);

        builder.HasIndex(ec => new { ec.TenantId, ec.EmpresaId, ec.ClienteId }).IsUnique();
        builder.HasIndex(ec => ec.ClienteId);

        // FKs reales — ver P0-1 de docs/business/MATURITY_REVIEW.md.
        builder.HasOne<Empresa>().WithMany()
            .HasForeignKey(ec => new { ec.TenantId, ec.EmpresaId })
            .HasPrincipalKey(e => new { e.TenantId, e.Id })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Cliente>().WithMany()
            .HasForeignKey(ec => new { ec.TenantId, ec.ClienteId })
            .HasPrincipalKey(c => new { c.TenantId, c.Id })
            .OnDelete(DeleteBehavior.Restrict);
    }
}
