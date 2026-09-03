using CaeManager.Domain.Documentos;
using CaeManager.Domain.DocumentosIa;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class ExtraccionIaCacheDocumentoConfiguration : IEntityTypeConfiguration<ExtraccionIaCacheDocumento>
{
    public void Configure(EntityTypeBuilder<ExtraccionIaCacheDocumento> builder)
    {
        builder.ToTable("ExtraccionesIaCacheDocumentos");
        builder.HasKey(v => v.Id);

        // Sin navegación de colección a propósito (HasOne<T>().WithMany() sin
        // .HasMany en el lado padre): con navegación real, EF fija el
        // TenantId del vínculo por fixup del grafo del padre antes de que
        // TenantSelladoInterceptor lo selle, y el sellado posterior revienta
        // porque TenantId es parte de la clave. Mismo patrón que
        // DocumentoConfiguration/FirmaEnCampoDocumentoConfiguration.
        builder.HasOne<ExtraccionIaCache>().WithMany()
            .HasForeignKey(v => new { v.TenantId, v.ExtraccionIaCacheId })
            .HasPrincipalKey(c => new { c.TenantId, c.Id })
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<Documento>().WithMany()
            .HasForeignKey(v => new { v.TenantId, v.DocumentoId })
            .HasPrincipalKey(d => new { d.TenantId, d.Id })
            .OnDelete(DeleteBehavior.Cascade);

        // Único: evita duplicar el mismo vínculo (VincularDocumentoAsync ya
        // comprueba antes de insertar, esto es el backstop de base de datos
        // ante la misma carrera que ExtraccionIaCacheConfiguration documenta
        // para su propio índice único).
        builder.HasIndex(v => new { v.TenantId, v.ExtraccionIaCacheId, v.DocumentoId }).IsUnique();

        // "Los vínculos de este Documento" — el acceso de PurgarVinculadosADocumentosAsync.
        builder.HasIndex(v => new { v.TenantId, v.DocumentoId });
    }
}
