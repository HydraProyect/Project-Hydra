using CaeManager.Application.Common;
using CaeManager.Application.Plataforma;
using CaeManager.Domain.Common;
using CaeManager.Domain.Integraciones;
using MediatR;

namespace CaeManager.Application.Integraciones.Commands.CambiarActivoProveedorPlataforma;

/// <summary>
/// El *kill switch* remoto de MVP2 (ARQUITECTURA-INTEGRACIONES.md § 14.5 en el
/// repositorio de negocio): la extensión de navegador lee
/// <see cref="ProveedorPlataformaCae.Activo"/> de la misma respuesta que ya
/// consulta para listar pendientes, y deja de ofrecer "Subir" para un
/// proveedor inactivo — sin publicar una extensión nueva, sin tocar código.
/// Este Command es el único camino para cambiar ese dato: antes de él,
/// <c>Activar()</c>/<c>Desactivar()</c> existían en el dominio (Módulo 2, para
/// el ciclo de vida del catálogo) pero ningún Command los disparaba todavía.
///
/// Autorización global, no acotada por tenant: el catálogo de proveedores es
/// compartido por todos los tenants (ver <see cref="ProveedorPlataformaCae"/>),
/// así que "activo/inactivo" no es una decisión de ningún tenant concreto —
/// mismo criterio que <c>CrearClienteDeleganteCommand</c> para operaciones
/// transversales por naturaleza.
/// </summary>
public record CambiarActivoProveedorPlataformaCommand(Guid ProveedorId, bool Activo) : ICommand;

public class CambiarActivoProveedorPlataformaCommandHandler(
    IProveedorPlataformaCaeRepository proveedorRepositorio,
    IAutorizacionAdminPlataforma autorizacion,
    ICurrentUserService currentUserService,
    IUnitOfWork unitOfWork)
    : IRequestHandler<CambiarActivoProveedorPlataformaCommand, Result>
{
    public async Task<Result> Handle(CambiarActivoProveedorPlataformaCommand request, CancellationToken cancellationToken)
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
            proveedor.Activar();
        else
            proveedor.Desactivar();

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}
