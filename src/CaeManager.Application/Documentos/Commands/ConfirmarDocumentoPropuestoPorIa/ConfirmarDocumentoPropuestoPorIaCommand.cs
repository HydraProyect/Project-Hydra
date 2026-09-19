using CaeManager.Application.Common;
using CaeManager.Application.Documentos.Commands.CrearDocumento;
using CaeManager.Domain.Common;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Documentos.Commands.ConfirmarDocumentoPropuestoPorIa;

/// <summary>
/// Lo que la IA propuso para un archivo antes de que exista el Documento: el
/// Trabajador emparejado, el tipo y las fechas leídas del archivo. Nada de esto
/// crea nada por sí solo; es el término de comparación para saber si la persona
/// confirmó tal cual o corrigió.
/// </summary>
public record PropuestaIaDocumento(
    Guid? TrabajadorId, Guid? TipoDocumentoId, DateOnly? FechaEmision, DateOnly? FechaVencimiento, int Confianza)
{
    public static PropuestaIaDocumento Vacia { get; } = new(null, null, null, null, 0);

    public bool EstaVacia => TrabajadorId is null && TipoDocumentoId is null && FechaEmision is null && FechaVencimiento is null;
}

/// <summary>
/// "La IA propone y una persona confirma antes de que exista el Documento"
/// (decisión del propietario, 2026-09-19; corrige el riesgo R-1 del inventario
/// de funciones de IA, F-04 en subida múltiple).
///
/// Es el ÚNICO camino por el que una propuesta de la detección previa se
/// convierte en un Documento de Trabajador. Los valores que trae —Trabajador,
/// tipo, fecha de emisión— son los que la persona dejó a la vista al pulsar
/// «confirmar», no los que propuso la IA: por eso <see cref="FechaEmision"/> es
/// obligatoria y sin valor por defecto, y la propuesta viaja aparte
/// (<see cref="Propuesta"/>) solo para dejar constancia de qué se confirmó y qué
/// se corrigió.
///
/// Lo que garantiza este Command, y lo que no: exige que quien lo ejecuta sea
/// una <see cref="TipoActor.Persona"/> con identidad resuelta, así que un
/// servicio de fondo o una integración con <c>ClaveApi</c> no pueden
/// confirmar nada. No puede demostrar que la persona mirase la pantalla; eso
/// lo garantiza la UI al no ofrecer ningún camino que lo llame sin una acción
/// suya por archivo.
///
/// Delega en <see cref="CrearDocumentoCommand"/> vía <see cref="IMediator"/> —
/// mismo patrón de "un Command orquesta otro" que
/// <c>ActualizarDocumentoDesdeAdjuntoCommand</c>—, así que la autorización de
/// escritura (Consulta no puede), la validación y la auditoría del actor real
/// son las del alta normal.
/// </summary>
public record ConfirmarDocumentoPropuestoPorIaCommand(
    Guid TrabajadorId, Guid TipoDocumentoId, DateOnly FechaEmision, DateOnly? FechaVencimientoManual,
    string ArchivoUrl, PropuestaIaDocumento Propuesta)
    : ICommand<Guid>;

public class ConfirmarDocumentoPropuestoPorIaCommandValidator : AbstractValidator<ConfirmarDocumentoPropuestoPorIaCommand>
{
    public ConfirmarDocumentoPropuestoPorIaCommandValidator()
    {
        RuleFor(c => c.TrabajadorId).NotEmpty().WithMessage("Selecciona un trabajador.");
        RuleFor(c => c.TipoDocumentoId).NotEmpty().WithMessage("Selecciona un tipo de documento.");
        RuleFor(c => c.ArchivoUrl).NotEmpty();
        RuleFor(c => c.Propuesta).NotNull();
        // Sin valor por defecto: default(DateOnly) es 0001-01-01, no "hoy".
        RuleFor(c => c.FechaEmision).NotEqual(default(DateOnly)).WithMessage("Indica la fecha de emisión.");
    }
}

public class ConfirmarDocumentoPropuestoPorIaCommandHandler(IMediator mediator, IActorAuditoria actorAuditoria)
    : IRequestHandler<ConfirmarDocumentoPropuestoPorIaCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(ConfirmarDocumentoPropuestoPorIaCommand request, CancellationToken cancellationToken)
    {
        // Antes que cualquier otra cosa: la confirmación es un acto de una
        // persona. Sin identidad resuelta el tipo queda Desconocido, que no
        // afirma persona, así que también se rechaza (fallo cerrado).
        var actor = await actorAuditoria.ObtenerAsync();
        if (actor.ActorRealUsuarioId is null || actor.ResolverTipoActor() != TipoActor.Persona)
            return Result.Fallo<Guid>(Error.Crear(
                "Documento.ConfirmacionHumanaRequerida",
                "Solo una persona puede confirmar un documento propuesto por la IA."));

        return await mediator.Send(new CrearDocumentoCommand(
            request.TrabajadorId, null, null, null, null,
            request.TipoDocumentoId, request.FechaEmision, request.FechaVencimientoManual,
            request.ArchivoUrl, DescribirOrigen(request)), cancellationToken);
    }

    /// <summary>
    /// Deja en el propio Documento qué propuso la IA y qué hizo la persona. No
    /// sustituye a la auditoría (que ya firma al actor real): es lo que ve
    /// quien abra el Documento sin ir a buscar en el rastro.
    /// </summary>
    internal static string DescribirOrigen(ConfirmarDocumentoPropuestoPorIaCommand request)
    {
        const string Base = "Creado desde subida múltiple.";
        var propuesta = request.Propuesta;
        if (propuesta.EstaVacia)
            return $"{Base} La IA no propuso datos; los indicó una persona.";

        var corregidos = new List<string>();
        if (propuesta.TrabajadorId != request.TrabajadorId) corregidos.Add("trabajador");
        if (propuesta.TipoDocumentoId != request.TipoDocumentoId) corregidos.Add("tipo");
        if (propuesta.FechaEmision != request.FechaEmision) corregidos.Add("fecha de emisión");
        if (request.FechaVencimientoManual is not null && propuesta.FechaVencimiento != request.FechaVencimientoManual)
            corregidos.Add("fecha de vencimiento");

        var origen = $"Propuesta de la IA ({propuesta.Confianza} % de confianza)";
        return corregidos.Count == 0
            ? $"{Base} {origen} confirmada por una persona."
            : $"{Base} {origen} corregida por una persona: {string.Join(", ", corregidos)}.";
    }
}
