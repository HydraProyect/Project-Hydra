using CaeManager.Domain.Alertas;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class AlertaConfiguration : IEntityTypeConfiguration<Alerta>
{
    public void Configure(EntityTypeBuilder<Alerta> builder)
    {
        builder.ToTable("Alertas");
        builder.HasKey(a => a.Id);

        builder.Property(a => a.Nivel).HasConversion<string>().HasMaxLength(20);

        builder.HasIndex(a => a.DocumentoId);
    }
}
