using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.TiposDocumento;
using CaeManager.Domain.Documentos;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Centros.Queries.ObtenerDocumentacionRequeridaDeCentro;

/// <summary>
/// Alimenta la pestaña "Requisitos del Centro" del Context Workspace
/// (Project-Hydra-Negocio/tecnico/docs/ux-audit/PLAN-EJECUCION-UX.md § 0.4) — catálogo completo de TipoDocumento
/// (Trabajador + Empresa) del tenant, con la posición de ESTE Centro sobre
/// cada uno: si aplica hoy (<see cref="ResolucionTipoDocumentoCentro"/>), si
/// hay una fila explícita (y su Incluido/PeriodicidadEspecial/BloqueaAcceso/
/// adjunto) o si sigue el criterio global de <c>TipoDocumento.CuentaParaCumplimiento</c>.
/// <c>ToleranciaDias</c> es la personalización de este Centro (<c>null</c> = hereda) y
/// <c>ToleranciaHeredadaDias</c> la del Cliente empresarial titular del Centro (0 si no la fijó): lo que rige es
/// <see cref="ReglaBloqueoDeAcceso.ResolverToleranciaDias"/>.
/// Sustituye a ObtenerRequisitosDocumentalesDeCentroQuery (retirada junto con
/// RequisitoDocumental).
/// </summary>
public record ObtenerDocumentacionRequeridaDeCentroQuery(Guid CentroId) : IRequest<IReadOnlyList<DocumentacionRequeridaCentroDto>?>;

public record DocumentacionRequeridaCentroDto(
    Guid TipoDocumentoId,
    string Nombre,
    AmbitoAplicacion AmbitoAplicacion,
    bool EsObligatorioGlobal,
    bool Aplica,
    bool? Incluido,
    int? PeriodicidadEspecialMeses,
    bool BloqueaAcceso,
    string? ArchivoUrl,
    string? NombreArchivoOriginal,
    int? ToleranciaDias = null,
    int ToleranciaHeredadaDias = 0);

public class ObtenerDocumentacionRequeridaDeCentroQueryHandler(
    ICentrosQueryContext centrosContext,
    ITiposDocumentoQueryContext tiposDocumentoContext,
    IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerDocumentacionRequeridaDeCentroQuery, IReadOnlyList<DocumentacionRequeridaCentroDto>?>
{
    public async Task<IReadOnlyList<DocumentacionRequeridaCentroDto>?> Handle(
        ObtenerDocumentacionRequeridaDeCentroQuery request, CancellationToken cancellationToken)
    {
        if (!await alcanceDatos.CentroVisibleAsync(request.CentroId, cancellationToken))
            return null;

        var clienteEmpresarialId = await centrosContext.Centros
            .Where(c => c.Id == request.CentroId)
            .Select(c => (Guid?)c.ClienteId)
            .FirstOrDefaultAsync(cancellationToken);
        if (clienteEmpresarialId is null)
            return null;

        var toleranciasHeredadas = await tiposDocumentoContext.ToleranciasDocumentoClienteEmpresarial
            .Where(t => t.ClienteEmpresarialId == clienteEmpresarialId)
            .ToDictionaryAsync(t => t.TipoDocumentoId, t => t.ToleranciaDias, cancellationToken);

        var tipos = await DocumentacionRequeridaDeCentros.CargarCatalogoAsync(tiposDocumentoContext, cancellationToken);
        var filas = await DocumentacionRequeridaDeCentros.CargarFilasAsync(tiposDocumentoContext, [request.CentroId], cancellationToken);

        return tipos
            .Select(t =>
            {
                filas.TryGetValue((t.Id, request.CentroId), out var fila);
                return new DocumentacionRequeridaCentroDto(
                    t.Id, t.Nombre, t.AmbitoAplicacion, t.CuentaParaCumplimiento,
                    Aplica: DocumentacionRequeridaDeCentros.Aplica(filas, t, request.CentroId),
                    Incluido: fila?.Incluido,
                    PeriodicidadEspecialMeses: fila?.PeriodicidadEspecialMeses,
                    BloqueaAcceso: DocumentacionRequeridaDeCentros.BloqueaAcceso(filas, t.Id, request.CentroId),
                    ArchivoUrl: fila?.ArchivoUrl,
                    NombreArchivoOriginal: fila?.NombreArchivoOriginal,
                    ToleranciaDias: fila?.ToleranciaDias,
                    ToleranciaHeredadaDias: toleranciasHeredadas.TryGetValue(t.Id, out var heredada) ? heredada : 0);
            })
            .ToList();
    }
}
