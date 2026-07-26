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

        builder.Property(e => e.Cif)
            .HasMaxLength(Empresa.LongitudCif);

        builder.HasIndex(e => new { e.TenantId, e.RazonSocial }).IsUnique();
        builder.HasIndex(e => new { e.TenantId, e.Cif }).IsUnique();

        // Filtro global (soft delete + tenant) centralizado en CaeManagerDbContext.OnModelCreating.
    }
}
