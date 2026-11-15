using CaeManager.Domain.Centros;
using CaeManager.Domain.Inbound.ContratosAcceso;
using CaeManager.Domain.Empresas;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Inbound.Persistence;

public class ContratoAccesoConfiguration : IEntityTypeConfiguration<ContratoAcceso>
{
    public void Configure(EntityTypeBuilder<ContratoAcceso> builder)
    {
        // Respaldo en la base de las invariantes de ContratoAcceso.Crear/Actualizar/Finalizar y del validador del alta:
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
            // B-05: los límites de CondicionesContratoAcceso y el estado Finalizado con su cierre real.
            t.HasCheckConstraint(
                "CK_ContratosAcceso_NivelMaxSubcontrata",
                $"\"{nameof(ContratoAcceso.NivelMaxSubcontrata)}\" IS NULL " +
                $"OR \"{nameof(ContratoAcceso.NivelMaxSubcontrata)}\" BETWEEN 0 AND {CondicionesContratoAcceso.NivelSubcontrataMaximo}");
            t.HasCheckConstraint(
                "CK_ContratosAcceso_CuposNoNegativos",
                $"(\"{nameof(ContratoAcceso.CupoTrabajadores)}\" IS NULL OR \"{nameof(ContratoAcceso.CupoTrabajadores)}\" >= 0) " +
                $"AND (\"{nameof(ContratoAcceso.CupoSubcontratas)}\" IS NULL OR \"{nameof(ContratoAcceso.CupoSubcontratas)}\" >= 0)");
            // El motivo, si lo hay, no está en blanco: el dominio recorta y trata uno en blanco como ausente.
            t.HasCheckConstraint(
                "CK_ContratosAcceso_MotivoSiHayCupo",
                $"(\"{nameof(ContratoAcceso.CupoTrabajadores)}\" IS NULL AND \"{nameof(ContratoAcceso.CupoSubcontratas)}\" IS NULL) " +
                $"= (\"{nameof(ContratoAcceso.MotivoCupo)}\" IS NULL) " +
                $"AND (\"{nameof(ContratoAcceso.MotivoCupo)}\" IS NULL OR \"{nameof(ContratoAcceso.MotivoCupo)}\" ~ '[^[:space:]]')");
            // Un Finalizado tiene siempre su cierre real. Es lo que hace fallar, en la ventana del relevo azul/verde, un
            // Reabrir de la ranura anterior sobre un contrato que la nueva ya finalizó (D-21 retira Reabrir).
            t.HasCheckConstraint(
                "CK_ContratosAcceso_FinalizadoConCierre",
                $"\"{nameof(ContratoAcceso.Estado)}\" <> 'Finalizado' OR \"{nameof(ContratoAcceso.FechaCierreReal)}\" IS NOT NULL");
            // Los enums se guardan como texto: solo sus nombres (un NULL de Tipo o PropiaActividad pasa el CHECK).
            t.HasCheckConstraint("CK_ContratosAcceso_Estado", EnValoresDe<EstadoContratoAcceso>(nameof(ContratoAcceso.Estado)));
            t.HasCheckConstraint("CK_ContratosAcceso_Tipo", EnValoresDe<TipoContratoAcceso>(nameof(ContratoAcceso.Tipo)));
            t.HasCheckConstraint(
                "CK_ContratosAcceso_PropiaActividad", EnValoresDe<PropiaActividadContrato>(nameof(ContratoAcceso.PropiaActividad)));
        });
        builder.HasKey(c => c.Id);

        builder.Property(c => c.Notas).HasMaxLength(ContratoAcceso.LongitudMaximaNotas);

        // Texto, como el estado de EvidenciasInbound. Sin valor por defecto en el modelo, a propósito: EF manda siempre el
        // estado. El DEFAULT 'Activo' de la columna lo pone la migración solo para las altas de la ranura anterior, que no
        // conocen la columna y daban de alta contratos abiertos.
        builder.Property(c => c.Estado).HasConversion<string>().HasMaxLength(30);
        builder.Property(c => c.Tipo).HasConversion<string>().HasMaxLength(30);
        builder.Property(c => c.PropiaActividad).HasConversion<string>().HasMaxLength(30);
        builder.Property(c => c.MotivoCupo).HasMaxLength(CondicionesContratoAcceso.LongitudMaximaMotivoCupo);
        builder.Ignore(c => c.Condiciones);

        builder.HasMany(c => c.HistorialVigencia)
            .WithOne()
            .HasForeignKey(h => new { h.TenantId, h.ContratoAccesoId })
            .HasPrincipalKey(c => new { c.TenantId, c.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.Navigation(c => c.HistorialVigencia).UsePropertyAccessMode(PropertyAccessMode.Field);

        // Sin índice único por (Centro, Empresa contratista): los contratos solapados no se prohíben (D-20, DECISION_LOG
        // 2026-11-14). Estos dos índices sirven a las lecturas por Centro y por Empresa contratista.
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

    /// <summary>
    /// CHECK con los nombres del enum, que es como se guarda (<c>HasConversion&lt;string&gt;</c>): un valor nuevo del enum
    /// cambia el modelo y obliga a una migración, en vez de fallar solo al insertar. El mismo criterio que
    /// <c>SolicitudAltaConfiguration.EnValoresDe</c>, sin cruzar la frontera entre persistencias de módulo.
    /// </summary>
    internal static string EnValoresDe<TEnum>(string columna) where TEnum : struct, Enum =>
        $"\"{columna}\" IN ({string.Join(", ", Enum.GetNames<TEnum>().Select(n => $"'{n}'"))})";
}
