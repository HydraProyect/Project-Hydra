using CaeManager.Domain.Comunicaciones;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class ContactoWhatsAppConfiguration : IEntityTypeConfiguration<ContactoWhatsApp>
{
    public void Configure(EntityTypeBuilder<ContactoWhatsApp> builder)
    {
        builder.ToTable("ContactosWhatsApp");
        builder.HasKey(c => c.Id);

        builder.Property(c => c.Telefono).IsRequired().HasMaxLength(ContactoWhatsApp.LongitudMaximaTelefono);
        builder.Property(c => c.Nombre).HasMaxLength(ContactoWhatsApp.LongitudMaximaNombre);

        // Único por tenant: un teléfono se resuelve contra un solo Cliente.
        builder.HasIndex(c => new { c.TenantId, c.Telefono }).IsUnique();

        // Filtro global de tenant centralizado en CaeManagerDbContext.OnModelCreating.
    }
}
