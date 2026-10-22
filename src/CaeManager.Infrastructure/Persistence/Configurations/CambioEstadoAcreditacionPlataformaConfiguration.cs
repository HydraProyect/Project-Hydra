using CaeManager.Domain.Documentos;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class CambioEstadoAcreditacionPlataformaConfiguration : IEntityTypeConfiguration<CambioEstadoAcreditacionPlataforma>
{
    public void Configure(EntityTypeBuilder<CambioEstadoAcreditacionPlataforma> builder)
    {
        builder.ToTable("CambiosEstadoAcreditacionPlataforma");
        builder.HasKey(c => c.Id);

        builder.Property(c => c.Motivo).HasMaxLength(RechazoAcreditacionDocumentoPlataforma.LongitudMaximaMotivoLiteral);
        builder.Property(c => c.Nota).HasMaxLength(CambioEstadoAcreditacionPlataforma.LongitudMaximaNota);
        builder.Property(c => c.EstadoVigenciaAnterior).HasConversion<int>();
        builder.Ignore(c => c.EstaDeshecho);

        builder.HasIndex(c => new { c.TenantId, c.AcreditacionId, c.MomentoUtc });

        // Filtro global de tenant centralizado en CaeManagerDbContext.OnModelCreating.
    }
}
