using CaeManager.Domain.Trabajadores;
using CaeManager.Domain.Vehiculos;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class VehiculoTrabajadorConfiguration : IEntityTypeConfiguration<VehiculoTrabajador>
{
    public void Configure(EntityTypeBuilder<VehiculoTrabajador> builder)
    {
        builder.ToTable("VehiculosTrabajadores");
        builder.HasKey(c => c.Id);
        builder.HasIndex(c => new { c.TenantId, c.VehiculoId, c.TrabajadorId }).IsUnique();
        builder.HasOne<Vehiculo>().WithMany(v => v.Conductores)
            .HasForeignKey(c => new { c.TenantId, c.VehiculoId })
            .HasPrincipalKey(v => new { v.TenantId, v.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(c => c.Trabajador).WithMany()
            .HasForeignKey(c => new { c.TenantId, c.TrabajadorId })
            .HasPrincipalKey(t => new { t.TenantId, t.Id })
            .OnDelete(DeleteBehavior.Restrict);
    }
}
