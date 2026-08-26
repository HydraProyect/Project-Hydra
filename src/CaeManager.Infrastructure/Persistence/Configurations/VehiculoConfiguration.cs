using CaeManager.Domain.Empresas;
using CaeManager.Domain.Vehiculos;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class VehiculoConfiguration : IEntityTypeConfiguration<Vehiculo>
{
    public void Configure(EntityTypeBuilder<Vehiculo> builder)
    {
        builder.ToTable("Vehiculos");
        builder.HasKey(v => v.Id);

        builder.Property(v => v.Nombre).IsRequired().HasMaxLength(Vehiculo.LongitudMaximaNombre);
        builder.Property(v => v.Modelo).IsRequired().HasMaxLength(Vehiculo.LongitudMaximaModelo);
        builder.Property(v => v.NumeroPlaca).IsRequired().HasMaxLength(Vehiculo.LongitudMaximaNumeroPlaca);

        builder.HasIndex(v => new { v.TenantId, v.NumeroPlaca }).IsUnique();
        builder.HasIndex(v => v.EmpresaId);
        builder.HasIndex(v => v.SubcontrataId);

        // FKs reales — ver P0-1 de docs/business/MATURITY_REVIEW.md. Mismo
        // criterio Empresa-XOR-Subcontrata que Trabajador. F3b-Subcontrata —
        // SubcontrataId repunta contra Empresas también (dos HasOne<Empresa>
        // distintos por columna, mismo patrón ya usado en
        // SubcontrataClienteConfiguration).
        builder.HasOne<Empresa>().WithMany()
            .HasForeignKey(v => new { v.TenantId, v.EmpresaId })
            .HasPrincipalKey(e => new { e.TenantId, e.Id })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Empresa>().WithMany()
            .HasForeignKey(v => new { v.TenantId, v.SubcontrataId })
            .HasPrincipalKey(s => new { s.TenantId, s.Id })
            .OnDelete(DeleteBehavior.Restrict);

        // Filtro global (soft delete + tenant) centralizado en CaeManagerDbContext.OnModelCreating.
    }
}
