using CaeManager.Application.Common;
using CaeManager.Domain.Documentos;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.TiposDocumento.Queries.ObtenerToleranciasClienteEmpresarial;

/// <summary>
/// La tolerancia por defecto de un Cliente empresarial para cada Tipo de documento que la regla de acceso evalúa (Trabajador y
/// Empresa): los días que el documento sigue valiendo para acceder a los Centros de ese Cliente empresarial tras vencer. Cada
/// Centro puede personalizar la suya; esta es la que heredan los demás. <see cref="ToleranciaDias"/> es 0 cuando no hay fila
/// (sin fila, la tolerancia es 0). Lo escribe <c>EstablecerToleranciaClienteEmpresarialCommand</c>.
/// </summary>
public record ObtenerToleranciasClienteEmpresarialQuery(Guid ClienteEmpresarialId) : IRequest<IReadOnlyList<ToleranciaTipoDocumentoDto>>;

public record ToleranciaTipoDocumentoDto(Guid TipoDocumentoId, string Nombre, AmbitoAplicacion Ambito, int ToleranciaDias);

public class ObtenerToleranciasClienteEmpresarialQueryHandler(ITiposDocumentoQueryContext dbContext, IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerToleranciasClienteEmpresarialQuery, IReadOnlyList<ToleranciaTipoDocumentoDto>>
{
    public async Task<IReadOnlyList<ToleranciaTipoDocumentoDto>> Handle(
        ObtenerToleranciasClienteEmpresarialQuery request, CancellationToken cancellationToken)
    {
        // Mismo control de alcance que el comando que escribe: un Cliente empresarial fuera de la cartera no se enseña.
        if (!await alcanceDatos.ClienteVisibleAsync(request.ClienteEmpresarialId, cancellationToken)) return [];

        // Qué ámbitos evalúa la regla de acceso lo decide la propia regla, no una lista copiada aquí.
        var todos = await dbContext.TiposDocumento
            .OrderBy(t => t.Orden)
            .Select(t => new { t.Id, t.Nombre, t.AmbitoAplicacion })
            .ToListAsync(cancellationToken);
        var tipos = todos.Where(t => ReglaBloqueoDeAcceso.AmbitoPuedeBloquear(t.AmbitoAplicacion)).ToList();

        var tolerancias = await dbContext.ToleranciasDocumentoClienteEmpresarial
            .Where(t => t.ClienteEmpresarialId == request.ClienteEmpresarialId)
            .ToDictionaryAsync(t => t.TipoDocumentoId, t => t.ToleranciaDias, cancellationToken);

        return tipos
            .Select(t => new ToleranciaTipoDocumentoDto(t.Id, t.Nombre, t.AmbitoAplicacion, tolerancias.GetValueOrDefault(t.Id)))
            .ToList();
    }
}
