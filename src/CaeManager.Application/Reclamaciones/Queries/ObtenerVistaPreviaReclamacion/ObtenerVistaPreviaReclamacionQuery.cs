using CaeManager.Application.Common;
using CaeManager.Application.Reclamaciones.Commands.EnviarReclamacion;
using CaeManager.Application.Reclamaciones.Commands.EnviarReclamacionEmpresa;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using MediatR;

namespace CaeManager.Application.Reclamaciones.Queries.ObtenerVistaPreviaReclamacion;

/// <summary>
/// Vista previa de un envío de reclamación ya compuesto (hoy, «Reclamar de
/// nuevo» desde el historial): a quién iría, con qué asunto y qué texto. No
/// envía ni escribe nada. Resuelve con exactamente la misma implementación
/// que el envío (<c>PrepararAsync</c> de los handlers): mismo acceso, misma
/// ventana de reclamables, mismo todo o nada y misma resolución de agenda.
/// Un envío que fallaría (documentos ya renovados, sin acceso, sin
/// destinatario) falla también aquí, con el mismo error.
/// </summary>
/// <param name="Ambito">Titular del lote: Cliente (documentos de Trabajador) o Empresa.</param>
public record ObtenerVistaPreviaReclamacionQuery(
    AmbitoAplicacion Ambito,
    Guid TitularId,
    IReadOnlyList<Guid> DocumentoIds) : IRequest<Result<ReclamacionPreparada>>;

public class ObtenerVistaPreviaReclamacionQueryHandler(
    EnviarReclamacionCommandHandler enviarCliente,
    EnviarReclamacionEmpresaCommandHandler enviarEmpresa)
    : IRequestHandler<ObtenerVistaPreviaReclamacionQuery, Result<ReclamacionPreparada>>
{
    public Task<Result<ReclamacionPreparada>> Handle(ObtenerVistaPreviaReclamacionQuery request, CancellationToken cancellationToken) =>
        request.Ambito == AmbitoAplicacion.Empresa
            ? enviarEmpresa.PrepararAsync(new EnviarReclamacionEmpresaCommand(request.TitularId, request.DocumentoIds), cancellationToken)
            : enviarCliente.PrepararAsync(new EnviarReclamacionCommand(request.TitularId, request.DocumentoIds), cancellationToken);
}
