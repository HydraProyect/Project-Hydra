using CaeManager.Application.Common;
using CaeManager.Application.Comunicaciones.Commands.ActualizarDocumentoDesdeAdjunto;
using CaeManager.Domain.Common;
using CaeManager.Domain.Comunicaciones;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CaeManager.Application.Comunicaciones.Commands.AsignarAdjuntosLote;

/// <summary>
/// Un adjunto que una persona aprobó: el tipo y el destino (Trabajador o Empresa) son los que vio en pantalla, y las fechas
/// las revisó ella. El lote no los usa para cambiar la propuesta: si ya no coinciden con la del adjunto, ese adjunto sale
/// como <see cref="EstadoAsignacionAdjunto.Conflicto"/> sin Documento.
/// </summary>
public record AdjuntoAAsignar(
    Guid AdjuntoId, Guid? TipoDocumentoId, Guid? TrabajadorId, Guid? EmpresaId,
    DateOnly FechaEmision, DateOnly? FechaVencimientoManual, bool NoCaduca);

public enum EstadoAsignacionAdjunto
{
    Asignado = 0,

    /// <summary><see cref="ResultadoAsignacionAdjunto.Detalle"/> dice por qué no se asignó.</summary>
    NoAsignado = 1,

    /// <summary>
    /// La propuesta del adjunto ya no es la aprobada, o otra sesión cambió el adjunto o su Documento durante el alta: no se
    /// guardó nada.
    /// </summary>
    Conflicto = 2,

    /// <summary>
    /// Falló sin confirmar el alta o antes de llegar a ella. Documento y adjunto se guardan juntos, así que el estado del
    /// adjunto dice qué pasó.
    /// </summary>
    Error = 3,

    /// <summary>
    /// No se pudo comprobar si el alta llegó a guardarse: no afirma ni que sí ni que no. Hay que revisar el adjunto antes
    /// de reintentar, y <see cref="ResultadoAsignacionAdjunto.DocumentoId"/> va vacío porque no consta ninguno.
    /// </summary>
    Indeterminado = 4
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
        // Sin la propuesta aprobada no hay con qué contrastar la vigente: el lote no adivina qué vio la persona.
        RuleForEach(c => c.Adjuntos)
            .Must(a => a is not null && a.TipoDocumentoId is { } tipo && tipo != Guid.Empty
                && (a.TrabajadorId is null) != (a.EmpresaId is null))
            .WithMessage("Cada adjunto del lote lleva el tipo de documento y un único destino aprobados.");
    }
}

