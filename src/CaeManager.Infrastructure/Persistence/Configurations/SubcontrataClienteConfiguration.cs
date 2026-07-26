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
    }
}
