using CaeManager.Application.Clientes.Queries.ObtenerClientesParaSelector;
using CaeManager.Infrastructure.Autorizacion;
using CaeManager.Infrastructure.Identity;
using MediatR;

namespace CaeManager.Web.Features.Integraciones;

/// <summary>Una Empresa sin rol CAE (rótulo legado de la Empresa cliente, deuda terminológica E29) que se puede elegir como
/// destino de un buzón de la organización.</summary>
public sealed record EmpresaParaElegir(Guid Id, string RazonSocial);

/// <summary>Las dos listas entre las que se elige el propietario de un buzón que se va a conectar.</summary>
public sealed record OpcionesPropietarioBuzon(IReadOnlyList<EmpresaParaElegir> Empresas, IReadOnlyList<GestorParaElegir> Gestores)
{
    public static readonly OpcionesPropietarioBuzon Vacias = new([], []);

    /// <summary>
    /// Carga las Empresas visibles y los Gestores CAE del Tenant activo. Es el único punto de Integraciones que lee la lista de
    /// Empresas de Outbound (arista Núcleo → Outbound congelada en <c>Modulos-Nucleo-a-Outbound.txt</c>): Conexiones y el cajón
    /// «Añadir buzón» de Buzones la comparten en vez de repetirla.
    /// </summary>
    public static async Task<OpcionesPropietarioBuzon> CargarAsync(
        IMediator mediator, DirectorioUsuariosTenant directorio, CancellationToken cancellationToken)
    {
        var empresas = await mediator.Send(new ObtenerClientesParaSelectorQuery(), cancellationToken);
        var gestores = await directorio.ObtenerVisiblesEnRolAsync(Roles.GestorCae, cancellationToken);
        return new OpcionesPropietarioBuzon(
            empresas.Select(e => new EmpresaParaElegir(e.Id, e.RazonSocial)).ToList(),
            gestores.Select(u => new GestorParaElegir(u.Id, u.NombreCompleto)).ToList());
    }
}
