using CaeManager.Application.Common;
using CaeManager.Domain.Inbound.Requisitos;

namespace CaeManager.Application.Inbound.Requisitos;

/// <summary>
/// Autoridad sobre una plantilla de requisitos del Tenant TCE (B-14), en el plano de Operación. La Propiedad la ponen el
/// filtro de Tenant y la RLS; esto decide qué Gestor CAE del Tenant la ve o la cambia, según de qué cuelga:
///
/// <list type="bullet">
/// <item><b>De Centro:</b> el Centro tiene que estar en su cartera de gestión (como <c>CrearTipoDocumentoCommand</c>, que
/// impone documentación a un Centro).</item>
/// <item><b>Exigencias de un Cliente empresarial:</b> el Cliente empresarial tiene que estar en su cartera visible y quien
/// escribe, en el lado de gestión (como <c>EstablecerToleranciaClienteEmpresarialCommand</c>).</item>
/// <item><b>Por Actividad o por riesgo:</b> la plantilla vale para todo el Tenant TCE, así que cambiarla pide alcanzar el
/// Tenant entero (acceso total o Asignación de Cartera universal): una cartera acotada no puede recortar a qué Centros
/// llega, y no debe imponer requisitos a los Centros de otro Gestor CAE.</item>
/// </list>
///
/// <para>Lecturas y comandos lo comparten: lo que no se ve tampoco se cambia (IDOR de escritura).</para>
/// </summary>
public static class PlantillaRequisitosAutorizacion
{
    public static Task<bool> PuedeEscribirAsync(PlantillaRequisitos plantilla, IAlcanceDatosService alcance, CancellationToken cancellationToken) =>
        PuedeEscribirAsync(plantilla.Tipo, ReferenciaDe(plantilla), alcance, cancellationToken);

    public static async Task<bool> PuedeEscribirAsync(
        TipoPlantillaRequisitos tipo, Guid referenciaId, IAlcanceDatosService alcance, CancellationToken cancellationToken)
    {
        switch (tipo)
        {
            case TipoPlantillaRequisitos.DeCentro:
                var centros = await alcance.ObtenerCentroIdsParaGestionAsync(cancellationToken);
                return centros is null || centros.Contains(referenciaId);
            case TipoPlantillaRequisitos.ExigenciasClienteEmpresarial:
                if (!await alcance.OperaDesdeElLadoDeGestionAsync(cancellationToken))
                    return false;
                var clientes = await alcance.ObtenerClienteIdsVisiblesAsync(cancellationToken);
                return clientes is null || clientes.Contains(referenciaId);
            case TipoPlantillaRequisitos.PorActividad:
            case TipoPlantillaRequisitos.PorRiesgo:
                return await alcance.AlcanzaElTenantEnteroAsync(cancellationToken);
            default:
                return false;
        }
    }

    /// <summary>
    /// Quién la ve: el lado de gestión del Tenant TCE, nunca el usuario de portal; y, si cuelga de un Centro o de un Cliente
    /// empresarial, solo con ese Centro o Cliente empresarial en su cartera visible.
    /// </summary>
    public static async Task<FiltroVisibilidad?> FiltroDeLecturaAsync(IAlcanceDatosService alcance, CancellationToken cancellationToken)
    {
        if (!await alcance.OperaDesdeElLadoDeGestionAsync(cancellationToken))
            return null;
        return new FiltroVisibilidad(
            await alcance.ObtenerCentroIdsVisiblesAsync(cancellationToken),
            await alcance.ObtenerClienteIdsVisiblesAsync(cancellationToken));
    }

    /// <summary>
    /// Restringe una consulta de plantillas a las visibles. Las de Actividad y riesgo las ve todo el lado de gestión: dicen
    /// qué pide el Tenant TCE, no datos de una cartera.
    /// </summary>
    public static IQueryable<PlantillaRequisitos> AplicarA(IQueryable<PlantillaRequisitos> plantillas, FiltroVisibilidad filtro)
    {
        if (filtro.CentroIds is { } centros)
            plantillas = plantillas.Where(p => p.CentroId == null || centros.Contains(p.CentroId.Value));
        if (filtro.ClienteEmpresarialIds is { } clientes)
            plantillas = plantillas.Where(p => p.ClienteEmpresarialId == null || clientes.Contains(p.ClienteEmpresarialId.Value));
        return plantillas;
    }

    internal static Guid ReferenciaDe(PlantillaRequisitos plantilla) =>
        plantilla.ActividadId ?? plantilla.RiesgoId ?? plantilla.CentroId ?? plantilla.ClienteEmpresarialId
        ?? throw new InvalidOperationException("Plantilla sin referencia.");

    /// <summary>Listas de cartera visibles; nulo es «sin restricción».</summary>
    public sealed record FiltroVisibilidad(IReadOnlyList<Guid>? CentroIds, IReadOnlyList<Guid>? ClienteEmpresarialIds);
}
