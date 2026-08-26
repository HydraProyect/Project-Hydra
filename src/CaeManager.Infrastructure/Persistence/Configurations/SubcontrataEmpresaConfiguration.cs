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

        // FKs reales — ver P0-1 de docs/business/MATURITY_REVIEW.md.
        // F3b-Subcontrata — SubcontrataId repunta contra Empresas.
        builder.HasOne<Empresa>().WithMany()
            .HasForeignKey(se => new { se.TenantId, se.SubcontrataId })
            .HasPrincipalKey(s => new { s.TenantId, s.Id })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Empresa>().WithMany()
            .HasForeignKey(se => new { se.TenantId, se.EmpresaId })
            .HasPrincipalKey(e => new { e.TenantId, e.Id })
            .OnDelete(DeleteBehavior.Restrict);
    }
}
