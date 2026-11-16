using CaeManager.Application.Centros.Queries.ObtenerCentrosParaSelector;
using CaeManager.Application.Inbound.CatalogoBase.Queries;
using CaeManager.Application.TiposDocumento.Queries.ObtenerTiposDocumento;
using CaeManager.Domain.Inbound.Requisitos;
using MediatR;

namespace CaeManager.Web.Features.Inbound.Plantillas.Servicios;

/// <summary>
/// Los nombres que el DTO de una plantilla no trae (solo lleva identificadores): la Actividad y el riesgo del catálogo base,
/// el Centro y el Cliente empresarial del Tenant TCE, y el tipo de documento de cada requisito. Un nombre ausente significa
/// «la Query de origen no lo devolvió al usuario»: la pantalla lo dice, no lo inventa.
/// </summary>
public sealed record ReferenciasPlantillas(
    IReadOnlyDictionary<Guid, string> Actividades,
    IReadOnlyDictionary<Guid, string> Riesgos,
    IReadOnlyDictionary<Guid, string> Centros,
    IReadOnlyDictionary<Guid, string> Clientes,
    IReadOnlyDictionary<Guid, TipoDocumentoListaDto> Tipos,
    IReadOnlySet<Guid>? TiposParaAlta = null)
{
    /// <summary>
    /// Los tipos que el dominio admite en un requisito nuevo: no archivados (<c>ParaAltaNueva</c>) y de ámbito Empresa,
    /// Trabajador o Vehículo (los de Cliente y Proyecto no se piden en un contrato de acceso). Nulo = sin dato: se ofrecen todos.
    /// </summary>
    public bool AdmiteAlta(TipoDocumentoListaDto tipo) =>
        (TiposParaAlta is null || TiposParaAlta.Contains(tipo.Id))
        && tipo.AmbitoAplicacion is Domain.Documentos.AmbitoAplicacion.Empresa or Domain.Documentos.AmbitoAplicacion.Trabajador or Domain.Documentos.AmbitoAplicacion.Vehiculo;

    public string? NombreDe(TipoPlantillaRequisitos tipo, Guid referenciaId)
    {
        var origen = tipo switch
        {
            TipoPlantillaRequisitos.PorActividad => Actividades,
            TipoPlantillaRequisitos.PorRiesgo => Riesgos,
            TipoPlantillaRequisitos.DeCentro => Centros,
            _ => Clientes,
        };
        return origen.GetValueOrDefault(referenciaId);
    }

    public IReadOnlyDictionary<Guid, string> OpcionesDe(TipoPlantillaRequisitos tipo) => tipo switch
    {
        TipoPlantillaRequisitos.PorActividad => Actividades,
        TipoPlantillaRequisitos.PorRiesgo => Riesgos,
        TipoPlantillaRequisitos.DeCentro => Centros,
        _ => Clientes,
    };
}

/// <summary>
/// Lee las referencias con las Queries que ya existen y con su autorización (<see cref="ObtenerCatalogoBaseQuery"/>,
/// <see cref="ObtenerCentrosParaSelectorQuery"/> y <see cref="ObtenerTiposDocumentoQuery"/>). No hay una lectura propia de
/// Clientes empresariales: se sacan de los Centros que el usuario ve, así que un Cliente empresarial sin Centros visibles no
/// tiene nombre (limitación declarada, no diseño).
/// </summary>
public sealed class ResolutorPlantillasRequisitos(IMediator mediator)
{
    public async Task<ReferenciasPlantillas> CargarAsync(CancellationToken cancellationToken = default)
    {
        // Una a una y sin paralelismo: las Queries comparten el contexto de datos del ámbito.
        var catalogo = await mediator.Send(new ObtenerCatalogoBaseQuery(), cancellationToken);
        var centros = await mediator.Send(new ObtenerCentrosParaSelectorQuery(), cancellationToken);
        var tipos = await mediator.Send(new ObtenerTiposDocumentoQuery(), cancellationToken);
        var tiposParaAlta = await mediator.Send(new ObtenerTiposDocumentoQuery(ParaAltaNueva: true), cancellationToken);

        var clientes = new Dictionary<Guid, string>();
        foreach (var centro in centros.Where(c => c.ClienteEmpresarialId != Guid.Empty))
            clientes.TryAdd(centro.ClienteEmpresarialId, centro.ClienteRazonSocial);

        return new ReferenciasPlantillas(
            catalogo.Actividades.ToDictionary(a => a.Id, a => a.Nombre),
            catalogo.Riesgos.ToDictionary(r => r.Id, r => r.Nombre),
            centros.GroupBy(c => c.Id).ToDictionary(g => g.Key, g => g.First().Nombre),
            clientes,
            tipos.GroupBy(t => t.Id).ToDictionary(g => g.Key, g => g.First()),
            tiposParaAlta.Select(t => t.Id).ToHashSet());
    }
}

/// <summary>Contratos de acceso que usan una plantilla («Usada en N contratos de acceso»). Hoy sin backend: B-15.</summary>
public interface IUsoDePlantillasRequisitos
{
    /// <summary>Un contrato ausente del resultado significa «sin dato», no cero.</summary>
    Task<IReadOnlyDictionary<Guid, int>> ContratosPorPlantillaAsync(
        IReadOnlyCollection<Guid> plantillaIds, CancellationToken cancellationToken = default);
}

public sealed class UsoDePlantillasRequisitosNoDisponible : IUsoDePlantillasRequisitos
{
    // shortcut: sin backend, cablear cuando entre B-15 (resolución por capas: qué plantillas aplican a cada contrato de acceso).
    public Task<IReadOnlyDictionary<Guid, int>> ContratosPorPlantillaAsync(
        IReadOnlyCollection<Guid> plantillaIds, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyDictionary<Guid, int>>(new Dictionary<Guid, int>());
}
