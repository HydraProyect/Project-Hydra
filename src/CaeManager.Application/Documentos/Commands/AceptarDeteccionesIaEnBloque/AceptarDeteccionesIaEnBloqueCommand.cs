using CaeManager.Application.Common;
using CaeManager.Application.Documentos.Commands.AplicarDeteccionIaDocumento;
using CaeManager.Domain.Common;
using MediatR;
using Microsoft.Extensions.Logging;

namespace CaeManager.Application.Documentos.Commands.AceptarDeteccionesIaEnBloque;

/// <summary>
/// Acepta varias lecturas de la IA que el Gestor CAE ha seleccionado y confirmado en la Revisión IA. La confianza solo
/// preselecciona en pantalla: este comando no la mira, ni acepta nada que el usuario no haya enviado.
///
/// Cada revisión se acepta con <see cref="AplicarDeteccionIaDocumentoCommand"/> por el pipeline normal —misma
/// autorización por <see cref="ICommand"/>, mismo alcance de cartera, un guardado, una <c>AprobacionDocumento</c> y una
/// decisión humana de la auditoría por Documento, todas con el usuario actual—, de modo que cada elemento es una
/// transacción propia: uno que falla no deshace los demás ni los bloquea. Se envía con
/// <see cref="AplicarDeteccionIaDocumentoCommand.SoloSiLaVigenciaLaFijaElTipo"/>: en bloque solo entran las revisiones
/// cuyo tipo calcula la vigencia desde la emisión; las demás (la vigencia la confirma el Gestor CAE a mano) fallan con
/// <see cref="CodigosRevisionIa.CodigoVigenciaARevisarIndividualmente"/> y se aceptan de una en una.
/// </summary>
public record AceptarDeteccionesIaEnBloqueCommand(IReadOnlyList<Guid> RevisionIds) : ICommand<ResultadoAceptacionEnBloque>;

public record ResultadoAceptacionRevisionIa(Guid RevisionId, bool Aceptada, string? CodigoError, string? Mensaje);

public record ResultadoAceptacionEnBloque(IReadOnlyList<ResultadoAceptacionRevisionIa> Resultados)
{
    public int Aceptadas => Resultados.Count(r => r.Aceptada);
    public int Fallidas => Resultados.Count(r => !r.Aceptada);
}

public class AceptarDeteccionesIaEnBloqueCommandHandler(
    IMediator mediator,
    IDescarteCambiosPendientes descarteCambios,
    ILogger<AceptarDeteccionesIaEnBloqueCommandHandler> logger)
    : IRequestHandler<AceptarDeteccionesIaEnBloqueCommand, Result<ResultadoAceptacionEnBloque>>
{
    /// <summary>La interfaz no preselecciona ni deja marcar más de las que este comando acepta de una vez.</summary>
    public const int MaximoRevisionesPorBloque = 200;

    public async Task<Result<ResultadoAceptacionEnBloque>> Handle(
        AceptarDeteccionesIaEnBloqueCommand request, CancellationToken cancellationToken)
    {
        var ids = request.RevisionIds.Where(id => id != Guid.Empty).Distinct().ToList();
        if (ids.Count == 0)
            return Result.Fallo<ResultadoAceptacionEnBloque>(Error.Crear(
                "RevisionIa.BloqueVacio", "Selecciona al menos una lectura de la IA para aceptar."));
        if (ids.Count > MaximoRevisionesPorBloque)
            return Result.Fallo<ResultadoAceptacionEnBloque>(Error.Crear(
                "RevisionIa.BloqueDemasiadoGrande", $"Acepta como máximo {MaximoRevisionesPorBloque} lecturas cada vez."));

        var resultados = new List<ResultadoAceptacionRevisionIa>(ids.Count);
        foreach (var id in ids)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var resultado = await mediator.Send(
                    new AplicarDeteccionIaDocumentoCommand(id, SoloSiLaVigenciaLaFijaElTipo: true), cancellationToken);
                if (resultado.EsExitoso)
                {
                    resultados.Add(new ResultadoAceptacionRevisionIa(id, true, null, null));
                    continue;
                }

                // Un fallo puede venir con entidades ya modificadas y sin guardar (p. ej. un conflicto de
                // concurrencia que ConcurrenciaBehavior traduce a Result tras un SaveChanges fallido): se sueltan
                // para que el siguiente elemento no las arrastre y falle también. Lo anterior ya está guardado.
                descarteCambios.DescartarCambiosPendientes();
                resultados.Add(new ResultadoAceptacionRevisionIa(id, false, resultado.Error.Codigo, resultado.Error.Mensaje));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Mismo motivo que arriba, para una excepción que escapa del pipeline (IDescarteCambiosPendientes).
                descarteCambios.DescartarCambiosPendientes();
                logger.LogError(ex, "Fallo inesperado al aceptar en bloque la revisión IA {RevisionId}", id);
                resultados.Add(new ResultadoAceptacionRevisionIa(
                    id, false, "RevisionIa.ErrorInesperado", "No se pudo aceptar esta lectura; inténtalo de nuevo."));
            }
        }

        return Result.Exito(new ResultadoAceptacionEnBloque(resultados));
    }
}
