using CaeManager.Domain.RequisitosDocumentales;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class RequisitoDocumentalConfiguration : IEntityTypeConfiguration<RequisitoDocumental>
{
    public void Configure(EntityTypeBuilder<RequisitoDocumental> builder)
    {
        builder.ToTable("RequisitosDocumentales");
        builder.HasKey(r => r.Id);

        builder.Property(r => r.Descripcion).IsRequired().HasMaxLength(RequisitoDocumental.LongitudMaximaDescripcion);
        builder.Property(r => r.PeriodicidadEspecial).HasMaxLength(RequisitoDocumental.LongitudMaximaPeriodicidad);
        builder.Property(r => r.Notas).HasMaxLength(RequisitoDocumental.LongitudMaximaNotas);

        builder.HasIndex(r => r.CentroId);

        builder.HasQueryFilter(r => !r.EstaEliminado);
    }
}
