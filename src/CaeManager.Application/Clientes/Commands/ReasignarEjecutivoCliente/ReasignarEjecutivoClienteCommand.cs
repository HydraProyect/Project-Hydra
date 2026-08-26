using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Empresas;
using CaeManager.Application.Operaciones;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Notificaciones;
using MediatR;

namespace CaeManager.Application.Clientes.Commands.ReasignarEjecutivoCliente;

/// <summary>
/// Reasigna el Gestor CAE dueño de un Cliente (Cliente.EjecutivoUsuarioId) —
/// solo para roles por encima de Gestor CAE (ver Roles.cs). Dispara los dos
/// avisos pedidos por el usuario: cambio de cartera a los Gestores
/// afectados, y — si el Cliente tiene algún TipoDocumento con lectura IA
/// desactivada — un aviso aparte al nuevo Gestor con enlace a la pantalla
/// de configuración, porque la configuración de IA se conserva tal cual al
/// reasignar (ver ConfiguracionIaDocumentoCliente).
///
/// <b>CoordinadorCae queda acotado a su ámbito de supervisión</b> (decisión de
/// dominio D-001, 2026-08-24): solo puede reasignar Clientes de la cartera
/// derivada de los Gestores que le reportan — la misma jerarquía que ya
/// acotaba su lectura (<see cref="IAlcanceDatosService"/>), que hasta esta
/// decisión no se comprobaba aquí. Administrador y DireccionCae conservan
/// alcance total, coherente con su rol.
/// </summary>
public record ReasignarEjecutivoClienteCommand(Guid ClienteId, Guid? NuevoEjecutivoUsuarioId) : ICommand;

public class ReasignarEjecutivoClienteCommandHandler(
    IEmpresaRepository empresaRepositorio,
    IConfiguracionIaDocumentoClienteRepository configuracionIaRepositorio,
    INotificacionUsuarioRepository notificacionRepositorio,
    IUnitOfWork unitOfWork,
    ICurrentUserService currentUserService,
    IAlcanceDatosService alcanceDatos,
    IAsignacionesOperativasWriter asignacionesWriter)
    : IRequestHandler<ReasignarEjecutivoClienteCommand, Result>
{
    // Application no puede referenciar Infrastructure.Identity.Roles — mismo motivo que en AutorizacionEscrituraBehavior.
    private static readonly string[] RolesPermitidos = ["Administrador", "DireccionCae", "CoordinadorCae"];

    public async Task<Result> Handle(ReasignarEjecutivoClienteCommand request, CancellationToken cancellationToken)
    {
        var rol = await currentUserService.ObtenerRolActualAsync();
        if (rol is null || !RolesPermitidos.Contains(rol))
            return Result.Fallo(Error.Crear("Cliente.SinPermisoReasignar", "Tu rol no puede reasignar la cartera de un cliente."));

        var empresa = await empresaRepositorio.ObtenerPorIdAsync(request.ClienteId, cancellationToken);
        if (empresa is null || !await alcanceDatos.ClienteVisibleAsync(empresa.Id, cancellationToken))
            return Result.Fallo(Error.Crear("Cliente.NoEncontrado", "No encontramos este cliente."));

        var ejecutivoAnteriorId = empresa.EjecutivoUsuarioId;
        if (ejecutivoAnteriorId == request.NuevoEjecutivoUsuarioId)
            return Result.Exito();

        empresa.AsignarEjecutivo(request.NuevoEjecutivoUsuarioId);

        if (ejecutivoAnteriorId is not null)
            notificacionRepositorio.Agregar(new NotificacionUsuario(
                ejecutivoAnteriorId.Value,
                "Cambio en tu cartera de clientes",
                $"Se te ha quitado el cliente \"{empresa.RazonSocial}\" de tu cartera."));

        if (request.NuevoEjecutivoUsuarioId is not null)
        {
            notificacionRepositorio.Agregar(new NotificacionUsuario(
                request.NuevoEjecutivoUsuarioId.Value,
                "Cambio en tu cartera de clientes",
                $"Se te ha asignado el cliente \"{empresa.RazonSocial}\" en tu cartera."));

            var tiposSinLecturaIa = await configuracionIaRepositorio.ObtenerNombresTiposDocumentoSinLecturaIaAsync(empresa.Id, cancellationToken);
            if (tiposSinLecturaIa.Count > 0)
                notificacionRepositorio.Agregar(new NotificacionUsuario(
                    request.NuevoEjecutivoUsuarioId.Value,
                    "Lectura automática por IA desactivada",
                    $"El cliente \"{empresa.RazonSocial}\" tiene la lectura automática por IA desactivada para: {string.Join(", ", tiposSinLecturaIa)}.",
                    urlAccion: $"/clientes/{empresa.Id}/lectura-ia",
                    textoAccion: "Gestionar"));
        }

        // Doble escritura: la cartera nueva entra en el mismo SaveChanges que
        // la proyección Empresa.EjecutivoUsuarioId, así que o se guardan las
        // dos o ninguna. La proyección sigue siendo la autoritativa durante F1.
        await asignacionesWriter.ReasignarCarteraClienteAsync(
            empresa.Id, request.NuevoEjecutivoUsuarioId, cancellationToken);

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Exito();
    }
}