/// <summary>
/// Asigna de una vez los adjuntos de una conversación. <b>No envía nada</b>: cada adjunto pasa por
/// <see cref="ActualizarDocumentoDesdeAdjuntoCommand"/>, que crea o renueva su Documento con la autorización, el
/// alcance y las guardas del alta normal, y que deja el adjunto asignado en el mismo guardado.
///
/// No es todo o nada: cada Documento confirma por separado, así que un adjunto rechazado no deshace los anteriores y
/// el resultado dice qué pasó con cada uno.
///
/// Cada elemento lleva el destino y el tipo que la persona aprobó. Justo antes de cada alta se lee la propuesta vigente
/// del adjunto: si no es la aprobada (otra sesión la cambió desde que se pintó la pantalla), ese adjunto sale como
/// <see cref="EstadoAsignacionAdjunto.Conflicto"/> sin llegar al alta. Si coincide, el alta recibe la aprobada como
/// propuesta vista y vuelve a contrastarla en el guardado, así que un cambio posterior también acaba en conflicto, sin
/// Documento.
/// </summary>
public class AsignarAdjuntosLoteCommandHandler(
    IComunicacionesQueryContext comunicacionesContext, IAlcanceDatosService alcanceDatos, IMediator mediator,
    ILogger<AsignarAdjuntosLoteCommandHandler> logger)
    : IRequestHandler<AsignarAdjuntosLoteCommand, Result<IReadOnlyList<ResultadoAsignacionAdjunto>>>
{
    private sealed record Candidato(
        EstadoRepartoAdjunto Estado, DireccionMensaje Direccion, Guid? TrabajadorId, Guid? EmpresaId, Guid? TipoDocumentoId);

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
         select new Candidato(a.EstadoReparto, m.Direccion, a.DestinoTrabajadorId, a.DestinoEmpresaId, a.TipoDocumentoPropuestoId))
        .FirstOrDefaultAsync(cancellationToken);

    private async Task<ResultadoAsignacionAdjunto> AsignarAsync(Guid conversacionId, AdjuntoAAsignar item, CancellationToken cancellationToken)
    {
        try
        {
            var candidato = await LeerAsync(conversacionId, item.AdjuntoId, cancellationToken);
            if (MotivoParaNoAsignar(candidato) is { } motivo)
                return NoAsignado(item, motivo);
            if (!EsLaAprobada(candidato!, item))
                return new ResultadoAsignacionAdjunto(item.AdjuntoId, EstadoAsignacionAdjunto.Conflicto, null, PropuestaCambiada);
            if (MotivoSinPropuesta(candidato!) is { } sinPropuesta)
                return NoAsignado(item, sinPropuesta);

            var asignado = await mediator.Send(new ActualizarDocumentoDesdeAdjuntoCommand(
                item.AdjuntoId, item.TipoDocumentoId!.Value, item.TrabajadorId, item.EmpresaId,
                item.FechaEmision, item.FechaVencimientoManual, null, item.NoCaduca, AplicaPropuestaFijada: true), cancellationToken);

            if (asignado.EsExitoso)
                return new ResultadoAsignacionAdjunto(item.AdjuntoId, EstadoAsignacionAdjunto.Asignado, asignado.Valor, null);

            return asignado.Error.Codigo switch
            {
                ActualizarDocumentoDesdeAdjuntoCommandHandler.CodigoConflicto =>
                    new ResultadoAsignacionAdjunto(item.AdjuntoId, EstadoAsignacionAdjunto.Conflicto, null, asignado.Error.Mensaje),
                ActualizarDocumentoDesdeAdjuntoCommandHandler.CodigoEstadoIndeterminado =>
                    new ResultadoAsignacionAdjunto(item.AdjuntoId, EstadoAsignacionAdjunto.Indeterminado, null, asignado.Error.Mensaje),
                _ => NoAsignado(item, asignado.Error.Mensaje)
            };
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
            // El alta comprueba lo guardado antes de propagar: lo que llega aquí no confirmó nada, o ni la alcanzó.
            logger.LogWarning("La asignación por lote falló en el adjunto {AdjuntoId} ({Excepcion}).", item.AdjuntoId, ex.GetType().Name);
            return new ResultadoAsignacionAdjunto(item.AdjuntoId, EstadoAsignacionAdjunto.Error, null, "No se pudo completar. Comprueba el estado del adjunto.");
        }
    }

    public const string PropuestaCambiada =
        "El destino o el tipo de este adjunto cambiaron después de revisarlo, y no se ha asignado. Revisa la propuesta y vuelve a asignarlo.";

    private static string? MotivoParaNoAsignar(Candidato? candidato)
    {
        if (candidato is null) return "Este adjunto no pertenece a la conversación.";
        if (candidato.Direccion != DireccionMensaje.Entrante) return "Solo se reparten los adjuntos recibidos.";
        if (candidato.Estado != EstadoRepartoAdjunto.Pendiente) return "Este adjunto ya está resuelto.";
        return null;
    }

    // Se comparan los tres valores tal cual, nulos incluidos: quitar el destino o el tipo también cambia la propuesta.
    private static bool EsLaAprobada(Candidato candidato, AdjuntoAAsignar item) =>
        candidato.TipoDocumentoId == item.TipoDocumentoId
        && candidato.TrabajadorId == item.TrabajadorId
        && candidato.EmpresaId == item.EmpresaId;

    // Solo se llega con la propuesta igual a la aprobada: falta algo únicamente si la aprobada ya venía incompleta.
    private static string? MotivoSinPropuesta(Candidato candidato)
    {
        if (candidato.TrabajadorId is null && candidato.EmpresaId is null) return "Este adjunto no tiene destino.";
        if (candidato.TipoDocumentoId is null) return "Este adjunto no tiene tipo de documento.";
        return null;
    }

    private static ResultadoAsignacionAdjunto NoAsignado(AdjuntoAAsignar item, string? detalle) =>
        new(item.AdjuntoId, EstadoAsignacionAdjunto.NoAsignado, null, detalle);
}
