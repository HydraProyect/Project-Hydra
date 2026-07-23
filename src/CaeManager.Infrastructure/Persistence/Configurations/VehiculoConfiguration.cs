using CaeManager.Domain.Vehiculos;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class VehiculoConfiguration : IEntityTypeConfiguration<Vehiculo>
{
    public void Configure(EntityTypeBuilder<Vehiculo> builder)
    {
        builder.ToTable("Vehiculos");
        builder.HasKey(v => v.Id);

        builder.Property(v => v.Nombre).IsRequired().HasMaxLength(Vehiculo.LongitudMaximaNombre);
        builder.Property(v => v.Modelo).IsRequired().HasMaxLength(Vehiculo.LongitudMaximaModelo);
        builder.Property(v => v.NumeroPlaca).IsRequired().HasMaxLength(Vehiculo.LongitudMaximaNumeroPlaca);

        builder.HasIndex(v => new { v.TenantId, v.NumeroPlaca }).IsUnique();
        builder.HasIndex(v => v.EmpresaId);
        builder.HasIndex(v => v.SubcontrataId);

        // Filtro global (soft delete + tenant) centralizado en CaeManagerDbContext.OnModelCreating.
    }
}
