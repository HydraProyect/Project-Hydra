using CaeManager.Domain.Proyectos;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class ProyectoTecnicoConfiguration : IEntityTypeConfiguration<ProyectoTecnico>
{
    public void Configure(EntityTypeBuilder<ProyectoTecnico> builder)
    {
        builder.ToTable("ProyectosTecnicos");
        builder.HasKey(pt => pt.Id);

        builder.HasIndex(pt => new { pt.TenantId, pt.ProyectoId, pt.TrabajadorId, pt.FechaAlta }).IsUnique();
        builder.HasIndex(pt => pt.TrabajadorId);
    }
}
