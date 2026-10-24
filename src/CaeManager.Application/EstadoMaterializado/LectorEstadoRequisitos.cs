using CaeManager.Application.Centros;
using CaeManager.Application.Empresas;
using CaeManager.Application.TiposDocumento;
using CaeManager.Application.Trabajadores;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.EstadoMaterializado;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.EstadoMaterializado;

/// <summary>
/// Un documento obligatorio que le falta a un Trabajador en un Centro (Tipo exigido sin ningún Documento operativo), tal como
/// lo pinta Alertas y, con ella, Mi trabajo, la bandeja e Inicio, leído de <c>EstadosRequisito</c>.
/// </summary>
/// <param name="EmpresaId">La Empresa del Trabajador (<c>Trabajador.EmpresaId</c>); <c>null</c> en un Trabajador de subcontratista.</param>
public sealed record FaltanteLeido(
    Guid TrabajadorId, string TrabajadorNombre, Guid? EmpresaId, Guid CentroId, string CentroNombre,
    Guid TipoDocumentoId, string TipoDocumentoNombre, Guid? ClienteEmpresarialId, string? ClienteNombre);

/// <summary>
/// Lector de <c>EstadosRequisito</c> para las colas de trabajo (PR 9 del plan del estado materializado): Alertas, y con ellas
/// Mi trabajo, la bandeja e Inicio. Sirve lo mismo que el cálculo en lectura de siempre, o dice que no puede (<c>null</c>) y el
/// llamador calcula: cada «no puedo» deja al llamador en su camino de cálculo, que sigue vivo y es el de referencia.
///
/// <para>
/// <b>Qué lee.</b> Los documentos obligatorios que faltan (par de Trabajador en <c>Faltante</c>): la misma regla de
/// <c>IDocumentosFaltantesService</c> y de los requisitos de eventos, que el recálculo ya aplicó al escribir los pares. El
/// alcance del usuario se aplica al leer, filtrando filas (un par solo depende de su Trabajador, su Centro y su Tipo, así que
/// filtrar por los Trabajadores y Centros visibles es exacto: no hace falta alcanzar el Tenant entero).
/// </para>
///
/// <para>
/// <b>Qué no lee.</b> Las alertas de vigencia (documentos que vencen: no son solo los exigidos por un Centro), los requisitos
/// bloqueantes de la bandeja (necesitan la tolerancia del Centro y el estado por canal de la Plataforma CAE, que los pares no
/// guardan) y «qué rechazo cierra su Centro» (es por documento y no por Centro). Siguen calculándose.
/// </para>
///
/// <para>
/// <b>Cuándo lee.</b> Solo en un Tenant completo (<see cref="ILectorEstadoCentros.TenantCompletoAsync"/>: fila de versión y cola
/// de recálculos vacía; la misma guarda que los lectores de Centro, Empresa y Trabajador, no una copia) y si ningún par leído es
/// de otra regla (<c>VersionCalculo</c>) ni tiene su <c>ProximoCambioEl</c> ya llegado (el estado cambia por el paso del tiempo y
/// nadie lo ha recalculado todavía; hasta la pasada de medianoche es lo normal tras cada madrugada).
/// </para>
/// </summary>
public interface ILectorEstadoRequisitos
{
    /// <summary>
    /// Los documentos obligatorios que faltan a los Trabajadores visibles en los Centros visibles (<c>null</c> = sin restricción,
    /// como en Alertas). <c>null</c> si el lector no puede servirlos.
    /// </summary>
    Task<IReadOnlyList<FaltanteLeido>?> FaltantesDeTrabajadoresAsync(
        IReadOnlyList<Guid>? trabajadorIdsVisibles, IReadOnlyList<Guid>? centroIdsVisibles, CancellationToken cancellationToken);
}

/// <summary>El lector que nunca lee: todo se calcula en lectura. Para quien construye un manejador sin inyección.</summary>
public sealed class SinLectorDeEstadoRequisitos : ILectorEstadoRequisitos
{
    public static SinLectorDeEstadoRequisitos Instancia { get; } = new();

    private SinLectorDeEstadoRequisitos()
    {
    }

