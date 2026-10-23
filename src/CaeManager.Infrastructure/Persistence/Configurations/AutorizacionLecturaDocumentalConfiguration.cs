using CaeManager.Domain.AutorizacionesLectura;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class AutorizacionLecturaDocumentalConfiguration : IEntityTypeConfiguration<AutorizacionLecturaDocumental>
{
    public void Configure(EntityTypeBuilder<AutorizacionLecturaDocumental> builder)
    {
        // Respaldo en la base de las invariantes de AutorizacionLecturaDocumental.Conceder/Revocar y del comando de alta.
        // El destinatario distinto del propio Tenant solo se puede comprobar aquí y en el comando: el dominio no conoce
        // el TenantId hasta que el interceptor lo sella al guardar.
        builder.ToTable("AutorizacionesLecturaDocumental", t =>
        {
            t.HasCheckConstraint(
                "CK_AutorizacionesLectura_DestinatarioAjeno",
                $"\"{nameof(AutorizacionLecturaDocumental.TenantDestinatarioId)}\" <> \"{nameof(AutorizacionLecturaDocumental.TenantId)}\"");
            t.HasCheckConstraint(
                "CK_AutorizacionesLectura_HastaNoAnteriorADesde",
                $"\"{nameof(AutorizacionLecturaDocumental.VigenciaHasta)}\" IS NULL " +
                $"OR \"{nameof(AutorizacionLecturaDocumental.VigenciaHasta)}\" >= \"{nameof(AutorizacionLecturaDocumental.VigenciaDesde)}\"");
            t.HasCheckConstraint(
                "CK_AutorizacionesLectura_RevocacionEntera",
                $"(\"{nameof(AutorizacionLecturaDocumental.RevocadaEnUtc)}\" IS NULL) = " +
                $"(\"{nameof(AutorizacionLecturaDocumental.RevocadaPorUsuarioId)}\" IS NULL)");
        });
        builder.HasKey(a => a.Id);

        // Una sola autorización sin revocar por (Tenant destinatario, contrato): revocar libera el hueco y la siguiente
        // es otra fila. Cierra la carrera entre dos altas simultáneas que el comando no puede cerrar con su lectura.
        builder.HasIndex(a => new { a.TenantId, a.TenantDestinatarioId, a.ContratoAccesoId })
            .IsUnique()
            .HasFilter($"\"{nameof(AutorizacionLecturaDocumental.RevocadaEnUtc)}\" IS NULL")
            .HasDatabaseName("IX_AutorizacionesLectura_UnaSinRevocarPorContrato");

        // Filtro global de tenant centralizado en CaeManagerDbContext.OnModelCreating. Sin soft delete: se revoca.
    }
}
