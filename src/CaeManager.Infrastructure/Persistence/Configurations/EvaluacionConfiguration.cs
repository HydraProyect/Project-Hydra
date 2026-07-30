using CaeManager.Domain.Evaluaciones;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class EvaluacionConfiguration : IEntityTypeConfiguration<Evaluacion>
{
    public void Configure(EntityTypeBuilder<Evaluacion> builder)
    {
        builder.ToTable("Evaluaciones");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.Observaciones).HasMaxLength(Evaluacion.LongitudMaximaObservaciones);

        builder.HasIndex(e => e.CentroId);
        builder.HasIndex(e => e.TrabajadorId);
    }
}
