using CaeManager.Domain.Centros;
using CaeManager.Domain.Documentos;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class AcreditacionDocumentoPlataformaConfiguration : IEntityTypeConfiguration<AcreditacionDocumentoPlataforma>
{
    public void Configure(EntityTypeBuilder<AcreditacionDocumentoPlataforma> builder)
    {
        builder.ToTable("AcreditacionesDocumentoPlataforma");
        builder.HasKey(a => a.Id);

        builder.HasIndex(a => new { a.TenantId, a.DocumentoId });
        builder.HasIndex(a => new { a.TenantId, a.CanalGestionDocumentalId });
        // Un documento no puede tener dos filas de acreditación para el mismo acceso.
        builder.HasIndex(a => new { a.TenantId, a.DocumentoId, a.CanalGestionDocumentalId }).IsUnique();

        builder.HasOne<Documento>().WithMany()
            .HasForeignKey(a => new { a.TenantId, a.DocumentoId })
            .HasPrincipalKey(d => new { d.TenantId, d.Id })
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<CanalGestionDocumental>().WithMany()
            .HasForeignKey(a => new { a.TenantId, a.CanalGestionDocumentalId })
            .HasPrincipalKey(c => new { c.TenantId, c.Id })
            .OnDelete(DeleteBehavior.Cascade);

        // HistorialRechazos es una colección de solo lectura respaldada por un
        // campo privado — mismo patrón que Conversacion.Mensajes.
        builder.HasMany(a => a.HistorialRechazos)
            .WithOne()
            .HasForeignKey(r => r.AcreditacionId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(a => a.HistorialRechazos).UsePropertyAccessMode(PropertyAccessMode.Field);

        // Vigencia en esta plataforma: dos columnas, porque son dos datos. La
        // propiedad Vigencia que las une es una vista de solo lectura que valida
        // la coherencia al leer, no una tercera columna.
        builder.Property(a => a.EstadoVigencia).HasConversion<int>();
        builder.Ignore(a => a.Vigencia);

        // Filtro global (soft delete + tenant) centralizado en CaeManagerDbContext.OnModelCreating.
    }
}
