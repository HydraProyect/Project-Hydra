using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class SelloEmpresaConfiguration : IEntityTypeConfiguration<SelloEmpresa>
{
    public void Configure(EntityTypeBuilder<SelloEmpresa> builder)
    {
        builder.ToTable("SellosEmpresa");
        builder.HasKey(s => s.Id);

        builder.Property(s => s.ImagenUrl).IsRequired().HasMaxLength(SelloEmpresa.LongitudMaximaImagenUrl);

        builder.HasOne<Empresa>().WithMany().HasForeignKey(s => s.EmpresaId).OnDelete(DeleteBehavior.Cascade);

        // Una empresa, un sello guardado — se reemplaza al re-guardar.
        builder.HasIndex(s => new { s.TenantId, s.EmpresaId }).IsUnique();
    }
}
