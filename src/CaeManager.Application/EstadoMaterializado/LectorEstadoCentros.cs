using CaeManager.Application.EstadoMaterializado.Frescura;
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
/// crea el relleno completo, y ningún recálculo diferido sin consumir. Sin ella, <see cref="LeerAsync"/> no devuelve ningún
/// Centro y el llamador calcula como antes. Mientras la fila no exista no se recalcula el estado de ese Tenant (diseño
/// § 13.7), así que sus filas, si las hay, pueden estar viejas. Ni siquiera con la fila hay garantía total: los huecos
/// conocidos del detector (diseño § 13.7) quedan para el cotejo nocturno.</item>
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
    /// <see cref="TenantCompletoAsync"/> y, en la misma sentencia, la versión del Tenant leída con el contexto RLS de la
    /// conexión, para que <see cref="CacheEstadoCircuito"/> no pague otra. La versión es <c>null</c> si el Tenant no está
    /// completo o la conexión ve más de una fila de versión (una identidad sin RLS): entonces no se cachea nada.
    /// </summary>
    async Task<ComprobacionTenantEstado> ComprobarTenantAsync(CancellationToken cancellationToken) =>
        new(await TenantCompletoAsync(cancellationToken), null);

    /// <summary>
    /// El estado guardado de esos Centros. Los que no tienen fila fiable no aparecen en el resultado, y si el Tenant no está
    /// completo (<see cref="TenantCompletoAsync"/>) no aparece ninguno: el llamador no puede olvidarse de comprobarlo y
    /// calcula lo que falte. No aplica el alcance del usuario: el llamador pasa los Centros que ya filtró con él.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, EstadoCentroLeido>> LeerAsync(IReadOnlyCollection<Guid> centroIds, CancellationToken cancellationToken);
}

/// <summary>Si el Tenant de la conexión está completo y, si se puede cachear, la versión leída en la misma sentencia.</summary>
public readonly record struct ComprobacionTenantEstado(bool Completo, VersionEstadoLeida? Version);

/// <inheritdoc cref="ILectorEstadoCentros"/>
/// <remarks>
/// Las filas de los Centros se leen a través de <see cref="CacheEstadoCircuito"/> (diseño § 13.11): todas las del Tenant, sin
/// alcance, y el alcance lo pone quien llama con los Centros que pasa. Las guardas de fila fiable (regla vigente, día de cambio
/// aún por llegar) se aplican en cada lectura sobre lo guardado, con el día de hoy.
/// </remarks>
public sealed class LectorEstadoCentros(IEstadoMaterializadoQueryContext contexto, CacheEstadoCircuito? cache = null) : ILectorEstadoCentros
{
    private const int LoteDeCentros = 2000;

    /// <summary>Lo que se guarda de la fila de un Centro: sin filtrar por regla ni por día, que se miran al servir.</summary>
    private sealed record FilaCentro(
        Guid EntidadId, short Estado, EstadoPlataformaCentro? EstadoPlataforma, int Requeridos, int AlDia,
        DateOnly? ProximoCambioEl, int VersionCalculo);

    public async Task<bool> TenantCompletoAsync(CancellationToken cancellationToken) =>
        (await ComprobarTenantAsync(cancellationToken)).Completo;

    // Una sola sentencia (fila de versión y cola vacía): los lectores de Empresa y Trabajador la comparten y la pagan en cada
    // lectura que luego tiene que calcular. Se leen dos filas como mucho para distinguir «una» de «varias» (como
    // LectorVersionEstadoTenant): con varias, el Tenant cuenta como completo, como antes, pero no se cachea.
    public async Task<ComprobacionTenantEstado> ComprobarTenantAsync(CancellationToken cancellationToken)
    {
        var filas = await contexto.VersionesEstadoTenant
            .Where(v => !contexto.RecalculosPendientes.Any())
            .Select(v => new { v.TenantId, v.Version })
            .Take(2)
            .ToListAsync(cancellationToken);
        return new ComprobacionTenantEstado(
            filas.Count > 0, filas.Count == 1 ? new VersionEstadoLeida(filas[0].TenantId, filas[0].Version) : null);
    }

    public async Task<IReadOnlyDictionary<Guid, EstadoCentroLeido>> LeerAsync(
        IReadOnlyCollection<Guid> centroIds, CancellationToken cancellationToken)
    {
        var leidos = new Dictionary<Guid, EstadoCentroLeido>();
        if (centroIds.Count == 0)
            return leidos;
        var tenant = await ComprobarTenantAsync(cancellationToken);
        if (!tenant.Completo)
            return leidos;

        var pedidos = centroIds.ToHashSet();
        var filas = cache is null
            ? await LeerFilasAsync(pedidos, cancellationToken)
            : await cache.ObtenerAsync(Consulta(), tenant.Version, pedidos, cancellationToken);

        var hoy = DiaDeNegocio.Hoy();
        foreach (var fila in filas)
        {
            // El alcance: solo los Centros que pidió quien llama (ya filtrados con el del usuario).
            if (!pedidos.Contains(fila.EntidadId))
                continue;
            // Fila fiable: de la regla vigente y con su día de cambio aún por llegar.
            if (fila.VersionCalculo != CalculoEstadoMaterializado.VersionDeLaRegla
                || (fila.ProximoCambioEl is { } cambio && cambio <= hoy))
                continue;
            // Un ordinal que el enum no conoce no se interpreta: el Centro se calcula como antes.
            if (!Enum.IsDefined(typeof(EstadoCentro), (int)fila.Estado))
                continue;

            leidos[fila.EntidadId] = new EstadoCentroLeido(
                EstadoCentroCombinado.Derivar((EstadoCentro)fila.Estado, fila.EstadoPlataforma),
                new FraccionCumplimiento(fila.AlDia, fila.Requeridos));
        }

        return leidos;
    }

    private ConsultaEstadoEnCache<FilaCentro> Consulta() => new(
        "estados-entidad-centro",
        f => [f.EntidadId],
        async ct => await Proyectar(contexto.EstadosEntidad.Where(e => e.Ambito == AmbitoEstadoEntidad.Centro)).ToListAsync(ct),
        LeerFilasAsync);

    private async Task<IReadOnlyList<FilaCentro>> LeerFilasAsync(IReadOnlyCollection<Guid> centroIds, CancellationToken cancellationToken)
    {
        var filas = new List<FilaCentro>();
        foreach (var lote in centroIds.Distinct().Chunk(LoteDeCentros))
            filas.AddRange(await Proyectar(contexto.EstadosEntidad
                    .Where(e => e.Ambito == AmbitoEstadoEntidad.Centro && lote.Contains(e.EntidadId)))
                .ToListAsync(cancellationToken));
        return filas;
    }

    private static IQueryable<FilaCentro> Proyectar(IQueryable<EstadoEntidad> filas) =>
        filas.Select(e => new FilaCentro(
            e.EntidadId, e.Estado, e.EstadoPlataforma, e.Requeridos, e.AlDia, e.ProximoCambioEl, e.VersionCalculo));
}
