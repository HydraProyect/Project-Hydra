using CaeManager.Application.Common;
using CaeManager.Application.Comunicaciones;
using CaeManager.Domain.Common;
using CaeManager.Domain.Comunicaciones;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Comunicaciones.Commands.ConfirmarClasificacionRuidoMensaje;

/// <summary>
/// El Gestor decide que un mensaje clasificado como ruido (ronda de reducción de ruido en
/// Comunicaciones) sí importa — deja de tratarse como tal, aunque la clasificación automática siga
/// devolviendo lo mismo. Mismo criterio de reversibilidad que
/// <see cref="Commands.DescartarSugerenciaVisita.DescartarSugerenciaVisitaCorreoCommand"/>: una
/// decisión explícita del Gestor, no automática. Opera a nivel de Mensaje (mismo grano que la
/// tarjeta que lo muestra en UnifiedTimeline) y confirma las dos formas en que un mensaje puede
/// llegar marcado como ruido: la clasificación del propio Mensaje
/// (<see cref="ClasificacionRuidoMensaje"/> — resumen sin cambios, correo interno, posible
/// phishing) y, si tiene ítems de gestión pendientes que son todos repetición de algo ya reclamado
/// (<see cref="ClasificacionRuidoDetalleGestion"/>), esos también.
/// </summary>
public record ConfirmarClasificacionRuidoMensajeCommand(Guid MensajeId) : ICommand;

public class ConfirmarClasificacionRuidoMensajeCommandHandler(
    IClasificacionRuidoMensajeRepository clasificacionMensajeRepositorio,
    IClasificacionRuidoDetalleGestionRepository clasificacionDetalleRepositorio,
    IComunicacionesQueryContext comunicacionesContext,
    IAlcanceDatosService alcanceDatos,
    IUnitOfWork unitOfWork)
    : IRequestHandler<ConfirmarClasificacionRuidoMensajeCommand, Result>
{
    public async Task<Result> Handle(ConfirmarClasificacionRuidoMensajeCommand request, CancellationToken cancellationToken)
    {
        // Verificación de Id ajeno (mismo hallazgo que PR #190,
        // AlcanceDeConversacionesYBuzonesTests lo mecaniza): el Mensaje pertenece a una
        // Conversacion que puede estar atada al buzón personal de otro gestor —
        // ConversacionVisibleAsync cubre cartera de Cliente y buzón personal a la vez.
        var conversacionDelMensaje = await comunicacionesContext.Mensajes
            .Where(m => m.Id == request.MensajeId)
            .Select(m => new { m.ConversacionId })
            .FirstOrDefaultAsync(cancellationToken);

        if (conversacionDelMensaje is null)
            return Result.Fallo(Error.Crear(
                "ClasificacionRuidoMensaje.NoEncontrada", "No encontramos ninguna clasificación de ruido para este mensaje."));

        var conversacion = await comunicacionesContext.Conversaciones
            .Where(c => c.Id == conversacionDelMensaje.ConversacionId)
            .Select(c => new { c.ClienteId, c.EmpresaId, c.ConexionIntegracionId })
            .FirstOrDefaultAsync(cancellationToken);

        if (conversacion is null || !await alcanceDatos.ConversacionVisibleAsync(conversacion.ClienteId, conversacion.EmpresaId, conversacion.ConexionIntegracionId, cancellationToken))
            return Result.Fallo(Error.Crear(
                "ClasificacionRuidoMensaje.NoEncontrada", "No encontramos ninguna clasificación de ruido para este mensaje."));

        var clasificacionMensaje = await clasificacionMensajeRepositorio.ObtenerPorMensajeIdAsync(request.MensajeId, cancellationToken);
        clasificacionMensaje?.ConfirmarManualmente();

        var sugerenciaGestionId = await comunicacionesContext.SugerenciasGestionCorreo
            .Where(s => s.MensajeId == request.MensajeId)
            .Select(s => (Guid?)s.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (sugerenciaGestionId is null && clasificacionMensaje is null)
        {
            return Result.Fallo(Error.Crear(
                "ClasificacionRuidoMensaje.NoEncontrada", "No encontramos ninguna clasificación de ruido para este mensaje."));
        }

        if (sugerenciaGestionId is { } id)
        {
            var detalleIdsPendientes = await comunicacionesContext.DetallesSugerenciaGestionCorreo
                .Where(d => d.SugerenciaGestionCorreoId == id && !d.Resuelta)
                .Select(d => d.Id)
                .ToListAsync(cancellationToken);

            foreach (var detalleId in detalleIdsPendientes)
            {
                var clasificacionDetalle = await clasificacionDetalleRepositorio.ObtenerPorDetalleIdAsync(detalleId, cancellationToken);
                clasificacionDetalle?.ConfirmarManualmente();
            }
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Exito();
    }
}
