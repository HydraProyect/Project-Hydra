using CaeManager.Domain.Documentos;
using CaeManager.Infrastructure.Persistence.Seed;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class TipoDocumentoConfiguration : IEntityTypeConfiguration<TipoDocumento>
{
    public void Configure(EntityTypeBuilder<TipoDocumento> builder)
    {
        builder.ToTable("TiposDocumento");
        builder.HasKey(t => t.Id);

        builder.Property(t => t.Nombre).IsRequired().HasMaxLength(TipoDocumento.LongitudMaximaNombre);
        builder.Property(t => t.Notas).HasMaxLength(TipoDocumento.LongitudMaximaNotas);
        builder.Property(t => t.Descripcion).HasMaxLength(TipoDocumento.LongitudMaximaDescripcion);
        builder.Property(t => t.CriteriosValidacion).HasMaxLength(TipoDocumento.LongitudMaximaCriteriosValidacion);
        builder.Property(t => t.SeSolicitaA).HasMaxLength(TipoDocumento.LongitudMaximaSeSolicitaA);
        builder.Property(t => t.Observaciones).HasMaxLength(TipoDocumento.LongitudMaximaObservaciones);

        builder.Property(t => t.AmbitoAplicacion).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(t => t.PerfilDocumentoOficial).HasConversion<string>().HasMaxLength(20).IsRequired();

        // Los dos ejes que sustituyen a EsObligatorio. Como cadena, igual que
        // el resto de enums persistidos de la casa: el nombre es legible en la
        // base y renombrar un valor se ve, en vez de reinterpretar filas en
        // silencio. "ObligacionCondicionada" es el valor más largo (22).
        builder.Property(t => t.Requerido).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(t => t.Naturaleza).HasConversion<string>().HasMaxLength(30).IsRequired();

        builder.HasIndex(t => new { t.TenantId, t.Nombre }).IsUnique();

        // Prerequisito de las FKs que Documento/TipoDocumentoCentro declaran
        // hacia TipoDocumento — ver P0-1 de docs/business/MATURITY_REVIEW.md.
        builder.HasIndex(t => new { t.TenantId, t.Id }).IsUnique();

        builder.HasData(TipoDocumentoSeedData.ComoFilasParaMigracion());
    }
}
