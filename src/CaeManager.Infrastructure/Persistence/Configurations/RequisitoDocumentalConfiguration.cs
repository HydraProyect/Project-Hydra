using CaeManager.Domain.Centros;
using CaeManager.Domain.RequisitosDocumentales;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class RequisitoDocumentalConfiguration : IEntityTypeConfiguration<RequisitoDocumental>
{
    public void Configure(EntityTypeBuilder<RequisitoDocumental> builder)
    {
        builder.ToTable("RequisitosDocumentales");
        builder.HasKey(r => r.Id);

        builder.Property(r => r.Descripcion).IsRequired().HasMaxLength(RequisitoDocumental.LongitudMaximaDescripcion);
        builder.Property(r => r.PeriodicidadEspecial).HasMaxLength(RequisitoDocumental.LongitudMaximaPeriodicidad);
        builder.Property(r => r.Notas).HasMaxLength(RequisitoDocumental.LongitudMaximaNotas);
        builder.Property(r => r.ArchivoUrl).HasMaxLength(RequisitoDocumental.LongitudMaximaArchivoUrl);
        builder.Property(r => r.NombreArchivoOriginal).HasMaxLength(RequisitoDocumental.LongitudMaximaNombreArchivo);

        builder.HasIndex(r => r.CentroId);

        // FK real — ver P0-1 de docs/business/MATURITY_REVIEW.md.
        builder.HasOne<Centro>().WithMany()
            .HasForeignKey(r => new { r.TenantId, r.CentroId })
            .HasPrincipalKey(c => new { c.TenantId, c.Id })
            .OnDelete(DeleteBehavior.Restrict);

        // Filtro global (soft delete + tenant) centralizado en CaeManagerDbContext.OnModelCreating.
    }
}
