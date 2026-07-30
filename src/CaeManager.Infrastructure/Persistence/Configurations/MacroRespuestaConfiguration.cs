using CaeManager.Domain.Comunicaciones;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class MacroRespuestaConfiguration : IEntityTypeConfiguration<MacroRespuesta>
{
    public void Configure(EntityTypeBuilder<MacroRespuesta> builder)
    {
        builder.ToTable("MacrosRespuesta");
        builder.HasKey(m => m.Id);

        builder.Property(m => m.Titulo).IsRequired().HasMaxLength(MacroRespuesta.LongitudMaximaTitulo);
        builder.Property(m => m.CuerpoHtml).IsRequired();

        builder.HasIndex(m => new { m.TenantId, m.ClienteId });

        // Filtro global (soft delete + tenant) centralizado en CaeManagerDbContext.OnModelCreating.
    }
}
