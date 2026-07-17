using CaeManager.Domain.Empresas;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class EmpresaConfiguration : IEntityTypeConfiguration<Empresa>
{
    public void Configure(EntityTypeBuilder<Empresa> builder)
    {
        builder.ToTable("Empresas");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.RazonSocial)
            .IsRequired()
            .HasMaxLength(Empresa.LongitudMaximaRazonSocial);

        builder.HasIndex(e => e.RazonSocial).IsUnique();

        builder.HasQueryFilter(e => !e.EstaEliminado);
    }
}
