using CaeManager.Domain.Auditoria;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class RegistroAuditoriaConfiguration : IEntityTypeConfiguration<RegistroAuditoria>
{
    public void Configure(EntityTypeBuilder<RegistroAuditoria> builder)
    {
        builder.ToTable("RegistrosAuditoria");
        builder.HasKey(r => r.Id);

        builder.Property(r => r.EntidadTipo).IsRequired().HasMaxLength(200);
        builder.Property(r => r.Accion).IsRequired().HasMaxLength(20);
        builder.Property(r => r.DatosAntes).HasColumnType("TEXT");
        builder.Property(r => r.DatosDespues).HasColumnType("TEXT");

        builder.HasIndex(r => new { r.EntidadTipo, r.EntidadId });
        builder.HasIndex(r => r.FechaUtc);
    }
}
