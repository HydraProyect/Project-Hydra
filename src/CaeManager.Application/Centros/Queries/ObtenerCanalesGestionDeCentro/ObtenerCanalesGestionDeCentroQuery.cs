using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.Integraciones;
using CaeManager.Domain.Centros;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Centros.Queries.ObtenerCanalesGestionDeCentro;

/// <summary>
/// Respalda la pestaña "Plataforma" del Context Workspace y el bloque "Plataforma CAE" de la vista rápida de una Gestión. Devuelve los N
/// canales del Centro (Project-Hydra-Negocio/tecnico/docs/ux-audit/PLAN-EJECUCION-UX.md § 0.6, Lote 0-E — antes era uno
/// solo), con el principal primero.
///
/// Deliberadamente NO proyecta Usuario/Contrasena (dato cifrado en reposo, ver
/// Project-Hydra-Negocio/tecnico/ARCHITECTURE.md — "el acceso a verlas está restringido por policy y queda
/// registrado en auditoría como acceso a dato sensible") — solo expone <see cref="CanalGestionResumenDto.TieneCredenciales"/>.
/// La lectura de Usuario/Contrasena vive aparte, en
/// <see cref="CaeManager.Application.Centros.Queries.ObtenerCredencialCanalGestion.ObtenerCredencialCanalGestionQuery"/>
/// (solo tras un clic explícito, con alcance de gestión, y cada lectura queda en la
/// auditoría como <c>AccesoDatoSensible</c> — decisión del propietario del 2026-09-23).
///
/// <see cref="CanalGestionResumenDto.UrlAcceso"/> y <see cref="CanalGestionResumenDto.Notas"/> solo viajan a quien gestiona
/// el Centro (<see cref="AlcanceDatosServiceExtensions.CentroParaGestionVisibleAsync"/>): el rol Cliente ve los Centros de su
/// Cliente empresarial y sus canales, pero recibe ambos campos a <c>null</c>, así que no se le ofrece «Abrir portal». Es el
/// mismo corte que la dirección del portal en <c>ObtenerAcreditacionesDelDocumentoQuery</c> y <c>ObtenerDocumentosQuery</c>
/// (decisión de Chris del 2026-11-12, opción B, para la dirección; las notas internas del canal siguen el mismo corte). Editar el canal exige el mismo alcance de gestión
/// (<c>EditarCanalGestionCommand</c>), así que nadie precarga el formulario de edición con estos campos vacíos y los guarda.
///
/// <see cref="CanalGestionResumenDto.ProveedorPlataformaCaeNombre"/> es
/// <c>null</c> cuando <see cref="CanalGestionResumenDto.ProveedorPlataformaCaeId"/>
/// también lo es — canal migrado desde el antiguo texto libre sin match
/// automático (Lote 2-B), pendiente de resolver en la próxima edición.
/// </summary>
public record ObtenerCanalesGestionDeCentroQuery(Guid CentroId) : IRequest<IReadOnlyList<CanalGestionResumenDto>>;

public record CanalGestionResumenDto(
    Guid Id,
    TipoCanalGestion Tipo,
    string EtiquetaProposito,
    bool EsPrincipal,
    Guid? ProveedorPlataformaCaeId,
    string? ProveedorPlataformaCaeNombre,
    string? UrlAcceso,
    string? EmailsDestinatarios,
    string? NombreContacto,
    string? Notas,
    bool TieneCredenciales,
    Guid Version);

public class ObtenerCanalesGestionDeCentroQueryHandler(
    ICentrosQueryContext dbContext, IProveedoresPlataformaCaeQueryContext proveedoresContext, IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerCanalesGestionDeCentroQuery, IReadOnlyList<CanalGestionResumenDto>>
{
    public async Task<IReadOnlyList<CanalGestionResumenDto>> Handle(
        ObtenerCanalesGestionDeCentroQuery request, CancellationToken cancellationToken)
    {
        if (!await alcanceDatos.CentroVisibleAsync(request.CentroId, cancellationToken))
            return [];

        // La dirección del portal y las notas, solo para quien gestiona el Centro: lista vacía para el rol Cliente (fallo
        // cerrado). Se corta aquí y no en la pantalla, porque el mismo DTO lo pintan Centro 360, el Context Workspace y la
        // vista rápida de la Gestión.
        var gestiona = await alcanceDatos.CentroParaGestionVisibleAsync(request.CentroId, cancellationToken);

        var canales = await dbContext.CanalesGestionDocumental
            .Where(c => c.CentroId == request.CentroId)
            .OrderByDescending(c => c.EsPrincipal)
            .ThenBy(c => c.EtiquetaProposito)
            .Select(c => new
            {
                c.Id,
                c.Tipo,
                c.EtiquetaProposito,
                c.EsPrincipal,
                c.ProveedorPlataformaCaeId,
                c.UrlAcceso,
                c.EmailsDestinatarios,
                c.NombreContacto,
                c.Notas,
                TieneCredenciales = c.Usuario != null,
                c.Version
            })
            .ToListAsync(cancellationToken);

        var proveedorIds = canales.Where(c => c.ProveedorPlataformaCaeId.HasValue)
            .Select(c => c.ProveedorPlataformaCaeId!.Value).ToHashSet();

        var nombresPorProveedor = await proveedoresContext.ProveedoresPlataformaCae
            .Where(p => proveedorIds.Contains(p.Id))
            .Select(p => new { p.Id, p.Nombre })
            .ToDictionaryAsync(p => p.Id, p => p.Nombre, cancellationToken);

        return canales
            .Select(c => new CanalGestionResumenDto(
                c.Id, c.Tipo, c.EtiquetaProposito, c.EsPrincipal, c.ProveedorPlataformaCaeId,
                c.ProveedorPlataformaCaeId.HasValue ? nombresPorProveedor.GetValueOrDefault(c.ProveedorPlataformaCaeId.Value) : null,
                gestiona ? c.UrlAcceso : null, c.EmailsDestinatarios, c.NombreContacto, gestiona ? c.Notas : null,
                c.TieneCredenciales, c.Version))
            .ToList();
    }
}
