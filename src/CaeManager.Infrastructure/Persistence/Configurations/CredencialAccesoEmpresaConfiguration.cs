using CaeManager.Domain.Empresas;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class CredencialAccesoEmpresaConfiguration : IEntityTypeConfiguration<CredencialAccesoEmpresa>
{
    public void Configure(EntityTypeBuilder<CredencialAccesoEmpresa> builder)
    {
        builder.ToTable("CredencialesAccesoEmpresa");
        builder.HasKey(c => c.Id);

        builder.Property(c => c.UrlAcceso).HasMaxLength(CredencialAccesoEmpresa.LongitudMaximaUrlAcceso);
        builder.Property(c => c.CampoEmpresa).HasMaxLength(CredencialAccesoEmpresa.LongitudMaximaCampoEmpresa);
        builder.Property(c => c.Notas).HasMaxLength(CredencialAccesoEmpresa.LongitudMaximaNotas);

        // El cifrado de Usuario/Contrasena se configura en CaeManagerDbContext.OnModelCreating,
        // porque necesita el IDataProtector inyectado en el propio DbContext (mismo patrón que PlataformaAcceso).

        builder.HasIndex(c => new { c.TenantId, c.EmpresaId }).IsUnique();

        // F3 (verificación del modelo real) — esta entidad NO tenía FK
        // declarada hacia Empresa hasta ahora (solo el índice único de
        // arriba); f3-revision-adversaria-2026-08-25.md punto 3 la
        // clasificaba por error como "2 FKs opcionales", cuando en realidad
        // es una sola FK, y ni siquiera existía todavía. Se añade aquí,
        // consistente con el patrón P0-1 del resto del repositorio.
        builder.HasOne<Empresa>().WithMany()
            .HasForeignKey(c => new { c.TenantId, c.EmpresaId })
            .HasPrincipalKey(e => new { e.TenantId, e.Id })
            .OnDelete(DeleteBehavior.Restrict);
    }
}
