using CaeManager.Application.Common;
using CaeManager.Application.Comunicaciones.Commands.ActualizarDocumentoDesdeAdjunto;
using CaeManager.Domain.Common;
using CaeManager.Domain.Comunicaciones;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CaeManager.Application.Comunicaciones.Commands.AsignarAdjuntosLote;

/// <summary>Las fechas las revisa una persona; destino y tipo son los que el adjunto ya tiene fijados.</summary>
public record AdjuntoAAsignar(Guid AdjuntoId, DateOnly FechaEmision, DateOnly? FechaVencimientoManual, bool NoCaduca);

public enum EstadoAsignacionAdjunto
{
    Asignado = 0,

    /// <summary><see cref="ResultadoAsignacionAdjunto.Detalle"/> dice por qué no se asignó.</summary>
    NoAsignado = 1,

    /// <summary>El Documento pudo quedar guardado sin que el adjunto constara como asignado: se pide comprobarlo.</summary>
    SinConfirmar = 2
}

public record ResultadoAsignacionAdjunto(Guid AdjuntoId, EstadoAsignacionAdjunto Estado, Guid? DocumentoId, string? Detalle);

public record AsignarAdjuntosLoteCommand(Guid ConversacionId, IReadOnlyList<AdjuntoAAsignar> Adjuntos)
    : ICommand<IReadOnlyList<ResultadoAsignacionAdjunto>>;

public class AsignarAdjuntosLoteCommandValidator : AbstractValidator<AsignarAdjuntosLoteCommand>
{
    public const int MaximoAdjuntos = 20;

    public AsignarAdjuntosLoteCommandValidator()
    {
        RuleFor(c => c.ConversacionId).NotEmpty();
        RuleFor(c => c.Adjuntos).NotEmpty().WithMessage("No hay adjuntos que asignar.");
        RuleFor(c => c.Adjuntos)
            .Must(a => a is null || a.Count <= MaximoAdjuntos)
            .WithMessage($"No se pueden asignar más de {MaximoAdjuntos} adjuntos a la vez.");
        RuleFor(c => c.Adjuntos)
            .Must(a => a is null || a.Select(x => x.AdjuntoId).Distinct().Count() == a.Count)
            .WithMessage("Un adjunto aparece dos veces en el lote.");
    }
}

