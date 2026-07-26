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
    }
}
