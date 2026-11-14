using CaeManager.Domain.Facturacion;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class CobroLiquidacionOperadorCaeConfiguration : IEntityTypeConfiguration<CobroLiquidacionOperadorCae>
{
    public void Configure(EntityTypeBuilder<CobroLiquidacionOperadorCae> builder)
    {
        builder.ToTable("CobrosLiquidacionOperadorCae", t =>
        {
            // El Operador CAE no se liquida a sí mismo, y el estado es uno de los tres que conoce el dominio
            // (la base lo exige aunque se salte el dominio).
            t.HasCheckConstraint("CK_CobrosLiquidacionOperadorCae_OperadorNoEsElTenantBeneficiario", "\"PropietarioTenantId\" <> \"TenantId\"");
            t.HasCheckConstraint("CK_CobrosLiquidacionOperadorCae_EstadoConocido", "\"Estado\" IN (1, 2, 3)");
            t.HasCheckConstraint("CK_CobrosLiquidacionOperadorCae_MesValido", "\"Mes\" BETWEEN 1 AND 12");
        });
        builder.HasKey(c => c.Id);
        // Una marca por liquidación: Operador emisor, Tenant beneficiario y mes.
        builder.HasIndex(c => new { c.TenantId, c.PropietarioTenantId, c.Anyo, c.Mes }).IsUnique();
        builder.Property(c => c.Estado).HasConversion<int>();
        builder.Property(c => c.ReferenciaFacturaEmisor).HasMaxLength(CobroLiquidacionOperadorCae.LongitudMaximaReferencia);
    }
}
