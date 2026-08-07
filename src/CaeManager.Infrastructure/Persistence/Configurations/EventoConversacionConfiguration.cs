using CaeManager.Domain.Comunicaciones;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class EventoConversacionConfiguration : IEntityTypeConfiguration<EventoConversacion>
{
    public void Configure(EntityTypeBuilder<EventoConversacion> builder)
    {
        builder.ToTable("EventosConversacion");
        builder.HasKey(e => e.Id);

        builder.HasIndex(e => e.ConversacionId);

        // Filtro global de tenant centralizado en CaeManagerDbContext.OnModelCreating.
    }
}
