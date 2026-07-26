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
    }
}
