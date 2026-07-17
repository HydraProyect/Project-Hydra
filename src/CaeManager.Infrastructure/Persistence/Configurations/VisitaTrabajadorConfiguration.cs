using CaeManager.Domain.Visitas;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class VisitaTrabajadorConfiguration : IEntityTypeConfiguration<VisitaTrabajador>
{
    public void Configure(EntityTypeBuilder<VisitaTrabajador> builder)
    {
        builder.ToTable("VisitasTrabajadores");
        builder.HasKey(vt => vt.Id);

        builder.HasIndex(vt => vt.VisitaId);
        builder.HasIndex(vt => vt.TrabajadorId);
        builder.HasIndex(vt => new { vt.VisitaId, vt.TrabajadorId }).IsUnique();
    }
}
