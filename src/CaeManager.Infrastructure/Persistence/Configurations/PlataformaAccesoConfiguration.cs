using CaeManager.Domain.Centros;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class PlataformaAccesoConfiguration : IEntityTypeConfiguration<PlataformaAcceso>
{
    public void Configure(EntityTypeBuilder<PlataformaAcceso> builder)
    {
        builder.ToTable("PlataformasAcceso");
        builder.HasKey(p => p.Id);

        builder.Property(p => p.NombrePlataforma)
            .IsRequired()
            .HasMaxLength(PlataformaAcceso.LongitudMaximaNombrePlataforma);

        // El cifrado de Usuario/Contrasena se configura en CaeManagerDbContext.OnModelCreating,
        // porque necesita el IDataProtector inyectado en el propio DbContext.

        builder.HasIndex(p => p.CentroId).IsUnique();
    }
}
