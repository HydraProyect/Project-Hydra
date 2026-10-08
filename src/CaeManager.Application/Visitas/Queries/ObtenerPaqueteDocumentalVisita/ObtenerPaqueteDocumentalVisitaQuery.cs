using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.Plataforma;
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
/// <b>Además del alcance, el rol</b> (decisión del propietario, 2026-10-08): el zip entrega
/// documentos, también los sensibles, y sale de TALVEG —descargado o adjunto a un correo—,
/// así que un usuario de negocio solo lo obtiene con un rol que escribe. Consulta lee la
/// solicitud y la copia (<see cref="ObtenerSolicitudAccesoCorreoQueryHandler"/> no cambia),
/// pero ni descarga el zip ni prepara su envío: «Descargar» y «Enviar por correo» pasan los
/// dos por esta consulta. Lista blanca, como <see cref="AutorizacionEscrituraBehavior{TRequest,TResponse}"/>:
/// Cliente y un rol sin resolver tampoco pasan. Se comprueba después del alcance, para que
/// fuera de él la respuesta siga siendo la de una Visita inexistente. Una Sesión Privilegiada
/// de plataforma queda como estaba: su rol efectivo es <c>null</c> y lo que ve lo decide su
/// alcance, no esta regla.
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
    IRegistroAccesoDocumentoSensibleService registroAcceso,
    ICurrentUserService currentUserService,
    ISesionPrivilegiadaActual sesionPrivilegiadaActual)
    : IRequestHandler<ObtenerPaqueteDocumentalVisitaQuery, Result<PaqueteDocumentalDescargaDto>>
{
    public static readonly Error SinDocumentos = Error.Crear(
        "PaqueteDocumental.SinDocumentos",
        "No hay documentación vigente que descargar para esta visita.");

    public static readonly Error RolSinDescarga = Error.Crear(
        "PaqueteDocumental.RolSinDescarga",
        "Tu rol no permite descargar ni enviar la documentación de una visita — solo consultarla.");

    // Los mismos literales que AutorizacionEscrituraBehavior, por el mismo motivo:
    // Application no referencia Infrastructure.Identity.Roles.
    private static readonly string[] RolesQueObtienenElPaquete =
        ["Administrador", "DireccionCae", "CoordinadorCae", "GestorCae"];

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

        if (await sesionPrivilegiadaActual.ObtenerAsync(cancellationToken) is null
            && (await currentUserService.ObtenerRolEfectivoAsync() is not { } rol || !RolesQueObtienenElPaquete.Contains(rol)))
            return Result.Fallo<PaqueteDocumentalDescargaDto>(RolSinDescarga);

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
