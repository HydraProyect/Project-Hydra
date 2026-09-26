using CaeManager.Domain.AsistenteIa;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class PasoTareaAsistenteConfiguration : IEntityTypeConfiguration<PasoTareaAsistente>
{
    public void Configure(EntityTypeBuilder<PasoTareaAsistente> builder)
    {
        // Un paso confirmado, ejecutado o fallido viene siempre de un plan
        // confirmado (ConfirmadoEnUtc), y uno ejecutado lleva cuándo se ejecutó.
        builder.ToTable("PasosTareaAsistente", t =>
        {
            t.HasCheckConstraint(
                "CK_PasosTareaAsistente_EjecucionTrasConfirmacion",
                "\"Estado\" NOT IN ('Confirmado', 'Ejecutado', 'Fallido') OR \"ConfirmadoEnUtc\" IS NOT NULL");
            t.HasCheckConstraint(
                "CK_PasosTareaAsistente_EjecutadoConFecha",
                "\"Estado\" <> 'Ejecutado' OR \"EjecutadoEnUtc\" IS NOT NULL");
        });
        builder.HasKey(p => p.Id);

        builder.Property(p => p.Estado).HasConversion<string>().HasMaxLength(32);
        builder.Property(p => p.OrdenAsistenteId).HasMaxLength(PasoTareaAsistente.LongitudMaximaOrdenAsistenteId).IsRequired();
        builder.Property(p => p.DatosJson).HasMaxLength(PasoTareaAsistente.LongitudMaximaDatosJson).IsRequired();
        builder.Property(p => p.Resumen).HasMaxLength(PasoTareaAsistente.LongitudMaximaResumen);
        builder.Property(p => p.CamposPendientesJson).IsRequired();
        builder.Property(p => p.AvisosJson).IsRequired();
        builder.Property(p => p.MotivoFallo).HasMaxLength(PasoTareaAsistente.LongitudMaximaMotivoFallo);

        builder.Ignore(p => p.CamposPendientes);
        builder.Ignore(p => p.Avisos);
        builder.Ignore(p => p.EstaVivo);

        builder.HasIndex(p => new { p.TareaAsistenteId, p.Posicion }).IsUnique();
    }
}
