using CaeManager.Domain.Documentos;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class RechazoAcreditacionDocumentoPlataformaConfiguration : IEntityTypeConfiguration<RechazoAcreditacionDocumentoPlataforma>
{
    public void Configure(EntityTypeBuilder<RechazoAcreditacionDocumentoPlataforma> builder)
    {
        builder.ToTable("RechazosAcreditacionDocumentoPlataforma");
        builder.HasKey(r => r.Id);

        builder.Property(r => r.MotivoLiteral).IsRequired().HasMaxLength(RechazoAcreditacionDocumentoPlataforma.LongitudMaximaMotivoLiteral);

        builder.HasIndex(r => new { r.TenantId, r.AcreditacionId });

        // Filtro global de tenant centralizado en CaeManagerDbContext.OnModelCreating.
    }
}
