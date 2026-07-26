using CaeManager.Domain.Facturacion;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class TarifaClienteConfiguration : IEntityTypeConfiguration<TarifaCliente>
{
    public void Configure(EntityTypeBuilder<TarifaCliente> builder)
    {
        builder.ToTable("TarifasCliente");
        builder.HasKey(t => t.Id);

        builder.Property(t => t.PrecioUnitario).HasColumnType("decimal(18,4)");
        builder.Property(t => t.MonedaIso).HasMaxLength(TarifaCliente.LongitudMonedaIso).IsRequired();

        // Una única tarifa activa por (tenant, cliente, concepto)
        builder.HasIndex(t => new { t.TenantId, t.ClienteId, t.Concepto })
               .IsUnique()
               .HasFilter($"\"{nameof(TarifaCliente.EstaEliminado)}\" = 0");

        builder.HasIndex(t => t.ClienteId);
    }
}
