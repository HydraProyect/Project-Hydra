using CaeManager.Domain.Centros;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class CentroConfiguration : IEntityTypeConfiguration<Centro>
{
    public void Configure(EntityTypeBuilder<Centro> builder)
    {
        builder.ToTable("Centros");
        builder.HasKey(c => c.Id);

        builder.Property(c => c.Nombre)
            .IsRequired()
            .HasMaxLength(Centro.LongitudMaximaNombre);

        builder.Property(c => c.CodigoCentro).HasMaxLength(Centro.LongitudMaximaCodigo);
        builder.Property(c => c.Direccion).HasMaxLength(Centro.LongitudMaximaDireccion);
        builder.Property(c => c.Contacto).HasMaxLength(Centro.LongitudMaximaContacto);

        // Sin navigation property hacia Cliente/Empresa a propósito: cada agregado
        // se consulta por su propio repositorio/query (ver ARCHITECTURE.md).
        builder.HasIndex(c => c.ClienteId);
        builder.HasIndex(c => c.EmpresaId);

        builder.HasQueryFilter(c => !c.EstaEliminado);
    }
}
