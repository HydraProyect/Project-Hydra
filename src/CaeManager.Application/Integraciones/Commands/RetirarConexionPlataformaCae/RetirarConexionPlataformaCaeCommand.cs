using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Integraciones;
using MediatR;

namespace CaeManager.Application.Integraciones.Commands.RetirarConexionPlataformaCae;

/// <summary>
/// Retira una conexión del Tenant propietario actual y destruye su credencial: la fila de la conexión
/// queda eliminada de forma lógica porque sus usos la referencian; el secreto se borra de verdad
/// (OWASP ASVS 4.0.3 V6.4.1). Misma autorización que
/// <c>GuardarConexionPlataformaCaeCommand</c>.
/// </summary>
public record RetirarConexionPlataformaCaeCommand(Guid ConexionId) : ICommand, IEscrituraDeDatosDeCredencial;

public class RetirarConexionPlataformaCaeCommandHandler(
    IConexionPlataformaCaeRepository conexiones,
    ICurrentUserService usuarioActual,
    ITenantActual tenantActual,
    IUnitOfWork unitOfWork)
    : IRequestHandler<RetirarConexionPlataformaCaeCommand, Result>
{
    public async Task<Result> Handle(RetirarConexionPlataformaCaeCommand request, CancellationToken cancellationToken)
    {
        var usuarioId = await usuarioActual.ObtenerUsuarioActualIdAsync();
        if (usuarioId is null || !await AutoridadSobreConexionPlataformaCae.EsAdministradorDelTenantPropietarioAsync(usuarioActual, tenantActual))
            return Result.Fallo(Error.Crear(
                "ConexionPlataformaCae.SinPermiso", "Solo un Administrador puede retirar esta conexión."));

        var conexion = await conexiones.ObtenerAsync(request.ConexionId, cancellationToken);
        if (conexion is null)
            return Result.Fallo(Error.Crear("ConexionPlataformaCae.NoEncontrada", "No encontramos esta conexión."));

        if (await conexiones.ObtenerCredencialAsync(conexion.Id, cancellationToken) is { } credencial)
            conexiones.QuitarCredencial(credencial);

        conexion.MarcarComoEliminado(usuarioId.Value);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}
