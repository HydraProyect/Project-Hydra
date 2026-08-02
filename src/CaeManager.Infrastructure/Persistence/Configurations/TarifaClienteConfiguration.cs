using CaeManager.Domain.Clientes;
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
               .HasFilter($"NOT \"{nameof(TarifaCliente.EstaEliminado)}\"");

        builder.HasIndex(t => t.ClienteId);

        // FK real — ver P0-1 de docs/business/MATURITY_REVIEW.md.
        builder.HasOne<Cliente>().WithMany()
            .HasForeignKey(t => new { t.TenantId, t.ClienteId })
            .HasPrincipalKey(c => new { c.TenantId, c.Id })
            .OnDelete(DeleteBehavior.Restrict);
    }
}
