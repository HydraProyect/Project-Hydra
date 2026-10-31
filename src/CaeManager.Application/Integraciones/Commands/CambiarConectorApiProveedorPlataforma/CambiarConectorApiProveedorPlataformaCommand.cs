using CaeManager.Application.Common;
using CaeManager.Application.Plataforma;
using CaeManager.Domain.Common;
using CaeManager.Domain.Integraciones;
using MediatR;

namespace CaeManager.Application.Integraciones.Commands.CambiarConectorApiProveedorPlataforma;

/// <summary>
/// El kill switch de la conexión por API con una plataforma CAE de terceros
/// (<see cref="ProveedorPlataformaCae.ConectorApiActivo"/>): corta todas las llamadas a esa plataforma,
/// de todos los Tenants, sin desplegar. Global como
/// <c>CambiarActivoProveedorPlataformaCommand</c>, que es el kill switch de la extensión de navegador
/// y queda aparte: el catálogo no pertenece a ningún Tenant.
/// </summary>
public record CambiarConectorApiProveedorPlataformaCommand(Guid ProveedorId, bool Activo) : ICommand;

public class CambiarConectorApiProveedorPlataformaCommandHandler(
    IProveedorPlataformaCaeRepository proveedorRepositorio,
    IAutorizacionAdminPlataforma autorizacion,
    ICurrentUserService currentUserService,
    IUnitOfWork unitOfWork)
    : IRequestHandler<CambiarConectorApiProveedorPlataformaCommand, Result>
{
    public async Task<Result> Handle(CambiarConectorApiProveedorPlataformaCommand request, CancellationToken cancellationToken)
    {
        var usuarioId = await currentUserService.ObtenerUsuarioActualIdAsync();
        if (usuarioId is null)
            return Result.Fallo(Error.Crear("ProveedorPlataforma.SinUsuario", "No pudimos identificarte. Vuelve a iniciar sesión."));

        if (!await autorizacion.PuedeGlobalmenteAsync(usuarioId.Value, cancellationToken))
            return Result.Fallo(Error.Crear(
                "ProveedorPlataforma.SinPermiso", "Solo la administración de plataforma puede activar o desactivar un conector."));

        var proveedor = await proveedorRepositorio.ObtenerPorIdAsync(request.ProveedorId, cancellationToken);
        if (proveedor is null)
            return Result.Fallo(Error.Crear("ProveedorPlataforma.NoEncontrado", "No encontramos este proveedor."));

        if (request.Activo)
            proveedor.ActivarConectorApi();
        else
            proveedor.DesactivarConectorApi();

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}
