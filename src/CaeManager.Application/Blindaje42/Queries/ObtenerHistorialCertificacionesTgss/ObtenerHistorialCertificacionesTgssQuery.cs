using CaeManager.Application.Common;
using CaeManager.Domain.Blindaje42;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Blindaje42.Queries.ObtenerHistorialCertificacionesTgss;

/// <summary>Historial completo de solicitudes de certificación TGSS de una Empresa concreta ante un Cliente concreto — la vista del drawer.</summary>
public record ObtenerHistorialCertificacionesTgssQuery(Guid EmpresaId, Guid ClienteId)
    : IRequest<IReadOnlyList<SolicitudCertificacionTgssDto>>;

public record SolicitudCertificacionTgssDto(
    Guid Id,
    DateOnly FechaSolicitud,
    DateOnly FechaLimiteOrientativa,
    ResultadoCertificacionTgss? Resultado,
    DateOnly? FechaRespuesta,
    EstadoBlindaje42 Estado,
    string? Observaciones,
    bool TieneEvidencia,
    string? EvidenciaNombreArchivo);

public class ObtenerHistorialCertificacionesTgssQueryHandler(
    IBlindaje42QueryContext blindajeContext, IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerHistorialCertificacionesTgssQuery, IReadOnlyList<SolicitudCertificacionTgssDto>>
{
    public async Task<IReadOnlyList<SolicitudCertificacionTgssDto>> Handle(
        ObtenerHistorialCertificacionesTgssQuery request, CancellationToken cancellationToken)
    {
        // Alcance de LECTURA aquí es correcto (REC-149, se queda): el doble
        // gate por ClienteId Y EmpresaId ya acota a la relación propia — para
        // el rol Cliente, ClienteVisibleAsync solo deja pasar su propio
        // ClienteId, así que solo puede pedir el historial de certificación
        // de SUS contratistas ante SÍ MISMO, nunca el de otro Cliente. Eso es
        // documentación de cumplimiento en la relación con el propio
        // Cliente — el objeto mismo del portal — no información ajena de la
        // contratista.
        if (!await alcanceDatos.ClienteVisibleAsync(request.ClienteId, cancellationToken) ||
            !await alcanceDatos.EmpresaVisibleAsync(request.EmpresaId, cancellationToken))
            return [];

        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);

        var solicitudes = await blindajeContext.SolicitudesCertificacionTgss
            .Where(s => s.EmpresaId == request.EmpresaId && s.ClienteId == request.ClienteId)
            .OrderByDescending(s => s.FechaSolicitud)
            .ThenByDescending(s => s.CreadoEnUtc)
            .Select(s => new
            {
                s.Id,
                s.FechaSolicitud,
                s.Resultado,
                s.FechaRespuesta,
                s.Observaciones,
                s.EvidenciaArchivoRuta,
                s.EvidenciaNombreArchivo
            })
            .ToListAsync(cancellationToken);

        return solicitudes.Select(s => new SolicitudCertificacionTgssDto(
            s.Id,
            s.FechaSolicitud,
            s.FechaSolicitud.AddDays(SolicitudCertificacionTgss.PlazoDiasTgss),
            s.Resultado,
            s.FechaRespuesta,
            CalculadoraEstadoBlindaje42.Calcular(s.Resultado, s.FechaSolicitud, hoy),
            s.Observaciones,
            s.EvidenciaArchivoRuta is not null,
            s.EvidenciaNombreArchivo))
            .ToList();
    }
}
