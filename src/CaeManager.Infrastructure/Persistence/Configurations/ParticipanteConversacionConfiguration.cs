using CaeManager.Domain.Comunicaciones;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class ParticipanteConversacionConfiguration : IEntityTypeConfiguration<ParticipanteConversacion>
{
    public void Configure(EntityTypeBuilder<ParticipanteConversacion> builder)
    {
        builder.ToTable("ParticipantesConversacion");
        builder.HasKey(p => p.Id);

        builder.Property(p => p.Email).IsRequired().HasMaxLength(320);

        builder.HasIndex(p => p.ConversacionCorreoId);

        // Filtro global de tenant centralizado en CaeManagerDbContext.OnModelCreating.
    }
}
