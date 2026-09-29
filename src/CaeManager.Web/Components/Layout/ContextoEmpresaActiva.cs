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
        var autorizados = await mediator.Send(new ObtenerClientesAutorizadosQuery(), ct);
        var activa = ClientesAutorizados.Activo(autorizados, tenantActual.TenantId);
        return ClientesAutorizados.SelectorVisible(autorizados, activa)
            ? new ContextoEmpresaActiva(activa, ClientesAutorizados.SinEmpresaSeleccionada(autorizados, activa))
            : Ninguno;
    }
}
