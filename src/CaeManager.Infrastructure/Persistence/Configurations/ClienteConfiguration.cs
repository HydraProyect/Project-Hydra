using CaeManager.Domain.Clientes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class ClienteConfiguration : IEntityTypeConfiguration<Cliente>
{
    public void Configure(EntityTypeBuilder<Cliente> builder)
    {
        builder.ToTable("Clientes");
        builder.HasKey(c => c.Id);

        builder.Property(c => c.RazonSocial)
            .IsRequired()
            .HasMaxLength(Cliente.LongitudMaximaRazonSocial);

        builder.Property(c => c.Cif)
            .IsRequired()
            .HasMaxLength(Cliente.LongitudCif);

        builder.Property(c => c.Notas)
            .HasMaxLength(Cliente.LongitudMaximaNotas);

        builder.HasIndex(c => c.RazonSocial);
        builder.HasIndex(c => c.Cif).IsUnique();

        builder.HasQueryFilter(c => !c.EstaEliminado);
    }
}
