using CaeManager.Domain.Documentos;
using CaeManager.Domain.Plantillas;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class DocumentoGeneradoConfiguration : IEntityTypeConfiguration<DocumentoGenerado>
{
    public void Configure(EntityTypeBuilder<DocumentoGenerado> builder)
    {
        builder.ToTable("DocumentosGenerados");
        builder.HasKey(d => d.Id);

        builder.Property(d => d.DatosUtilizadosJson).IsRequired().HasColumnType("text");
        builder.Property(d => d.Estado).IsRequired().HasConversion<string>();
        builder.Property(d => d.CodigoSeguroVerificacion).HasMaxLength(100);
        builder.Property(d => d.HashSha256Impreso).HasMaxLength(64);

        builder.HasIndex(d => new { d.TenantId, d.PlantillaDocumentoVersionId });
        builder.HasIndex(d => new { d.TenantId, d.TrabajadorId });
        builder.HasIndex(d => new { d.TenantId, d.EmpresaId });

        // Restrict, mismo criterio que el resto de referencias de Plantillas:
        // borrar una versión con documentos ya generados debe fallar de forma
        // visible, no arrastrarlos en silencio.
        builder.HasOne<PlantillaDocumentoVersion>()
            .WithMany()
            .HasForeignKey(d => d.PlantillaDocumentoVersionId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Documento>()
            .WithMany()
            .HasForeignKey(d => d.DocumentoId)
            .OnDelete(DeleteBehavior.Restrict);

        // Filtro global (soft delete + tenant) centralizado en CaeManagerDbContext.OnModelCreating.
    }
}
