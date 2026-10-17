using CaeManager.Domain.Dashboard;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class CierreMensualTenantBeneficiarioConfiguration : IEntityTypeConfiguration<CierreMensualTenantBeneficiario>
{
    public void Configure(EntityTypeBuilder<CierreMensualTenantBeneficiario> builder)
    {
        builder.ToTable("CierresMensualesTenantBeneficiario");
        builder.HasKey(c => c.Id);
        builder.HasIndex(c => new { c.TenantId, c.Anyo, c.Mes }).IsUnique();
        builder.Property(c => c.FotoJson).IsRequired().HasColumnType("jsonb");
        builder.Property(c => c.MotivoReapertura).HasMaxLength(500);
    }
}
