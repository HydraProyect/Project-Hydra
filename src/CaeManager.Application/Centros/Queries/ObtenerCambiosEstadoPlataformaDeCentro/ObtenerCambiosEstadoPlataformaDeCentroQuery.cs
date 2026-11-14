using CaeManager.Application.Common;
using CaeManager.Application.Documentos;
using CaeManager.Application.Documentos.SituacionEnCentro;
using CaeManager.Application.Empresas;
using CaeManager.Application.Integraciones;
using CaeManager.Application.TiposDocumento;
using CaeManager.Application.Trabajadores;
using CaeManager.Application.Vehiculos;
using CaeManager.Domain.Documentos;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Centros.Queries.ObtenerCambiosEstadoPlataformaDeCentro;

/// <summary>
/// Sección de cambios del Historial de Centro 360: cada cambio de estado en la plataforma CAE del Cliente anotado a
/// mano en ese Centro (bloque 3 de la sesión del 21-10), desde Centro 360 o con «Lo he enviado» de la extensión
/// (<see cref="CambioEstadoPlataformaCentroDto.Origen"/>), el más reciente primero, deshechos incluidos y marcados.
/// </summary>
public record ObtenerCambiosEstadoPlataformaDeCentroQuery(Guid CentroId, int Maximo = 50)
    : IRequest<IReadOnlyList<CambioEstadoPlataformaCentroDto>>;

public record CambioEstadoPlataformaCentroDto(
    Guid Id, DateTime MomentoUtc, string DocumentoNombre, string? TitularNombre, string NombrePlataforma,
    EstadoAcreditacion EstadoAnterior, EstadoAcreditacion Estado, DateOnly Fecha, string? Motivo, string? Nota,
    string? UsuarioNombre, bool Deshecho,
    OrigenCambioEstadoAcreditacion Origen = OrigenCambioEstadoAcreditacion.Centro360);

