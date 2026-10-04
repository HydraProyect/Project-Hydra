using CaeManager.Application.Common;
using CaeManager.Application.Reclamaciones.Commands.EnviarReclamacion;
using CaeManager.Application.Reclamaciones.Commands.EnviarReclamacionEmpresa;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using MediatR;

namespace CaeManager.Application.Reclamaciones.Commands.PrepararVistaPreviaReclamacion;

/// <summary>
/// Vista previa de un envío de reclamación ya compuesto (hoy, «Reclamar de
/// nuevo» desde el historial): a quién iría, con qué asunto y qué texto. No
/// envía ni escribe nada. Resuelve con exactamente la misma implementación
/// que el envío (<c>PrepararAsync</c> de los handlers): mismo acceso, misma
/// ventana de reclamables, mismo todo o nada y misma resolución de agenda.
/// Un envío que fallaría (documentos ya renovados, sin acceso, sin
/// destinatario) falla también aquí, con el mismo error.
///
/// <para>
/// Implementa <see cref="ICommand{TRespuesta}"/> aunque no escriba, a propósito:
/// así pasa por la misma puerta que el envío (rol con escritura, sesión
/// privilegiada, puerta comercial) y por el mismo alcance sin memoizar, y quien
/// no puede enviar tampoco ve a quién se enviaría ni el texto con nombres de
/// trabajadores. Una consulta pura se saltaría esos behaviors.
/// </para>
/// </summary>
/// <param name="Ambito">Titular del lote: Cliente (documentos de Trabajador) o Empresa.</param>
public record PrepararVistaPreviaReclamacionCommand(
    AmbitoAplicacion Ambito,
    Guid TitularId,
    IReadOnlyList<Guid> DocumentoIds,
    IReadOnlyList<PendienteSinFecha>? Pendientes = null) : ICommand<ReclamacionPreparada>;

public class PrepararVistaPreviaReclamacionCommandHandler(
    EnviarReclamacionCommandHandler enviarCliente,
    EnviarReclamacionEmpresaCommandHandler enviarEmpresa)
    : IRequestHandler<PrepararVistaPreviaReclamacionCommand, Result<ReclamacionPreparada>>
{
    public Task<Result<ReclamacionPreparada>> Handle(PrepararVistaPreviaReclamacionCommand request, CancellationToken cancellationToken) =>
        request.Ambito == AmbitoAplicacion.Empresa
            ? enviarEmpresa.PrepararAsync(new EnviarReclamacionEmpresaCommand(request.TitularId, request.DocumentoIds, Pendientes: request.Pendientes), cancellationToken)
            : enviarCliente.PrepararAsync(new EnviarReclamacionCommand(request.TitularId, request.DocumentoIds, Pendientes: request.Pendientes), cancellationToken);
}
