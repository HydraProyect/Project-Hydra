using CaeManager.Application.Common;
using CaeManager.Application.Tenants.Queries.ObtenerClientesAutorizados;
using MediatR;

namespace CaeManager.Web.Components.Layout;

/// <summary>
/// Resultado de resolver la empresa gestionada activa de una pantalla de un Tenant beneficiario a la vez.
/// <see cref="Activa"/> solo se rellena si el selector de la barra lateral es visible (mismo criterio que el
/// selector); <see cref="SinSeleccion"/> es el estado 4a del mockup: hay que elegir una empresa de la cartera
/// antes de ver la lista. Ninguno de los dos autoriza nada: el alcance lo siguen decidiendo la consulta y RLS.
/// </summary>
public sealed record ContextoEmpresaActiva(ClienteAutorizadoDto? Activa, bool SinSeleccion)
{
    public static readonly ContextoEmpresaActiva Ninguno = new(null, false);

    /// <summary>
    /// Resuelve el contexto con la lista autorizada y el Tenant efectivo. Las pantallas no montan su lista ni
    /// sus acciones hasta que esto termina (<c>_resolviendoEmpresa</c>), para no lanzar cargas del Tenant de
    /// origen mientras la consulta está en vuelo.
    /// </summary>
    public static async Task<ContextoEmpresaActiva> ResolverAsync(
        IMediator mediator, ITenantActual tenantActual, CancellationToken ct = default)
    {
        IReadOnlyList<ClienteAutorizadoDto> autorizados;
        try
        {
            autorizados = await mediator.Send(new ObtenerClientesAutorizadosQuery(), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // La página se retiró mientras se resolvía: salida normal. Quien llama comprueba su token
            // antes de seguir cargando datos.
            return Ninguno;
        }

        var activa = ClientesAutorizados.Activo(autorizados, tenantActual.TenantId);
        if (!ClientesAutorizados.SelectorVisible(autorizados, activa))
            return Ninguno;

        // En el estado 4a (hay que elegir empresa) no hay empresa activa: si no, la cabecera enseñaría el
        // Tenant de origen como si fuera la empresa elegida, encima del «Selecciona una empresa».
        return ClientesAutorizados.SinEmpresaSeleccionada(autorizados, activa)
            ? new ContextoEmpresaActiva(null, true)
            : new ContextoEmpresaActiva(activa, false);
    }
}
