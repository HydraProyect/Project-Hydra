using CaeManager.Application.Common;
using CaeManager.Application.Documentos.Acreditacion;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Centros.Commands.EliminarDocumentacionRequeridaCentro;

/// <summary>
/// Quita la posición explícita de un Centro sobre un TipoDocumento — vuelve a
/// seguir el criterio global de <c>TipoDocumento.EsObligatorio</c>
/// (Project-Hydra-Negocio/tecnico/docs/ux-audit/PLAN-EJECUCION-UX.md § 0.4). Baja física, sin ciclo de vida propio, mismo
/// criterio que el resto de altas/bajas de <see cref="TipoDocumentoCentro"/>.
/// </summary>
public record EliminarDocumentacionRequeridaCentroCommand(Guid CentroId, Guid TipoDocumentoId) : ICommand;

public class EliminarDocumentacionRequeridaCentroCommandValidator : AbstractValidator<EliminarDocumentacionRequeridaCentroCommand>
{
    public EliminarDocumentacionRequeridaCentroCommandValidator()
    {
        RuleFor(c => c.CentroId).NotEmpty();
        RuleFor(c => c.TipoDocumentoId).NotEmpty();
    }
}

public class EliminarDocumentacionRequeridaCentroCommandHandler(
    ITipoDocumentoCentroRepository repositorio, IAlcanceDatosService alcanceDatos, IUnitOfWork unitOfWork,
    IAltaAcreditacionesPlataformaService altaAcreditaciones, ISerializacionAltasAcreditacionEmpresa serializacionAltas,
    IInvalidadorAlcance invalidadorAlcance)
    : IRequestHandler<EliminarDocumentacionRequeridaCentroCommand, Result>
{
    private static Result CentroNoEncontrado() =>
        Result.Fallo(Error.Crear("DocumentacionRequeridaCentro.CentroNoEncontrado", "No encontramos este centro."));

    public async Task<Result> Handle(EliminarDocumentacionRequeridaCentroCommand request, CancellationToken cancellationToken)
    {
        // Asignación de Cartera: la Documentación requerida es configuración DEL Centro; fuera del
        // alcance de gestión el Centro «no existe» (mismo criterio que EditarCentroCommand).
        if (!await alcanceDatos.CentroParaGestionVisibleAsync(request.CentroId, cancellationToken))
            return CentroNoEncontrado();

        // Quitar una exclusión de un tipo exigido de serie hace que el Centro pase a exigirlo: se serializa con el alta de
        // un Documento de Empresa, igual que EstablecerDocumentacionRequeridaCentro (ver
        // ISerializacionAltasAcreditacionEmpresa). Se toma siempre, sin mirar antes la fila ni el tipo: qué se borra y si
        // el valor general es Sí se decide con lo releído bajo el candado.
        return await serializacionAltas.EjecutarAsync(async ct =>
        {
            // La Asignación de Cartera se vuelve a decidir bajo el candado: pudo revocarse mientras se esperaba, y el
            // alcance memoizado (en Blazor Server, el del circuito) es de antes; se descarta, igual que en
            // BloqueoRespuestaSugerida.ComprobarEnvioPorBuzonAsync. Revisión Codex de la PR #2104, ronda 2.
            invalidadorAlcance.Invalidar();
            if (!await alcanceDatos.CentroParaGestionVisibleAsync(request.CentroId, ct))
                return CentroNoEncontrado();

            var fila = await repositorio.ObtenerPorParActualizadoAsync(request.TipoDocumentoId, request.CentroId, ct);
            if (fila is null)
                return Result.Exito();

            repositorio.Eliminar(fila);
            // El Centro vuelve al valor general del tipo; si es Sí, los Documentos de Empresa de ese tipo de quienes
            // tienen actividad en él nacen pendientes de acreditar (regla A-1). Los de Trabajador no cambian.
            await altaAcreditaciones.AgregarPendientesAsync(new AltasConAcreditacion
            {
                RequisitosRetirados = [fila],
                SoloDocumentosDeEmpresa = true
            }, ct);
            await unitOfWork.SaveChangesAsync(ct);
            return Result.Exito();
        }, cancellationToken);
    }
}