    public Task<IReadOnlyList<FaltanteLeido>?> FaltantesDeTrabajadoresAsync(
        IReadOnlyList<Guid>? trabajadorIdsVisibles, IReadOnlyList<Guid>? centroIdsVisibles, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<FaltanteLeido>?>(null);
}

/// <inheritdoc cref="ILectorEstadoRequisitos"/>
public sealed class LectorEstadoRequisitos(
    IEstadoMaterializadoQueryContext tablas,
    ILectorEstadoCentros lectorCentros,
    ITrabajadoresQueryContext trabajadoresContext,
    ICentrosQueryContext centrosContext,
    ITiposDocumentoQueryContext tiposDocumentoContext,
    IEmpresasQueryContext empresasContext)
    : ILectorEstadoRequisitos
{
    private sealed record FilaLeida(
        Guid TrabajadorId, string TrabajadorNombre, Guid? EmpresaId, Guid CentroId, string CentroNombre,
        Guid TipoDocumentoId, string TipoDocumentoNombre, Guid? ClienteEmpresarialId, string? ClienteNombre,
        int VersionCalculo, DateOnly? ProximoCambioEl);

    public async Task<IReadOnlyList<FaltanteLeido>?> FaltantesDeTrabajadoresAsync(
        IReadOnlyList<Guid>? trabajadorIdsVisibles, IReadOnlyList<Guid>? centroIdsVisibles, CancellationToken cancellationToken)
    {
        // Un alcance vacío no ve nada, con el Tenant como esté: no hace falta ninguna lectura para decirlo.
        if (trabajadorIdsVisibles is { Count: 0 } || centroIdsVisibles is { Count: 0 })
            return [];

        // La misma guarda que los demás lectores: Tenant completo y sin recálculos pendientes.
        if (!await lectorCentros.TenantCompletoAsync(cancellationToken))
            return null;

        var pares = tablas.EstadosRequisito
            .Where(p => p.AmbitoTitular == AmbitoAplicacion.Trabajador && p.EstadoTalveg == EstadoTalvegRequisito.Faltante);
        if (trabajadorIdsVisibles is not null)
            pares = pares.Where(p => trabajadorIdsVisibles.Contains(p.TitularId));
        if (centroIdsVisibles is not null)
            pares = pares.Where(p => centroIdsVisibles.Contains(p.CentroId));

        // Los nombres en la misma sentencia. Trabajador, Centro y Tipo con unión interna (el filtro global quita los borrados,
        // como en el cálculo de siempre); el Cliente empresarial del Centro con unión externa (sin él, la fila sale sin Cliente).
        var filas = await (
            from p in pares
            join t in trabajadoresContext.Trabajadores on p.TitularId equals t.Id
            join c in centrosContext.Centros on p.CentroId equals c.Id
            join tipo in tiposDocumentoContext.TiposDocumento on p.TipoDocumentoId equals tipo.Id
            join cl in empresasContext.Empresas on c.ClienteId equals cl.Id into clientes
            from cliente in clientes.DefaultIfEmpty()
            select new FilaLeida(
                t.Id, t.Nombre + " " + t.Apellidos, t.EmpresaId, c.Id, c.Nombre, tipo.Id, tipo.Nombre,
                cliente == null ? (Guid?)null : cliente.Id, cliente == null ? null : cliente.RazonSocial,
                p.VersionCalculo, p.ProximoCambioEl))
            .ToListAsync(cancellationToken);

        // Fila fiable: de la regla vigente y con su día de cambio por llegar. Una sola que no lo sea manda al cálculo de siempre.
        var hoy = DiaDeNegocio.Hoy();
        if (filas.Any(f => f.VersionCalculo != CalculoEstadoMaterializado.VersionDeLaRegla
                           || (f.ProximoCambioEl is { } cambio && cambio <= hoy)))
            return null;

        return filas
            .OrderBy(f => f.TrabajadorNombre, StringComparer.Ordinal)
            .ThenBy(f => f.CentroNombre, StringComparer.Ordinal)
            .ThenBy(f => f.TipoDocumentoNombre, StringComparer.Ordinal)
            .Select(f => new FaltanteLeido(
                f.TrabajadorId, f.TrabajadorNombre, f.EmpresaId, f.CentroId, f.CentroNombre,
                f.TipoDocumentoId, f.TipoDocumentoNombre, f.ClienteEmpresarialId, f.ClienteNombre))
            .ToList();
    }
}