/// <summary>
/// Mismo alcance que la tabla que los produce: el Centro tiene que ser visible para el usuario y, de cada cambio, solo
/// sale el de un documento cuyo titular (Trabajador, Empresa o Vehículo) también lo es. Lo que no, no aparece. RLS
/// acota además al Tenant operado. El alcance se aplica en la consulta, ANTES de cortar en <c>Maximo</c>: si no, 50 cambios
/// de titulares fuera de cartera dejarían el Historial vacío aunque hubiera otros visibles.
/// <para>La nota, el motivo y quién lo anotó solo salen a los roles que leen las reclamaciones (mismo criterio que
/// <c>SituacionDocumentosEnCentrosService</c>): el Usuario de Cliente del portal y Consulta ven el cambio sin ese detalle.
/// Es la regla de <b>trazabilidad interna</b> del rol Consulta (opción C, DECISION_LOG 2026-11-14, «Tanda 1 de decisiones
/// pendientes», punto 1), y el corte es sobre el <b>registro del cambio</b>: la nota, el motivo y el Gestor CAE autor de un
/// cambio de estado anotado a mano no se enseñan a Consulta en esta sección ni en las anotaciones de la situación del Centro.
/// No es un corte del dato: cuando el cambio anotado a mano es un rechazo, <c>AcreditacionDocumentoPlataforma.CambiarEstadoAMano</c>
/// guarda ese mismo motivo también en el historial de rechazos (causa «Otro», sin autor ni nota; esa tabla no distingue si el
/// rechazo lo devolvió la Plataforma CAE o se anotó a mano), y ese historial sí le llega a Consulta por la regla operativa
/// (<c>ObtenerCentroIdsParaGestionAsync</c>) como <c>UltimoMotivoRechazo</c>: <c>ObtenerDocumentosQuery</c> lo devuelve solo si la
/// acreditación está rechazada; <c>ObtenerAcreditacionesPorProveedorQuery</c> (pestaña Plataformas de Empresa 360, donde la
/// presentación lo filtra, y <c>GET /extension/acreditaciones-pendientes</c>, que no restringe por rol) lo devuelve sin
/// condicionarlo al estado actual de la acreditación (y de ella cuelga también la Bandeja del Gestor, que llega a Inicio, además de la pestaña
/// Plataforma de Documentos, entre otras vías: ver el comentario de esa lista). La vista rápida de un Documento
/// (<c>ObtenerAcreditacionesDelDocumentoQuery</c>) sí lo corta para Consulta, con la misma lista que esta sección. Son dos reglas
/// por tipo de dato y no se unifican, y el DECISION_LOG solo nombra esa vista rápida: el motivo de un rechazo anotado a mano sigue
/// siendo legible para Consulta por esas otras vías, sin corte decidido; la nota y el autor, no.</para>
/// </summary>
public class ObtenerCambiosEstadoPlataformaDeCentroQueryHandler(
    IDocumentosQueryContext documentosContext, ICentrosQueryContext centrosContext,
    ITiposDocumentoQueryContext tiposContext, ITrabajadoresQueryContext trabajadoresContext,
    IEmpresasQueryContext empresasContext, IVehiculosQueryContext vehiculosContext,
    IProveedoresPlataformaCaeQueryContext proveedoresContext, IAlcanceDatosService alcanceDatos,
    IDirectorioUsuariosService directorioUsuarios, ICurrentUserService currentUserService)
    : IRequestHandler<ObtenerCambiosEstadoPlataformaDeCentroQuery, IReadOnlyList<CambioEstadoPlataformaCentroDto>>
{
    public async Task<IReadOnlyList<CambioEstadoPlataformaCentroDto>> Handle(
        ObtenerCambiosEstadoPlataformaDeCentroQuery request, CancellationToken cancellationToken)
    {
        var centrosVisibles = await alcanceDatos.ObtenerCentroIdsVisiblesAsync(cancellationToken);
        if (centrosVisibles is not null && !centrosVisibles.Contains(request.CentroId))
            return [];

        var trabajadoresVisibles = await alcanceDatos.ObtenerTrabajadorIdsVisiblesAsync(cancellationToken);
        var empresasVisibles = await alcanceDatos.ObtenerEmpresaIdsVisiblesAsync(cancellationToken);
        var vehiculosVisibles = await alcanceDatos.ObtenerVehiculoIdsVisiblesAsync(cancellationToken);

        var visibles = await (
            from cambio in documentosContext.CambiosEstadoAcreditacionPlataforma
            join acreditacion in documentosContext.AcreditacionesDocumentoPlataforma on cambio.AcreditacionId equals acreditacion.Id
            join canal in centrosContext.CanalesGestionDocumental on acreditacion.CanalGestionDocumentalId equals canal.Id
            join documento in documentosContext.Documentos on acreditacion.DocumentoId equals documento.Id
            where canal.CentroId == request.CentroId
                && ((documento.TrabajadorId != null
                        && (trabajadoresVisibles == null || trabajadoresVisibles.Contains(documento.TrabajadorId!.Value)))
                    || (documento.EmpresaId != null
                        && (empresasVisibles == null || empresasVisibles.Contains(documento.EmpresaId!.Value)))
                    || (documento.VehiculoId != null
                        && (vehiculosVisibles == null || vehiculosVisibles.Contains(documento.VehiculoId!.Value))))
            orderby cambio.MomentoUtc descending
            select new
            {
                cambio.Id,
                cambio.MomentoUtc,
                cambio.EstadoAnterior,
                cambio.Estado,
                cambio.Fecha,
                cambio.Motivo,
                cambio.Nota,
                cambio.UsuarioId,
                cambio.DeshechoUtc,
                cambio.Origen,
                canal.ProveedorPlataformaCaeId,
                documento.TipoDocumentoId,
                documento.TrabajadorId,
                documento.EmpresaId,
                documento.VehiculoId
            })
            .Take(Math.Clamp(request.Maximo, 1, 200))
            .ToListAsync(cancellationToken);
        if (visibles.Count == 0) return [];

        var leeDetalle = await currentUserService.ObtenerRolEfectivoAsync() is { } rol
            && SituacionDocumentosEnCentrosService.RolesQueLeenReclamaciones.Contains(rol);

        var tipoIds = visibles.Select(c => c.TipoDocumentoId).Distinct().ToList();
        var tipos = await tiposContext.TiposDocumento.Where(t => tipoIds.Contains(t.Id))
            .ToDictionaryAsync(t => t.Id, t => t.Nombre, cancellationToken);
        var trabajadorIds = visibles.Where(c => c.TrabajadorId is not null).Select(c => c.TrabajadorId!.Value).Distinct().ToList();
        var trabajadores = await trabajadoresContext.Trabajadores.Where(t => trabajadorIds.Contains(t.Id))
            .Select(t => new { t.Id, t.Nombre, t.Apellidos })
            .ToDictionaryAsync(t => t.Id, t => $"{t.Nombre} {t.Apellidos}".Trim(), cancellationToken);
        var empresaIds = visibles.Where(c => c.EmpresaId is not null).Select(c => c.EmpresaId!.Value).Distinct().ToList();
        var empresas = await empresasContext.Empresas.Where(e => empresaIds.Contains(e.Id))
            .ToDictionaryAsync(e => e.Id, e => e.RazonSocial, cancellationToken);
        var vehiculoIds = visibles.Where(c => c.VehiculoId is not null).Select(c => c.VehiculoId!.Value).Distinct().ToList();
        var vehiculos = await vehiculosContext.Vehiculos.Where(v => vehiculoIds.Contains(v.Id))
            .ToDictionaryAsync(v => v.Id, v => v.Nombre, cancellationToken);
        var proveedorIds = visibles.Where(c => c.ProveedorPlataformaCaeId is not null).Select(c => c.ProveedorPlataformaCaeId!.Value).Distinct().ToList();
        var proveedores = await proveedoresContext.ProveedoresPlataformaCae.Where(p => proveedorIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, p => p.Nombre, cancellationToken);
        var usuarioIds = !leeDetalle ? [] : visibles.Where(c => c.UsuarioId is not null).Select(c => c.UsuarioId!.Value).Distinct().ToList();
        var nombres = usuarioIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await directorioUsuarios.ObtenerNombresVisiblesAsync(usuarioIds, cancellationToken);

        return visibles.Select(c => new CambioEstadoPlataformaCentroDto(
            c.Id, c.MomentoUtc,
            tipos.GetValueOrDefault(c.TipoDocumentoId, "Documento"),
            c.TrabajadorId is { } t ? trabajadores.GetValueOrDefault(t)
                : c.EmpresaId is { } e ? empresas.GetValueOrDefault(e)
                : c.VehiculoId is { } v ? vehiculos.GetValueOrDefault(v) : null,
            c.ProveedorPlataformaCaeId is { } p ? proveedores.GetValueOrDefault(p, "Plataforma") : "Plataforma",
            c.EstadoAnterior, c.Estado, c.Fecha, leeDetalle ? c.Motivo : null, leeDetalle ? c.Nota : null,
            leeDetalle && c.UsuarioId is { } u ? nombres.GetValueOrDefault(u) : null,
            c.DeshechoUtc is not null, c.Origen)).ToList();
    }
}
