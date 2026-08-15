using CaeManager.Domain.Plantillas;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class PlantillaDocumentoVersionConfiguration : IEntityTypeConfiguration<PlantillaDocumentoVersion>
{
    public void Configure(EntityTypeBuilder<PlantillaDocumentoVersion> builder)
    {
        builder.ToTable("PlantillasDocumentoVersion");
        builder.HasKey(v => v.Id);

        builder.Property(v => v.ArchivoOriginalUrl).HasMaxLength(PlantillaDocumentoVersion.LongitudMaximaArchivoOriginalUrl);
        builder.Property(v => v.HashSha256ArchivoOriginal).HasMaxLength(PlantillaDocumentoVersion.LongitudHashSha256);
        builder.Property(v => v.EstadoConfiguracion).IsRequired().HasConversion<string>();

        // Restrict: borrar una plantilla con historial de versiones debe
        // fallar de forma visible, no arrastrarlas en silencio — mismo
        // criterio que ContactoAgenda con su propietario polimórfico.
        builder.HasOne<PlantillaDocumento>()
            .WithMany()
            .HasForeignKey(v => v.PlantillaDocumentoId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(v => new { v.TenantId, v.PlantillaDocumentoId, v.NumeroVersion }).IsUnique();

        builder.HasMany(v => v.Elementos)
            .WithOne()
            .HasForeignKey(e => e.PlantillaDocumentoVersionId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(v => v.Elementos).UsePropertyAccessMode(PropertyAccessMode.Field);

        // Filtro global de tenant centralizado en CaeManagerDbContext.OnModelCreating.
    }
}

public class PlantillaElementoConfiguration : IEntityTypeConfiguration<PlantillaElemento>
{
    public void Configure(EntityTypeBuilder<PlantillaElemento> builder)
    {
        builder.ToTable("PlantillasElemento");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.Tipo).IsRequired().HasConversion<string>();
        builder.Property(e => e.EtiquetaVisible).IsRequired().HasMaxLength(PlantillaElemento.LongitudMaximaEtiquetaVisible);
        builder.Property(e => e.FuenteDato).HasConversion<string>();
        builder.Property(e => e.ValorConstante).HasMaxLength(PlantillaElemento.LongitudMaximaValorConstante);
        builder.Property(e => e.Formato).HasMaxLength(PlantillaElemento.LongitudMaximaFormato);
        builder.Property(e => e.RolFirmante).HasConversion<string>();
        builder.Property(e => e.NombreCampoAcroForm).HasMaxLength(PlantillaElemento.LongitudMaximaNombreCampoAcroForm);

        builder.HasIndex(e => e.PlantillaDocumentoVersionId);

        // Filtro global de tenant centralizado en CaeManagerDbContext.OnModelCreating.
    }
}
