using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Plataforma;
using MediatR;

namespace CaeManager.Application.Plataforma.OrdenMenu;

/// <summary>
/// Guarda el orden global del menú lateral (decisión del propietario de 2026-09-23, MVP-1).
/// Listas vacías = restablecer el orden por defecto del catálogo.
///
/// <para>
/// Autorización global, no acotada por tenant, mismo criterio que
/// <c>CambiarActivoProveedorPlataformaCommand</c>: el orden lo ven todos los Tenants, así que no
/// es una decisión de ninguno. Solo el Actor de Plataforma TALVEG con una concesión
/// AdminPlataforma <b>global</b> vigente; un Administrador de Tenant no puede, tenga el rol que
/// tenga. La interfaz oculta la pantalla a los demás, pero la barrera es esta; debajo, la RLS de
/// la tabla repite el predicado (<c>app_es_admin_plataforma_global</c>).
/// </para>
///
/// <para>
/// <c>VersionEsperada</c> es la versión que se vio en pantalla (<see cref="Guid.Empty"/> si no
/// había orden guardado): dos Actores de Plataforma con la pantalla abierta no se pisan en
/// silencio.
/// </para>
///
/// <para>
/// La auditoría la escribe <c>AuditoriaInterceptor</c> con el Actor real, como para toda entidad
/// de dominio; además la fila guarda quién hizo el último cambio para mostrarlo en la pantalla.
/// </para>
/// </summary>
public record GuardarOrdenMenuLateralCommand(
    IReadOnlyList<string> Grupos, IReadOnlyList<string> Enlaces, Guid VersionEsperada) : ICommand;

public class GuardarOrdenMenuLateralCommandHandler(
    IOrdenMenuLateralRepository repositorio,
    IAutorizacionAdminPlataforma autorizacion,
    ICurrentUserService currentUserService,
    IActorAuditoria actorAuditoria,
    IUnitOfWork unitOfWork,
    CacheOrdenMenuLateral cache)
    : IRequestHandler<GuardarOrdenMenuLateralCommand, Result>
{
    public async Task<Result> Handle(GuardarOrdenMenuLateralCommand request, CancellationToken cancellationToken)
    {
        var usuarioId = await currentUserService.ObtenerUsuarioActualIdAsync();
        if (usuarioId is null)
            return Result.Fallo(Error.Crear("OrdenMenu.SinUsuario", "No pudimos identificarte. Vuelve a iniciar sesión."));

        if (!await autorizacion.PuedeGlobalmenteAsync(usuarioId.Value, cancellationToken))
            return Result.Fallo(Error.Crear(
                "OrdenMenu.SinPermiso", "Solo la administración de plataforma de TALVEG puede ordenar el menú."));

        var actorReal = (await actorAuditoria.ObtenerAsync()).ActorRealUsuarioId ?? usuarioId.Value;
        var ahora = DateTime.UtcNow;

        try
        {
            var orden = await repositorio.ObtenerAsync(cancellationToken);
            if (orden is null)
            {
                if (request.VersionEsperada != Guid.Empty)
                    return Result.Fallo(Conflicto());
                repositorio.Agregar(OrdenMenuLateral.Crear(request.Grupos, request.Enlaces, actorReal, ahora));
            }
            else
            {
                if (orden.Version != request.VersionEsperada)
                    return Result.Fallo(Conflicto());
                orden.Reordenar(request.Grupos, request.Enlaces, actorReal, ahora);
            }
        }
        catch (ArgumentException ex)
        {
            return Result.Fallo(Error.Crear("OrdenMenu.NoValido", ex.Message));
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        cache.Invalidar();

        return Result.Exito();
    }

    private static Error Conflicto() => Error.Crear(
        ConcurrenciaOptimista.CodigoConflicto,
        "Otra persona cambió el orden del menú mientras lo editabas. Recarga la página para ver su versión y aplica tus cambios de nuevo.");
}
