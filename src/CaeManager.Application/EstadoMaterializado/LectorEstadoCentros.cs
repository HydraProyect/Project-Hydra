using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.EstadoMaterializado;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.EstadoMaterializado;

/// <summary>
/// Lo que el listado de Centros lee de <c>EstadosEntidad</c> para un Centro: el rótulo combinado de hoy (derivado de las dos
/// columnas, <see cref="EstadoCentroCombinado.Derivar"/>) y la fracción de cumplimiento de su contexto.
/// </summary>
public sealed record EstadoCentroLeido(EstadoCentro Estado, FraccionCumplimiento Cumplimiento);

/// <summary>
/// Lector del estado materializado de los Centros (diseño del estado materializado, PR 6). Lo que devuelve es lo mismo que
/// calcularía <c>ICalculoEstadoCentroService</c>, ya guardado; y solo lo devuelve cuando es fiable:
/// <list type="bullet">
/// <item><b>Tenant completo</b> (<see cref="TenantCompletoAsync"/>): tiene su fila en <c>VersionesEstadoTenant</c>, que solo
/// crea el relleno completo, y ningún recálculo diferido sin consumir. Sin ella, ningún Centro del Tenant se lee de las
/// tablas: el llamador calcula como antes. Mientras la fila no exista no se recalcula el estado de ese Tenant (diseño § 13.7),
/// así que sus filas, si las hay, pueden estar viejas.</item>
/// <item><b>Fila fiable</b> (<see cref="LeerAsync"/>): calculada con la versión vigente de la regla y con su
/// <c>ProximoCambioEl</c> aún por llegar. Una fila cuyo día de cambio ya pasó (la pasada de medianoche aún no la rehízo) o de
/// una regla anterior no se devuelve, y el llamador calcula ese Centro como antes.</item>
/// </list>
/// Todo lo que este lector no devuelve se calcula en línea: nunca se sirve un estado que no se sepa al día.
/// </summary>
public interface ILectorEstadoCentros
{
    /// <summary>Si el estado materializado del Tenant de la conexión está entero y sin recálculos pendientes.</summary>
    Task<bool> TenantCompletoAsync(CancellationToken cancellationToken);

    /// <summary>
    /// El estado guardado de esos Centros. Los que no tienen fila fiable no aparecen en el resultado. No comprueba
    /// <see cref="TenantCompletoAsync"/>: es cosa del llamador. No aplica el alcance del usuario: el llamador pasa los Centros
    /// que ya filtró con él.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, EstadoCentroLeido>> LeerAsync(IReadOnlyCollection<Guid> centroIds, CancellationToken cancellationToken);
}

/// <inheritdoc cref="ILectorEstadoCentros"/>
public sealed class LectorEstadoCentros(IEstadoMaterializadoQueryContext contexto) : ILectorEstadoCentros
{
    private const int LoteDeCentros = 2000;

    public async Task<bool> TenantCompletoAsync(CancellationToken cancellationToken) =>
        await contexto.VersionesEstadoTenant.AnyAsync(cancellationToken)
        && !await contexto.RecalculosPendientes.AnyAsync(cancellationToken);

    public async Task<IReadOnlyDictionary<Guid, EstadoCentroLeido>> LeerAsync(
        IReadOnlyCollection<Guid> centroIds, CancellationToken cancellationToken)
    {
        var leidos = new Dictionary<Guid, EstadoCentroLeido>();
        if (centroIds.Count == 0)
            return leidos;

        var hoy = DiaDeNegocio.Hoy();
        const int versionDeLaRegla = CalculoEstadoMaterializado.VersionDeLaRegla;
        foreach (var lote in centroIds.Distinct().Chunk(LoteDeCentros))
        {
            var filas = await contexto.EstadosEntidad
                .Where(e => e.Ambito == AmbitoEstadoEntidad.Centro
                    && lote.Contains(e.EntidadId)
                    && e.VersionCalculo == versionDeLaRegla
                    && (e.ProximoCambioEl == null || e.ProximoCambioEl > hoy))
                .Select(e => new { e.EntidadId, e.Estado, e.EstadoPlataforma, e.Requeridos, e.AlDia })
                .ToListAsync(cancellationToken);

            foreach (var fila in filas)
            {
                // Un ordinal que el enum no conoce no se interpreta: el Centro se calcula como antes.
                if (!Enum.IsDefined(typeof(EstadoCentro), (int)fila.Estado))
                    continue;

                leidos[fila.EntidadId] = new EstadoCentroLeido(
                    EstadoCentroCombinado.Derivar((EstadoCentro)fila.Estado, fila.EstadoPlataforma),
                    new FraccionCumplimiento(fila.AlDia, fila.Requeridos));
            }
        }

        return leidos;
    }
}
