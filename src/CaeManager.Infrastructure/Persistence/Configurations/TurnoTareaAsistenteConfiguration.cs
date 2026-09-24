using CaeManager.Domain.AsistenteIa;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class TurnoTareaAsistenteConfiguration : IEntityTypeConfiguration<TurnoTareaAsistente>
{
    public void Configure(EntityTypeBuilder<TurnoTareaAsistente> builder)
    {
        builder.ToTable("TurnosTareaAsistente");
        builder.HasKey(t => t.Id);

        builder.Property(t => t.Autor).HasConversion<string>().HasMaxLength(32);
        builder.Property(t => t.TextoOriginal).HasMaxLength(TurnoTareaAsistente.LongitudMaximaTexto).IsRequired();
        builder.Property(t => t.TextoEnmascarado).HasMaxLength(TurnoTareaAsistente.LongitudMaximaTexto);

        builder.HasIndex(t => new { t.TareaAsistenteId, t.Numero }).IsUnique();
    }
}
