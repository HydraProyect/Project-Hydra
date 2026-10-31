using CaeManager.Application.Tenants;
using CaeManager.Application.Tenants.Queries.ObtenerClientesAutorizados;
using CaeManager.Infrastructure.Identity;
using MediatR;

namespace CaeManager.Web.Features.Extension;

/// <summary>
/// Los Tenants que la extensión ofrece en su selector: el de origen y los beneficiarios con Asignación de Operación y
/// Asignación de Cartera vigentes. No hay predicado propio: es la lista del selector web
/// (<see cref="ObtenerClientesAutorizadosQuery"/>, sin su memoria breve) sin la vía heredada de soporte, que la
/// extensión no alcanza. Listar no autoriza nada: cada petición posterior vuelve a resolverse en
/// <c>ContextoExtensionMiddleware</c>.
/// </summary>
public static class TenantsBeneficiariosExtensionEndpoints
{
    public static IEndpointRouteBuilder MapTenantsBeneficiariosExtensionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/extension/tenants-beneficiarios", ListarAsync)
            .RequireAuthorization(Policies.SoloExtension);

        return endpoints;
    }

    public static async Task<IResult> ListarAsync(IMediator mediator, CancellationToken cancellationToken)
    {
        var autorizados = await mediator.Send(new ObtenerClientesAutorizadosQuery { LecturaVigente = true }, cancellationToken);

        return Results.Ok(new
        {
            tenants = Ofrecidos(autorizados).Select(t => new { t.TenantId, t.Nombre, t.EsOrigen }),
            porDefecto = ClientesAutorizados.TenantPorDefecto(autorizados)?.TenantId,
        });
    }

    /// <summary>Lo que la extensión puede solicitar: el Tenant de origen y lo gestionado por Asignación de Operación.</summary>
    public static IEnumerable<ClienteAutorizadoDto> Ofrecidos(IEnumerable<ClienteAutorizadoDto> autorizados) =>
        autorizados.Where(t => t.EsOrigen || t.EsGestionadoPorOperacion);
}
