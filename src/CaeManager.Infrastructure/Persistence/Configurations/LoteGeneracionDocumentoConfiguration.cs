using CaeManager.Domain.Plantillas;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class LoteGeneracionDocumentoConfiguration : IEntityTypeConfiguration<LoteGeneracionDocumento>
{
    public void Configure(EntityTypeBuilder<LoteGeneracionDocumento> builder)
    {
        builder.ToTable("LotesGeneracionDocumento");
        builder.HasKey(l => l.Id);

        builder.Property(l => l.ContextoJson).HasColumnType("text");
        builder.Property(l => l.Estado).IsRequired().HasConversion<string>();

        builder.HasIndex(l => new { l.TenantId, l.PlantillaDocumentoVersionId });
        builder.HasIndex(l => new { l.TenantId, l.Id }).IsUnique();

        builder.HasOne<PlantillaDocumentoVersion>()
            .WithMany()
            .HasForeignKey(l => l.PlantillaDocumentoVersionId)
            .OnDelete(DeleteBehavior.Restrict);

        // Filtro global de tenant centralizado en CaeManagerDbContext.OnModelCreating.
    }
}

public class ItemGeneracionDocumentoConfiguration : IEntityTypeConfiguration<ItemGeneracionDocumento>
{
    public void Configure(EntityTypeBuilder<ItemGeneracionDocumento> builder)
    {
        builder.ToTable("ItemsGeneracionDocumento");
        builder.HasKey(i => i.Id);

        builder.Property(i => i.Estado).IsRequired().HasConversion<string>();
        builder.Property(i => i.Error).HasMaxLength(ItemGeneracionDocumento.LongitudMaximaError);

        builder.HasIndex(i => new { i.TenantId, i.LoteGeneracionDocumentoId });

        // Restrict: un lote con items ya procesados no se borra en silencio.
        builder.HasOne<LoteGeneracionDocumento>()
            .WithMany()
            .HasForeignKey(i => new { i.TenantId, i.LoteGeneracionDocumentoId })
            .HasPrincipalKey(l => new { l.TenantId, l.Id })
            .OnDelete(DeleteBehavior.Restrict);

        // Filtro global de tenant centralizado en CaeManagerDbContext.OnModelCreating.
    }
}
