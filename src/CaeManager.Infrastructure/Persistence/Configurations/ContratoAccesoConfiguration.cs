using CaeManager.Domain.Centros;
using CaeManager.Domain.ContratosAcceso;
using CaeManager.Domain.Empresas;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class ContratoAccesoConfiguration : IEntityTypeConfiguration<ContratoAcceso>
{
    public void Configure(EntityTypeBuilder<ContratoAcceso> builder)
    {
        // Respaldo en la base de las invariantes de ContratoAcceso.Crear/Actualizar/Cerrar y del validador del alta:
        // una escritura que se salte el dominio (SQL, migración de datos) tampoco puede dejarlas rotas.
        builder.ToTable("ContratosAcceso", t =>
        {
            t.HasCheckConstraint(
                "CK_ContratosAcceso_ContratistaDistintaDelCliente",
                $"\"{nameof(ContratoAcceso.ClienteEmpresarialId)}\" <> \"{nameof(ContratoAcceso.EmpresaContratistaId)}\"");
            t.HasCheckConstraint(
                "CK_ContratosAcceso_FinNoAnteriorAlInicio",
                $"\"{nameof(ContratoAcceso.FechaFin)}\" IS NULL " +
                $"OR \"{nameof(ContratoAcceso.FechaFin)}\" >= \"{nameof(ContratoAcceso.FechaInicio)}\"");
            t.HasCheckConstraint(
                "CK_ContratosAcceso_CierreNoAnteriorAlInicio",
                $"\"{nameof(ContratoAcceso.FechaCierreReal)}\" IS NULL " +
                $"OR \"{nameof(ContratoAcceso.FechaCierreReal)}\" >= \"{nameof(ContratoAcceso.FechaInicio)}\"");
            // El Tenant de la Empresa contratista nunca es el propio: ContratoAcceso.VincularTenantContratista no lo ve
            // en un contrato aún sin guardar, y el comando lo compara con el Tenant activo.
            t.HasCheckConstraint(
                "CK_ContratosAcceso_TenantContratistaAjeno",
                $"\"{nameof(ContratoAcceso.TenantContratistaId)}\" IS NULL " +
                $"OR \"{nameof(ContratoAcceso.TenantContratistaId)}\" <> \"{nameof(ContratoAcceso.TenantId)}\"");
        });
        builder.HasKey(c => c.Id);

        builder.Property(c => c.Notas).HasMaxLength(ContratoAcceso.LongitudMaximaNotas);

        // Sin índice único por (Centro, Empresa contratista): los solapes están permitidos mientras DN-6 siga abierta
        // (ver ContratoAcceso). Estos dos índices sirven a las lecturas por Centro y por Empresa contratista.
        builder.HasIndex(c => new { c.TenantId, c.CentroId });
        builder.HasIndex(c => new { c.TenantId, c.EmpresaContratistaId });

        // El Cliente empresarial titular y la Empresa contratista son las dos Empresas del mismo Tenant propietario.
        builder.HasOne<Empresa>().WithMany()
            .HasForeignKey(c => new { c.TenantId, c.ClienteEmpresarialId })
            .HasPrincipalKey(e => new { e.TenantId, e.Id })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Empresa>().WithMany()
            .HasForeignKey(c => new { c.TenantId, c.EmpresaContratistaId })
            .HasPrincipalKey(e => new { e.TenantId, e.Id })
            .OnDelete(DeleteBehavior.Restrict);

        // Compuesta con ClienteEmpresarialId, como en ProyectoConfiguration: contra la clave alternativa de Centro
        // (TenantId, Id, ClienteId), un contrato cuyo ClienteEmpresarialId no case con el ClienteId del Centro es
        // irrepresentable en la base, no solo rechazado por CrearContratoAccesoCommand.
        builder.HasOne<Centro>().WithMany()
            .HasForeignKey(c => new { c.TenantId, c.CentroId, c.ClienteEmpresarialId })
            .HasPrincipalKey(c => new { c.TenantId, c.Id, c.ClienteId })
            .OnDelete(DeleteBehavior.Restrict);

        // Las FK compuestas de las filas hijas del contrato (EvidenciasInbound) apuntan a (TenantId, Id): la clave alternativa
        // que EF crea por su HasPrincipalKey basta, sin un índice único que la repita.

        // Filtro global (soft delete + tenant) centralizado en CaeManagerDbContext.OnModelCreating.
    }
}
