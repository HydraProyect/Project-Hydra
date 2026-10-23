using CaeManager.Domain.AutorizacionesLectura;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class AccesoLecturaInboundConfiguration : IEntityTypeConfiguration<AccesoLecturaInbound>
{
    public void Configure(EntityTypeBuilder<AccesoLecturaInbound> builder)
    {
        builder.ToTable("AccesosLecturaInbound", t =>
            t.HasCheckConstraint(
                "CK_AccesosLecturaInbound_DocumentoSegunTipo",
                "(\"Tipo\" = 'Listado' AND \"DocumentoId\" IS NULL) OR (\"Tipo\" = 'Apertura' AND \"DocumentoId\" IS NOT NULL)"));
        builder.HasKey(a => a.Id);

        builder.Property(a => a.Tipo).HasConversion<string>().HasMaxLength(20).IsRequired();

        builder.HasIndex(a => new { a.TenantId, a.AutorizacionId, a.MomentoUtc })
            .HasDatabaseName("IX_AccesosLecturaInbound_Autorizacion");

        // Compuesta con TenantId: el acceso y la autorización que lo ampara son siempre del mismo Tenant. ContratoAccesoId,
        // TenantLectorId y UsuarioId son de otro Tenant y no admiten FK.
        builder.HasOne<AutorizacionLecturaDocumental>().WithMany()
            .HasForeignKey(a => new { a.TenantId, a.AutorizacionId })
            .HasPrincipalKey(a => new { a.TenantId, a.Id })
            .OnDelete(DeleteBehavior.Restrict);

        // Filtro global de tenant centralizado en CaeManagerDbContext.OnModelCreating.
    }
}
