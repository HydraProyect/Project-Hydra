using CaeManager.Domain.Reclamaciones;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class ReclamacionDocumentalConfiguration : IEntityTypeConfiguration<ReclamacionDocumental>
{
    public void Configure(EntityTypeBuilder<ReclamacionDocumental> builder)
    {
        builder.ToTable("ReclamacionesDocumentales");
        builder.HasKey(r => r.Id);

        builder.Property(r => r.DestinatarioEmail).IsRequired().HasMaxLength(ReclamacionDocumental.LongitudMaximaDestinatarioEmail);

        builder.HasIndex(r => new { r.TenantId, r.ClienteId });

        // Mensajes/Participantes de Conversacion es el mismo patrón: colección
        // de solo lectura respaldada por campo privado, EF lee/escribe el
        // campo directamente.
        builder.HasMany(r => r.Documentos)
            .WithOne()
            .HasForeignKey(d => d.ReclamacionDocumentalId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(r => r.Documentos).UsePropertyAccessMode(PropertyAccessMode.Field);

        // Filtro global (soft delete + tenant) centralizado en CaeManagerDbContext.OnModelCreating.
    }
}
