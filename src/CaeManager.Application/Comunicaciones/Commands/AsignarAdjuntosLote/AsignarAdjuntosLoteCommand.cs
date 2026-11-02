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

    /// <summary>La asignación falló sin respuesta: el Documento pudo quedar guardado, así que se pide comprobarlo.</summary>
    SinConfirmar = 2
}

public record ResultadoAsignacionAdjunto(Guid AdjuntoId, EstadoAsignacionAdjunto Estado, Guid? DocumentoId, string? Detalle);

public record AsignarAdjuntosLoteCommand(Guid ConversacionId, IReadOnlyList<AdjuntoAAsignar> Adjuntos)
    : ICommand<IReadOnlyList<ResultadoAsignacionAdjunto>>;

public class AsignarAdjuntosLoteCommandValidator : AbstractValidator<AsignarAdjuntosLoteCommand>
{
    public const int MaximoAdjuntos = 50;

    public AsignarAdjuntosLoteCommandValidator()
    {
        RuleFor(c => c.ConversacionId).NotEmpty();
        RuleFor(c => c.Adjuntos).NotEmpty().WithMessage("No hay adjuntos que asignar.");
        RuleFor(c => c.Adjuntos.Count).LessThanOrEqualTo(MaximoAdjuntos).WithMessage($"No se pueden asignar más de {MaximoAdjuntos} adjuntos a la vez.");
        RuleFor(c => c.Adjuntos)
            .Must(a => a.Select(x => x.AdjuntoId).Distinct().Count() == a.Count)
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
/// </summary>
public class AsignarAdjuntosLoteCommandHandler(
    IComunicacionesQueryContext comunicacionesContext, IAlcanceDatosService alcanceDatos, IMediator mediator,
    ILogger<AsignarAdjuntosLoteCommandHandler> logger)
    : IRequestHandler<AsignarAdjuntosLoteCommand, Result<IReadOnlyList<ResultadoAsignacionAdjunto>>>
{
    private sealed record Candidato(
        Guid Id, EstadoRepartoAdjunto Estado, DireccionMensaje Direccion, Guid? TrabajadorId, Guid? EmpresaId, Guid? TipoDocumentoId);

    public async Task<Result<IReadOnlyList<ResultadoAsignacionAdjunto>>> Handle(
        AsignarAdjuntosLoteCommand request, CancellationToken cancellationToken)
    {
        if (!await RepartoAdjuntos.ConversacionVisibleAsync(comunicacionesContext, alcanceDatos, request.ConversacionId, cancellationToken))
            return Result.Fallo<IReadOnlyList<ResultadoAsignacionAdjunto>>(Error.Crear("Conversacion.NoEncontrada", "No encontramos esta conversación."));

        var adjuntoIds = request.Adjuntos.Select(a => a.AdjuntoId).ToList();
        var candidatos = await (
            from a in comunicacionesContext.AdjuntosMensaje
            join m in comunicacionesContext.Mensajes on a.MensajeId equals m.Id
            where m.ConversacionId == request.ConversacionId && adjuntoIds.Contains(a.Id)
            select new Candidato(a.Id, a.EstadoReparto, m.Direccion, a.DestinoTrabajadorId, a.DestinoEmpresaId, a.TipoDocumentoPropuestoId))
            .ToListAsync(cancellationToken);

        var resultados = new List<ResultadoAsignacionAdjunto>(request.Adjuntos.Count);
        foreach (var item in request.Adjuntos)
            resultados.Add(await AsignarAsync(item, candidatos.FirstOrDefault(c => c.Id == item.AdjuntoId), cancellationToken));

        return Result.Exito<IReadOnlyList<ResultadoAsignacionAdjunto>>(resultados);
    }

    private async Task<ResultadoAsignacionAdjunto> AsignarAsync(AdjuntoAAsignar item, Candidato? candidato, CancellationToken cancellationToken)
    {
        if (MotivoParaNoAsignar(candidato) is { } motivo)
            return NoAsignado(item, motivo);

        var comando = new ActualizarDocumentoDesdeAdjuntoCommand(
            item.AdjuntoId, candidato!.TipoDocumentoId!.Value, candidato.TrabajadorId, candidato.EmpresaId,
            item.FechaEmision, item.FechaVencimientoManual, null, item.NoCaduca);

        try
        {
            var asignado = await mediator.Send(comando, cancellationToken);
            return asignado.EsExitoso
                ? new ResultadoAsignacionAdjunto(item.AdjuntoId, EstadoAsignacionAdjunto.Asignado, asignado.Valor, null)
                : NoAsignado(item, asignado.Error.Mensaje);
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
        if (candidato.TrabajadorId is null && candidato.EmpresaId is null) return "Este adjunto no tiene destino.";
        if (candidato.TipoDocumentoId is null) return "Este adjunto no tiene tipo de documento.";
        return null;
    }

    private static ResultadoAsignacionAdjunto NoAsignado(AdjuntoAAsignar item, string? detalle) =>
        new(item.AdjuntoId, EstadoAsignacionAdjunto.NoAsignado, null, detalle);
}
