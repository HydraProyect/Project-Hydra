using CaeManager.Domain.Reclamaciones;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class ReclamacionDocumentalDocumentoConfiguration : IEntityTypeConfiguration<ReclamacionDocumentalDocumento>
{
    public void Configure(EntityTypeBuilder<ReclamacionDocumentalDocumento> builder)
    {
        builder.ToTable("ReclamacionesDocumentalesDocumentos");
        builder.HasKey(d => d.Id);

        builder.HasIndex(d => d.ReclamacionDocumentalId);
        builder.HasIndex(d => new { d.TenantId, d.DocumentoId });

        // Filtro global (soft delete + tenant) centralizado en CaeManagerDbContext.OnModelCreating.
    }
}
