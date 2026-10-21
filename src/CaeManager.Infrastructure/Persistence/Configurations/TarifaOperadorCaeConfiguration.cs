using CaeManager.Domain.Dashboard;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class TarifaOperadorCaeConfiguration : IEntityTypeConfiguration<TarifaOperadorCae>
{
    public void Configure(EntityTypeBuilder<TarifaOperadorCae> builder)
    {
        builder.ToTable("TarifasOperadorCae", t =>
        {
            // El Operador CAE no se cobra a sí mismo y no hay precios negativos (la base lo exige aunque se salte el dominio).
            t.HasCheckConstraint("CK_TarifasOperadorCae_OperadorNoEsElTenantBeneficiario", "\"PropietarioTenantId\" <> \"TenantId\"");
            t.HasCheckConstraint("CK_TarifasOperadorCae_PrecioNoNegativo", "\"PrecioUnitario\" >= 0");
        });
        builder.HasKey(t => t.Id);
        // La versión vigente de un concepto para una Empresa gestionada: la más reciente con VigenteDesdeUtc <= instante.
        builder.HasIndex(t => new { t.TenantId, t.PropietarioTenantId, t.Concepto, t.VigenteDesdeUtc });
        builder.Property(t => t.Concepto).HasConversion<int>();
        builder.Property(t => t.PrecioUnitario).HasPrecision(18, 2);
        builder.Property(t => t.MonedaIso).IsRequired().HasMaxLength(TarifaOperadorCae.LongitudMonedaIso);
    }
}
