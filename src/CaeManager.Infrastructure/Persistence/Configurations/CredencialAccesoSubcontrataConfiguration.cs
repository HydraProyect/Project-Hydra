using CaeManager.Domain.Empresas;
using CaeManager.Domain.Subcontratas;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class CredencialAccesoSubcontrataConfiguration : IEntityTypeConfiguration<CredencialAccesoSubcontrata>
{
    public void Configure(EntityTypeBuilder<CredencialAccesoSubcontrata> builder)
    {
        builder.ToTable("CredencialesAccesoSubcontrata");
        builder.HasKey(c => c.Id);

        builder.Property(c => c.UrlAcceso).HasMaxLength(CredencialAccesoSubcontrata.LongitudMaximaUrlAcceso);
        builder.Property(c => c.CampoEmpresa).HasMaxLength(CredencialAccesoSubcontrata.LongitudMaximaCampoEmpresa);
        builder.Property(c => c.Notas).HasMaxLength(CredencialAccesoSubcontrata.LongitudMaximaNotas);

        // El cifrado de Usuario/Contrasena se configura en CaeManagerDbContext.OnModelCreating,
        // porque necesita el IDataProtector inyectado en el propio DbContext (mismo patrón que CredencialAccesoEmpresa).

        builder.HasIndex(c => new { c.TenantId, c.SubcontrataId }).IsUnique();

        // F3 (verificación del modelo real) — SubcontrataId no tenía FK
        // declarada hoy; se añade aquí ya apuntando a la Empresas
        // unificada (no a Subcontratas), consistente con el patrón P0-1.
        // Corrige la clasificación de f3-revision-adversaria-2026-08-25.md
        // punto 3, que trataba CredencialAccesoPortal como una única tabla
        // con dos FKs — en realidad son dos tablas separadas (herencia por
        // tabla concreta), cada una con una sola FK.
        builder.HasOne<Empresa>().WithMany()
            .HasForeignKey(c => new { c.TenantId, c.SubcontrataId })
            .HasPrincipalKey(e => new { e.TenantId, e.Id })
            .OnDelete(DeleteBehavior.Restrict);
    }
}
