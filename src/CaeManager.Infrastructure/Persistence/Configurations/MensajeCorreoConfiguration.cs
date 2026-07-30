using CaeManager.Domain.Comunicaciones;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class MensajeCorreoConfiguration : IEntityTypeConfiguration<MensajeCorreo>
{
    public void Configure(EntityTypeBuilder<MensajeCorreo> builder)
    {
        builder.ToTable("MensajesCorreo");
        builder.HasKey(m => m.Id);

        builder.Property(m => m.RemitenteEmail).IsRequired().HasMaxLength(320);
        builder.Property(m => m.CuerpoHtml).IsRequired();

        builder.HasIndex(m => m.ConversacionCorreoId);
        builder.HasIndex(m => m.FechaUtc);

        // Filtro global de tenant centralizado en CaeManagerDbContext.OnModelCreating.
    }
}
