using CaeManager.Domain.Centros;
using CaeManager.Domain.Integraciones;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class CanalGestionDocumentalConfiguration : IEntityTypeConfiguration<CanalGestionDocumental>
{
    public void Configure(EntityTypeBuilder<CanalGestionDocumental> builder)
    {
        builder.ToTable("CanalesGestionDocumental");
        builder.HasKey(c => c.Id);

        builder.Property(c => c.UrlAcceso).HasMaxLength(CanalGestionDocumental.LongitudMaximaUrlAcceso);
        builder.Property(c => c.EmailsDestinatarios).HasMaxLength(CanalGestionDocumental.LongitudMaximaEmailsDestinatarios);
        builder.Property(c => c.NombreContacto).HasMaxLength(CanalGestionDocumental.LongitudMaximaNombreContacto);
        builder.Property(c => c.Notas).HasMaxLength(CanalGestionDocumental.LongitudMaximaNotas);
        builder.Property(c => c.EtiquetaProposito).IsRequired().HasMaxLength(CanalGestionDocumental.LongitudMaximaEtiquetaProposito);

        // El cifrado de Usuario/Contrasena se configura en CaeManagerDbContext.OnModelCreating,
        // porque necesita el IDataProtector inyectado en el propio DbContext.

        // N canales por Centro desde el Lote 0-E (PLAN-EJECUCION-UX.md § 0.6) —
        // el índice único de (TenantId, CentroId) que imponía el 1:1 pasa a ser
        // un índice normal de búsqueda.
        builder.HasIndex(c => new { c.TenantId, c.CentroId });

        // "A lo sumo un principal por Centro" se sostiene en la base de datos,
        // no solo en el Command: dos peticiones concurrentes marcando canales
        // distintos como principal pasarían las dos la comprobación en memoria.
        // Filtrado por EstaEliminado para que un canal borrado (soft delete) no
        // siga bloqueando el hueco de principal.
        builder.HasIndex(
                c => new { c.TenantId, c.CentroId },
                "IX_CanalesGestionDocumental_TenantId_CentroId_Principal")
            .IsUnique()
            .HasFilter("\"EsPrincipal\" AND NOT \"EstaEliminado\"");

        // FK real — ver P0-1 de docs/business/MATURITY_REVIEW.md.
        builder.HasOne<Centro>().WithMany()
            .HasForeignKey(canal => new { canal.TenantId, canal.CentroId })
            .HasPrincipalKey(centro => new { centro.TenantId, centro.Id })
            .OnDelete(DeleteBehavior.Restrict);

        // ProveedorPlataformaCae es catálogo global (sin TenantId) — FK simple
        // por Id, sin componer con TenantId como la de Centro. Nullable porque
        // los canales de tipo Email nunca lo tienen, y los de Plataforma
        // migrados desde el antiguo NombrePlataforma libre pueden quedar sin
        // resolver hasta la siguiente edición (Lote 2-B).
        builder.HasOne<ProveedorPlataformaCae>().WithMany()
            .HasForeignKey(c => c.ProveedorPlataformaCaeId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
