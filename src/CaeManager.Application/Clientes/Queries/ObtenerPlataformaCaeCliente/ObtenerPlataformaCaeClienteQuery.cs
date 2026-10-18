using CaeManager.Application.Common;
using CaeManager.Application.Empresas;
using CaeManager.Application.Plataforma;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Clientes.Queries.ObtenerPlataformaCaeCliente;

/// <summary>
/// La parte no secreta de la caja «Plataforma CAE» de Cliente 360: la plataforma del Cliente
/// empresarial, la dirección de su portal y el código de cliente. No lee ninguna credencial:
/// los accesos de cada Empresa van por <c>ObtenerAccesosPlataformaCaeClienteQuery</c>, marcada.
///
/// <para>
/// <c>null</c> = la caja no se pinta: Cliente empresarial fuera de la cartera, o Usuario de
/// Cliente (rol Cliente), que abre la ficha de su propio Cliente empresarial pero para quien
/// la plataforma con la que se le acredita es un artefacto interno de la gestión. El rol
/// Consulta sí la recibe (decisión del 17-10: «Consulta: solo plataforma y Abrir portal»).
/// </para>
///
/// <para>
/// El código de cliente solo viaja a los roles con escritura del Tenant propietario y fuera
/// de toda Sesión Privilegiada (decisión de la coordinación, 2026-10-18): Consulta, soporte e
/// impersonación lo reciben <c>null</c>, aunque la interfaz ya lo oculte con
/// <c>SoloConEscritura</c>. Ocultarlo solo en la pantalla dejaría el dato en la respuesta.
/// </para>
/// </summary>
public record ObtenerPlataformaCaeClienteQuery(Guid ClienteEmpresarialId) : IRequest<PlataformaCaeClienteDto?>;

/// <param name="Registrada">False: el Cliente empresarial aún no tiene plataforma registrada.</param>
/// <param name="CodigoCliente">Solo para los roles con escritura; <c>null</c> para el resto.</param>
public record PlataformaCaeClienteDto(bool Registrada, string? Nombre, string? UrlPortal, string? CodigoCliente);

public class ObtenerPlataformaCaeClienteQueryHandler(
    IEmpresasQueryContext dbContext,
    IAlcanceDatosService alcanceDatos,
    ICurrentUserService currentUserService,
    ISesionPrivilegiadaActual sesionPrivilegiadaActual)
    : IRequestHandler<ObtenerPlataformaCaeClienteQuery, PlataformaCaeClienteDto?>
{
    // Mismos literales que AutorizacionEscrituraBehavior.RolesConEscritura, mismo motivo:
    // Application no puede referenciar Infrastructure.Identity.Roles.
    private static readonly string[] RolesQueVenElCodigoDeCliente =
        ["Administrador", "DireccionCae", "CoordinadorCae", "GestorCae"];

    public async Task<PlataformaCaeClienteDto?> Handle(ObtenerPlataformaCaeClienteQuery request, CancellationToken cancellationToken)
    {
        if (!await alcanceDatos.ClienteVisibleAsync(request.ClienteEmpresarialId, cancellationToken)
            || !await alcanceDatos.OperaDesdeElLadoDeGestionAsync(cancellationToken))
            return null;

        if (!await dbContext.Empresas.AnyAsync(e => e.Id == request.ClienteEmpresarialId, cancellationToken))
            return null;

        var plataforma = await dbContext.PlataformasCaeClienteEmpresarial
            .Where(p => p.ClienteEmpresarialId == request.ClienteEmpresarialId)
            .Select(p => new PlataformaCaeClienteDto(true, p.Nombre, p.UrlPortal, p.CodigoCliente))
            .FirstOrDefaultAsync(cancellationToken);

        if (plataforma is null)
            return new PlataformaCaeClienteDto(false, null, null, null);

        return await VeElCodigoDeClienteAsync(cancellationToken) ? plataforma : plataforma with { CodigoCliente = null };
    }

    private async Task<bool> VeElCodigoDeClienteAsync(CancellationToken cancellationToken) =>
        await sesionPrivilegiadaActual.ObtenerAsync(cancellationToken) is null
        && await currentUserService.ObtenerRolEfectivoAsync() is { } rol
        && RolesQueVenElCodigoDeCliente.Contains(rol);
}
