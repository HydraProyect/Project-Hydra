using CaeManager.Domain.Reclamaciones;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class ReclamacionDocumentalDocumentoConfiguration : IEntityTypeConfiguration<ReclamacionDocumentalDocumento>
{
    public void Configure(EntityTypeBuilder<ReclamacionDocumentalDocumento> builder)
    {
        // Una línea es un Documento que existe (DocumentoId, sin Tipo ni Trabajador) o un documento que falta (sin
        // DocumentoId, con su Tipo y, si es de Trabajador, a quién le falta). Impuesto en la base y no solo en el
        // constructor, por la misma razón que el titular único de la reclamación: tiene que sobrevivir a una siembra
        // por SQL o a una migración de datos. Una línea sin ninguna de las dos formas no diría qué se reclamó.
        builder.ToTable("ReclamacionesDocumentalesDocumentos", t => t.HasCheckConstraint(
            "CK_ReclamacionesDocumentalesDocumentos_Forma",
            "(\"DocumentoId\" IS NOT NULL AND \"TipoDocumentoId\" IS NULL AND \"TrabajadorId\" IS NULL)" +
            " OR (\"DocumentoId\" IS NULL AND \"TipoDocumentoId\" IS NOT NULL)"));
        builder.HasKey(d => d.Id);

        builder.HasIndex(d => d.ReclamacionDocumentalId);
        builder.HasIndex(d => new { d.TenantId, d.DocumentoId });

        // Filtro global (soft delete + tenant) centralizado en CaeManagerDbContext.OnModelCreating.
    }
}
