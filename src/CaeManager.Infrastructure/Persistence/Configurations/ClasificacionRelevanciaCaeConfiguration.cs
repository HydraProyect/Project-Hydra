using CaeManager.Domain.Comunicaciones;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class ClasificacionRelevanciaCaeConfiguration : IEntityTypeConfiguration<ClasificacionRelevanciaCae>
{
    public void Configure(EntityTypeBuilder<ClasificacionRelevanciaCae> builder)
    {
        builder.ToTable("ClasificacionesRelevanciaCae");
        builder.HasKey(c => c.Id);

        builder.HasIndex(c => c.ConversacionId).IsUnique();

        // Filtro global de tenant centralizado en CaeManagerDbContext.OnModelCreating.
    }
}
