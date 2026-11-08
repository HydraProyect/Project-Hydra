using CaeManager.Domain.Documentos;
using CaeManager.Domain.Gestiones;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class GestionAutomaticaConfiguration : IEntityTypeConfiguration<GestionAutomatica>
{
    public void Configure(EntityTypeBuilder<GestionAutomatica> builder)
    {
        builder.ToTable("GestionesAutomaticas");
        builder.HasKey(g => g.Id);

        builder.Property(g => g.Origen).HasConversion<int>();
        builder.Ignore(g => g.EstaSustituida);

        // Una Gestión automática por Documento: alta y renovación con archivo hacen nacer siempre un Documento
        // nuevo, así que el Documento identifica el acto y la base no admite dos Gestiones del mismo acto.
        builder.HasIndex(g => new { g.TenantId, g.DocumentoId }).IsUnique();

        // Tenant propietario = el del Documento: la clave foránea compuesta impide colgarla de un Documento de
        // otro Tenant. En cascada, como las acreditaciones que agrupa: si se purga el Documento, no queda nada.
        builder.HasOne<Documento>().WithMany()
            .HasForeignKey(g => new { g.TenantId, g.DocumentoId })
            .HasPrincipalKey(d => new { d.TenantId, d.Id })
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(g => g.Pasos)
            .WithOne()
            .HasForeignKey(p => new { p.TenantId, p.GestionAutomaticaId })
            .HasPrincipalKey(g => new { g.TenantId, g.Id })
            .OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(g => g.Pasos).UsePropertyAccessMode(PropertyAccessMode.Field);

        // Filtro global (soft delete + tenant) centralizado en CaeManagerDbContext.OnModelCreating.
    }
}

public class PasoGestionAutomaticaConfiguration : IEntityTypeConfiguration<PasoGestionAutomatica>
{
    public void Configure(EntityTypeBuilder<PasoGestionAutomatica> builder)
    {
        builder.ToTable("PasosGestionAutomatica");
        builder.HasKey(p => p.Id);

        builder.HasIndex(p => new { p.TenantId, p.GestionAutomaticaId, p.AcreditacionId }).IsUnique();
        builder.HasIndex(p => new { p.TenantId, p.AcreditacionId });

        // El paso es una acreditación del mismo Tenant: sin acreditación no hay paso.
        builder.HasOne<AcreditacionDocumentoPlataforma>().WithMany()
            .HasForeignKey(p => new { p.TenantId, p.AcreditacionId })
            .HasPrincipalKey(a => new { a.TenantId, a.Id })
            .OnDelete(DeleteBehavior.Cascade);

        // Filtro global (tenant) centralizado en CaeManagerDbContext.OnModelCreating.
    }
}
