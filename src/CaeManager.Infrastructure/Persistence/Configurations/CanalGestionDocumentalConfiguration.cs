using CaeManager.Domain.Centros;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class CanalGestionDocumentalConfiguration : IEntityTypeConfiguration<CanalGestionDocumental>
{
    public void Configure(EntityTypeBuilder<CanalGestionDocumental> builder)
    {
        builder.ToTable("CanalesGestionDocumental");
        builder.HasKey(c => c.Id);

        builder.Property(c => c.NombrePlataforma).HasMaxLength(CanalGestionDocumental.LongitudMaximaNombrePlataforma);
        builder.Property(c => c.UrlAcceso).HasMaxLength(CanalGestionDocumental.LongitudMaximaUrlAcceso);
        builder.Property(c => c.EmailsDestinatarios).HasMaxLength(CanalGestionDocumental.LongitudMaximaEmailsDestinatarios);
        builder.Property(c => c.NombreContacto).HasMaxLength(CanalGestionDocumental.LongitudMaximaNombreContacto);
        builder.Property(c => c.Notas).HasMaxLength(CanalGestionDocumental.LongitudMaximaNotas);

        // El cifrado de Usuario/Contrasena se configura en CaeManagerDbContext.OnModelCreating,
        // porque necesita el IDataProtector inyectado en el propio DbContext.

        builder.HasIndex(c => new { c.TenantId, c.CentroId }).IsUnique();

        // FK real — ver P0-1 de docs/business/MATURITY_REVIEW.md.
        builder.HasOne<Centro>().WithMany()
            .HasForeignKey(canal => new { canal.TenantId, canal.CentroId })
            .HasPrincipalKey(centro => new { centro.TenantId, centro.Id })
            .OnDelete(DeleteBehavior.Restrict);
    }
}
