using CaeManager.Application.Common;
using CaeManager.Application.Tenants;
using CaeManager.Domain.Common;
using CaeManager.Domain.Notificaciones;
using CaeManager.Domain.Operaciones;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Operaciones.IncorporacionCartera.Commands;

/// <summary>
/// Un Coordinador CAE rechaza la solicitud de un Gestor CAE de su mismo
/// Operador CAE. No crea ni cierra nada: solo resuelve la solicitud y avisa
/// al Gestor CAE.
/// </summary>
public record RechazarSolicitudIncorporacionCarteraCommand(Guid SolicitudId) : ICommand;

public class RechazarSolicitudIncorporacionCarteraCommandHandler(
    ICurrentUserService currentUserService,
    IDirectorioUsuariosService directorioUsuarios,
    ICatalogoIncorporacionCartera catalogo,
    ISolicitudIncorporacionCarteraRepository repositorio,
    INotificacionUsuarioRepository notificaciones,
    ITenantsQueryContext tenants)
    : IRequestHandler<RechazarSolicitudIncorporacionCarteraCommand, Result>
{
    public async Task<Result> Handle(RechazarSolicitudIncorporacionCarteraCommand request, CancellationToken cancellationToken)
    {
        var contexto = await ContextoOperadorCae.ResolverAsync(currentUserService, directorioUsuarios, cancellationToken);
        if (contexto.EsFallido) return Result.Fallo(contexto.Error);
        var ctx = contexto.Valor;

        if (!ctx.EsCoordinadorCae)
            return Result.Fallo(ErroresSolicitudCartera.SinPermiso);

        using (ctx.EnOrigen())
        {
            var solicitud = await repositorio.ObtenerPorIdAsync(request.SolicitudId, ctx.OperadorTenantId, cancellationToken);
            if (solicitud is null)
                return Result.Fallo(ErroresSolicitudCartera.NoEncontrada);
            if (solicitud.Estado != EstadoSolicitudIncorporacionCartera.Pendiente)
                return Result.Fallo(ErroresSolicitudCartera.YaResuelta);
            if (solicitud.SolicitanteUsuarioId == ctx.UsuarioId)
                return Result.Fallo(ErroresSolicitudCartera.PropiaSolicitud);

            solicitud.Rechazar(ctx.UsuarioId, DateTime.UtcNow);

            var empresa = await tenants.Tenants
                .Where(t => t.Id == solicitud.PropietarioTenantId)
                .Select(t => t.Nombre)
                .FirstOrDefaultAsync(cancellationToken) ?? "la empresa";

            // Mismo guardado que el rechazo, y en el tenant de origen: aquí no
            // hay nada que escribir en el Tenant propietario, así que el aviso
            // y la resolución son atómicos.
            notificaciones.Agregar(new NotificacionUsuario(
                solicitud.SolicitanteUsuarioId,
                "Incorporación a cartera rechazada",
                $"Tu solicitud para incorporar «{empresa}» a tu cartera se ha rechazado.",
                RutasIncorporacionCartera.Bandeja,
                "Ver mis solicitudes"));

            if (!await catalogo.GuardarDetectandoCarreraAsync(cancellationToken))
                return Result.Fallo(ErroresSolicitudCartera.YaResuelta);

            return Result.Exito();
        }
    }
}