/// <summary>
/// Asigna de una vez los adjuntos de una conversación. <b>No envía nada</b>: cada adjunto pasa por
/// <see cref="ActualizarDocumentoDesdeAdjuntoCommand"/>, que crea o renueva su Documento con la autorización, el
/// alcance y las guardas del alta normal, y que marca el adjunto como asignado.
///
/// No es todo o nada: cada Documento confirma por separado, así que un adjunto rechazado no deshace los anteriores y
/// el resultado dice qué pasó con cada uno.
///
/// Destino y tipo se leen justo antes de cada alta. Entre esa lectura y el commit del Documento otra sesión aún puede
/// cambiarlos: no se reserva el adjunto, pero el marcado lo detecta y aquí sale como <see cref="EstadoAsignacionAdjunto.SinConfirmar"/>.
/// </summary>
public class AsignarAdjuntosLoteCommandHandler(
    IComunicacionesQueryContext comunicacionesContext, IAlcanceDatosService alcanceDatos, IMediator mediator,
    ILogger<AsignarAdjuntosLoteCommandHandler> logger)
    : IRequestHandler<AsignarAdjuntosLoteCommand, Result<IReadOnlyList<ResultadoAsignacionAdjunto>>>
{
    private sealed record Destino(Guid? TrabajadorId, Guid? EmpresaId, Guid? TipoDocumentoId);

    private sealed record Candidato(EstadoRepartoAdjunto Estado, DireccionMensaje Direccion, Destino Destino, Guid? DocumentoId);

    public async Task<Result<IReadOnlyList<ResultadoAsignacionAdjunto>>> Handle(
        AsignarAdjuntosLoteCommand request, CancellationToken cancellationToken)
    {
        if (!await RepartoAdjuntos.ConversacionVisibleAsync(comunicacionesContext, alcanceDatos, request.ConversacionId, cancellationToken))
            return Result.Fallo<IReadOnlyList<ResultadoAsignacionAdjunto>>(Error.Crear("Conversacion.NoEncontrada", "No encontramos esta conversación."));

        var resultados = new List<ResultadoAsignacionAdjunto>(request.Adjuntos.Count);
        foreach (var item in request.Adjuntos)
            resultados.Add(await AsignarAsync(request.ConversacionId, item, cancellationToken));

        return Result.Exito<IReadOnlyList<ResultadoAsignacionAdjunto>>(resultados);
    }

    private Task<Candidato?> LeerAsync(Guid conversacionId, Guid adjuntoId, CancellationToken cancellationToken) =>
        (from a in comunicacionesContext.AdjuntosMensaje
         join m in comunicacionesContext.Mensajes on a.MensajeId equals m.Id
         where m.ConversacionId == conversacionId && a.Id == adjuntoId
         select new Candidato(a.EstadoReparto, m.Direccion, new Destino(a.DestinoTrabajadorId, a.DestinoEmpresaId, a.TipoDocumentoPropuestoId), a.DocumentoId))
        .FirstOrDefaultAsync(cancellationToken);

    private async Task<ResultadoAsignacionAdjunto> AsignarAsync(Guid conversacionId, AdjuntoAAsignar item, CancellationToken cancellationToken)
    {
        try
        {
            var candidato = await LeerAsync(conversacionId, item.AdjuntoId, cancellationToken);
            if (MotivoParaNoAsignar(candidato) is { } motivo)
                return NoAsignado(item, motivo);

            var asignado = await mediator.Send(new ActualizarDocumentoDesdeAdjuntoCommand(
                item.AdjuntoId, candidato!.Destino.TipoDocumentoId!.Value, candidato.Destino.TrabajadorId, candidato.Destino.EmpresaId,
                item.FechaEmision, item.FechaVencimientoManual, null, item.NoCaduca, AplicaPropuestaFijada: true), cancellationToken);
            if (!asignado.EsExitoso) return NoAsignado(item, asignado.Error.Mensaje);

            // El marcado corre tras el commit y puede no llegar (fallo o resolución a la vez): se cuenta lo que quedó, no lo que se pidió.
            var actual = await LeerAsync(conversacionId, item.AdjuntoId, cancellationToken);
            if (actual is { Estado: EstadoRepartoAdjunto.Asignado } && actual.DocumentoId == asignado.Valor)
                return new ResultadoAsignacionAdjunto(item.AdjuntoId, EstadoAsignacionAdjunto.Asignado, asignado.Valor, null);

            return new ResultadoAsignacionAdjunto(item.AdjuntoId, EstadoAsignacionAdjunto.SinConfirmar, asignado.Valor, actual switch
            {
                { Estado: EstadoRepartoAdjunto.Asignado } => "El adjunto quedó asignado a otro Documento.",
                { } cambiado when cambiado.Destino != candidato.Destino => "El destino o el tipo del adjunto cambiaron mientras se asignaba: revisa el Documento.",
                _ => "El Documento se guardó, pero el adjunto no quedó asignado."
            });
        }
        catch (ValidationException ex)
        {
            return NoAsignado(item, ex.Errors.FirstOrDefault()?.ErrorMessage);
        }
        catch (PresupuestoMemoriaAgotadoException ex)
        {
            return NoAsignado(item, ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Crear o renovar confirman antes de publicar sus eventos: el Documento pudo quedar guardado.
            logger.LogWarning("La asignación por lote no pudo confirmar el adjunto {AdjuntoId} ({Excepcion}).", item.AdjuntoId, ex.GetType().Name);
            return new ResultadoAsignacionAdjunto(item.AdjuntoId, EstadoAsignacionAdjunto.SinConfirmar, null, null);
        }
    }

    private static string? MotivoParaNoAsignar(Candidato? candidato)
    {
        if (candidato is null) return "Este adjunto no pertenece a la conversación.";
        if (candidato.Direccion != DireccionMensaje.Entrante) return "Solo se reparten los adjuntos recibidos.";
        if (candidato.Estado != EstadoRepartoAdjunto.Pendiente) return "Este adjunto ya está resuelto.";
        if (candidato.Destino.TrabajadorId is null && candidato.Destino.EmpresaId is null) return "Este adjunto no tiene destino.";
        if (candidato.Destino.TipoDocumentoId is null) return "Este adjunto no tiene tipo de documento.";
        return null;
    }

    private static ResultadoAsignacionAdjunto NoAsignado(AdjuntoAAsignar item, string? detalle) =>
        new(item.AdjuntoId, EstadoAsignacionAdjunto.NoAsignado, null, detalle);
}
