using CaeManager.Domain.Empresas;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class EmpresaClienteConfiguration : IEntityTypeConfiguration<EmpresaCliente>
{
    public void Configure(EntityTypeBuilder<EmpresaCliente> builder)
    {
        builder.ToTable("EmpresasClientes");
        builder.HasKey(ec => ec.Id);

        builder.HasIndex(ec => new { ec.TenantId, ec.EmpresaId, ec.ClienteId }).IsUnique();
        builder.HasIndex(ec => ec.ClienteId);

        // F3 (verificación del modelo real) — EmpresaId y ClienteId apuntan
        // ahora ambas a la misma Empresas unificada. FKs reales — ver P0-1
        // de docs/business/MATURITY_REVIEW.md.
        builder.HasOne<Empresa>().WithMany()
            .HasForeignKey(ec => new { ec.TenantId, ec.EmpresaId })
            .HasPrincipalKey(e => new { e.TenantId, e.Id })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Empresa>().WithMany()
            .HasForeignKey(ec => new { ec.TenantId, ec.ClienteId })
            .HasPrincipalKey(e => new { e.TenantId, e.Id })
            .OnDelete(DeleteBehavior.Restrict);

        // Bloqueante de f3-revision-adversaria-2026-08-25.md punto 6: antes
        // de F3, EmpresaId y ClienteId apuntaban a tablas distintas, así que
        // una fila autorreferente era estructuralmente imposible. Tras
        // unificar, ambas apuntan a Empresas — sin este CHECK, la misma
        // organización podría "relacionarse consigo misma".
        builder.ToTable(t => t.HasCheckConstraint(
            "CK_EmpresasClientes_NoAutorreferencia",
            @"""EmpresaId"" <> ""ClienteId"""));
    }
}
