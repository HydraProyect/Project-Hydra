using CaeManager.Domain.Comunicaciones;
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
        builder.Property(r => r.ConversacionId);

        builder.HasIndex(r => new { r.TenantId, r.ClienteId });

        // Restrict, como el resto de referencias entre agregados de este
        // repositorio: la reclamación es append-only (es el historial de qué se
        // reclamó y cuándo), así que perder su vínculo con la conversación en
        // cascada sería perder rastro. Con soft delete la cascada no llegaría a
        // dispararse casi nunca, pero la regla la fija el modelo, no el uso.
        builder.HasOne<Conversacion>()
            .WithMany()
            .HasForeignKey(r => r.ConversacionId)
            .OnDelete(DeleteBehavior.Restrict);

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
