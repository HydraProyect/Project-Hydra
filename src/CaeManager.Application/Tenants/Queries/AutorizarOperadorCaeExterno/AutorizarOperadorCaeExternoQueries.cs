using CaeManager.Application.Common;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Tenants.Queries.AutorizarOperadorCaeExterno;

/// <summary>
/// El Tenant propietario que la persona actual puede autorizar a un Operador CAE
/// externo, o <c>null</c> si no es Administrador de ninguno. Alimenta la visibilidad
/// del botón «Autorizar un Operador CAE externo» de <c>/delegaciones</c> (incremento 1b)
/// y el <c>TenantClienteId</c> que la pantalla manda al comando.
///
/// <para>
/// El Tenant es el <b>de origen</b> de la cuenta
/// (<see cref="ICurrentUserService.ObtenerTenantOrigenIdAsync"/>), nunca el Context
/// Workspace ni <c>ITenantActual</c>: quien opera el workspace de otro Tenant no
/// adquiere autoridad sobre sus vínculos. Y la respuesta la da exactamente el mismo
/// predicado que el comando (<see cref="IAutorizacionDelegacionTenant"/>, resuelto
/// contra la base): la pantalla no puede ofrecer lo que el comando rechazaría.
/// </para>
/// </summary>
public record ObtenerTenantPropietarioAutorizanteQuery : IRequest<Guid?>;

/// <summary>
/// Resuelve UN Operador CAE externo que el Administrador del Tenant propietario puede
/// autorizar: por su Id (el enlace de preselección del Actor de Plataforma TALVEG) o
/// por su nombre exacto (el buscador del modal). Nunca devuelve una lista.
///
/// <para>
/// <b>Por qué uno y no un catálogo.</b> <c>Tenant</c> es catálogo global sin
/// filtro de tenant ni RLS de fila: un listado abierto enseñaría a cualquier
/// Administrador qué organizaciones son clientes comerciales de TALVEG como
/// Operador CAE externo. Pedir el nombre exacto o traer el enlace exige saber ya
/// a quién se busca. Proyecta solo Id y nombre: nada de a qué Tenants opera cada
/// candidato ni de sus delegaciones.
/// </para>
///
/// <para>
/// La preselección no escribe nada: este resultado es una sugerencia que la
/// pantalla muestra; la única escritura es el <c>CrearDelegacionTenantCommand</c>
/// que dispara el clic del Administrador, y ese comando vuelve a validarlo todo.
/// </para>
/// </summary>
public record BuscarOperadorCaeExternoAutorizableQuery(Guid? OperadorId, string? NombreExacto)
    : IRequest<OperadorCaeExternoAutorizableDto?>;

public record OperadorCaeExternoAutorizableDto(Guid TenantId, string Nombre);

public class AutorizarOperadorCaeExternoQueriesHandler(
    ITenantsQueryContext tenantsContext,
    IAutorizacionDelegacionTenant autorizacion,
    ICurrentUserService currentUserService)
    : IRequestHandler<ObtenerTenantPropietarioAutorizanteQuery, Guid?>,
      IRequestHandler<BuscarOperadorCaeExternoAutorizableQuery, OperadorCaeExternoAutorizableDto?>
{
    public Task<Guid?> Handle(ObtenerTenantPropietarioAutorizanteQuery request, CancellationToken cancellationToken) =>
        TenantPropietarioAutorizanteAsync(cancellationToken);

    public async Task<OperadorCaeExternoAutorizableDto?> Handle(
        BuscarOperadorCaeExternoAutorizableQuery request, CancellationToken cancellationToken)
    {
        // La autoridad primero, antes de tocar el catálogo global: quien no es
        // Administrador de su Tenant no puede usar esta consulta para averiguar
        // qué organizaciones existen.
        var tenantPropietarioId = await TenantPropietarioAutorizanteAsync(cancellationToken);
        if (tenantPropietarioId is null) return null;

        var candidatos = tenantsContext.Tenants
            .Where(OperadorCaeExternoElegible.Predicado)
            .Where(t => t.Id != tenantPropietarioId.Value);

        if (request.OperadorId is { } operadorId)
        {
            candidatos = candidatos.Where(t => t.Id == operadorId);
        }
        else
        {
            var nombre = request.NombreExacto?.Trim();
            if (string.IsNullOrEmpty(nombre)) return null;

            var nombreNormalizado = nombre.ToLower();
            candidatos = candidatos.Where(t => t.Nombre.ToLower() == nombreNormalizado);
        }

        return await candidatos
            .OrderBy(t => t.Nombre).ThenBy(t => t.Id)
            .Select(t => new OperadorCaeExternoAutorizableDto(t.Id, t.Nombre))
            .FirstOrDefaultAsync(cancellationToken);
    }

    private async Task<Guid?> TenantPropietarioAutorizanteAsync(CancellationToken cancellationToken)
    {
        var usuarioId = await currentUserService.ObtenerUsuarioActualIdAsync();
        var tenantOrigenId = await currentUserService.ObtenerTenantOrigenIdAsync();
        if (usuarioId is null || tenantOrigenId is null) return null;

        return await autorizacion.PuedeGestionarDelegacionesAsync(usuarioId.Value, tenantOrigenId.Value, cancellationToken)
            ? tenantOrigenId
            : null;
    }
}
