using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.Visitas.GestionPorCorreo;
using CaeManager.Application.Visitas.PaqueteDocumental;
using CaeManager.Application.Visitas.Queries.ObtenerSolicitudAccesoCorreo;
using CaeManager.Domain.Auditoria;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Visitas.Queries.ObtenerPaqueteDocumentalVisita;

/// <summary>
/// Zip con la documentación de una Visita para descargarlo y adjuntarlo a mano al
/// correo de solicitud de acceso (P1-X1). Misma selección que el paquete que se adjunta
/// a una conversación de buzón (<see cref="IPaqueteDocumentalVisitaService"/>): solo
/// vigentes, uno por titular y tipo, el más reciente, nunca vencidos.
///
/// <para>
/// Solo para una Visita a un Centro que requiere gestión CAE y se gestiona por correo
/// (<see cref="CanalCorreoDeCentro"/>). Autorización idéntica a la del texto del correo
/// (<see cref="ObtenerSolicitudAccesoCorreoQueryHandler"/>): alcance de <b>gestión</b>
/// sobre el Centro, como <c>CrearVisita</c> (#889); fuera de alcance, lo mismo que una
/// Visita inexistente. Los Trabajadores no se acotan a la cartera —mismo criterio que
/// <c>CrearVisita</c>—; el Tenant propietario lo imponen el filtro de consulta y RLS.
/// </para>
///
/// <para>
/// DEC-36: el zip entrega el contenido de cada Documento que contiene, así que cada uno
/// cuenta como una <see cref="TipoAccesoDocumentoSensible.Apertura"/> y se registra si es
/// sensible — después de construir el zip, como el endpoint de un Documento suelto
/// registra después de abrir el archivo: un documento cuyo archivo no se pudo abrir no
/// entró y no deja registro.
/// </para>
/// </summary>
public record ObtenerPaqueteDocumentalVisitaQuery(Guid VisitaId) : IRequest<Result<PaqueteDocumentalDescargaDto>>;

public record PaqueteDocumentalDescargaDto(string NombreArchivo, byte[] Contenido);

public class ObtenerPaqueteDocumentalVisitaQueryHandler(
    IVisitasQueryContext visitasContext,
    ICentrosQueryContext centrosContext,
    IAlcanceDatosService alcanceDatos,
    IPaqueteDocumentalVisitaService paqueteDocumental,
    IRegistroAccesoDocumentoSensibleService registroAcceso)
    : IRequestHandler<ObtenerPaqueteDocumentalVisitaQuery, Result<PaqueteDocumentalDescargaDto>>
{
    public static readonly Error SinDocumentos = Error.Crear(
        "PaqueteDocumental.SinDocumentos",
        "No hay documentación vigente que descargar para esta visita.");

    public async Task<Result<PaqueteDocumentalDescargaDto>> Handle(ObtenerPaqueteDocumentalVisitaQuery request, CancellationToken cancellationToken)
    {
        var visita = await (
            from v in visitasContext.Visitas
            join centro in centrosContext.Centros on v.CentroId equals centro.Id
            where v.Id == request.VisitaId
            select new { CentroId = centro.Id, centro.GestionCae, v.EstaCancelada })
            .FirstOrDefaultAsync(cancellationToken);

        if (visita is null || !await alcanceDatos.CentroParaGestionVisibleAsync(visita.CentroId, cancellationToken))
            return Result.Fallo<PaqueteDocumentalDescargaDto>(ObtenerSolicitudAccesoCorreoQueryHandler.NoEncontrada);

        if (visita.EstaCancelada)
            return Result.Fallo<PaqueteDocumentalDescargaDto>(ObtenerSolicitudAccesoCorreoQueryHandler.VisitaCancelada);

        if (visita.GestionCae == ModalidadGestionCae.SinGestionCae)
            return Result.Fallo<PaqueteDocumentalDescargaDto>(ObtenerSolicitudAccesoCorreoQueryHandler.CentroSinGestionCae);

        if (await CanalCorreoDeCentro.ResolverAsync(centrosContext, visita.CentroId, cancellationToken) is null)
            return Result.Fallo<PaqueteDocumentalDescargaDto>(ObtenerSolicitudAccesoCorreoQueryHandler.CentroNoGestionadoPorCorreo);

        var paquete = await paqueteDocumental.ConstruirAsync(request.VisitaId, cancellationToken);
        if (paquete is null)
            return Result.Fallo<PaqueteDocumentalDescargaDto>(SinDocumentos);

        foreach (var documento in paquete.Documentos)
            await registroAcceso.RegistrarSiSensibleAsync(documento.DocumentoId, TipoAccesoDocumentoSensible.Apertura, cancellationToken);

        return Result.Exito(new PaqueteDocumentalDescargaDto(paquete.NombreArchivo, paquete.Contenido));
    }
}
