using CaeManager.Application.Common;
using CaeManager.Application.Documentos;
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
/// mano desde ese Centro (bloque 3 de la sesión del 21-10), el más reciente primero, deshechos incluidos y marcados.
/// </summary>
public record ObtenerCambiosEstadoPlataformaDeCentroQuery(Guid CentroId, int Maximo = 50)
    : IRequest<IReadOnlyList<CambioEstadoPlataformaCentroDto>>;

public record CambioEstadoPlataformaCentroDto(
    Guid Id, DateTime MomentoUtc, string DocumentoNombre, string? TitularNombre, string NombrePlataforma,
    EstadoAcreditacion EstadoAnterior, EstadoAcreditacion Estado, DateOnly Fecha, string? Motivo, string? Nota,
    string? UsuarioNombre, bool Deshecho);

/// <summary>
/// Mismo alcance que la tabla que los produce: el Centro tiene que ser visible para el usuario y, de cada cambio, solo
/// sale el de un documento cuyo titular (Trabajador, Empresa o Vehículo) también lo es. Lo que no, no aparece. RLS
/// acota además al Tenant operado.
/// </summary>
public class ObtenerCambiosEstadoPlataformaDeCentroQueryHandler(
    IDocumentosQueryContext documentosContext, ICentrosQueryContext centrosContext,
    ITiposDocumentoQueryContext tiposContext, ITrabajadoresQueryContext trabajadoresContext,
    IEmpresasQueryContext empresasContext, IVehiculosQueryContext vehiculosContext,
    IProveedoresPlataformaCaeQueryContext proveedoresContext, IAlcanceDatosService alcanceDatos,
    IDirectorioUsuariosService directorioUsuarios)
    : IRequestHandler<ObtenerCambiosEstadoPlataformaDeCentroQuery, IReadOnlyList<CambioEstadoPlataformaCentroDto>>
{
    public async Task<IReadOnlyList<CambioEstadoPlataformaCentroDto>> Handle(
        ObtenerCambiosEstadoPlataformaDeCentroQuery request, CancellationToken cancellationToken)
    {
        var centrosVisibles = await alcanceDatos.ObtenerCentroIdsVisiblesAsync(cancellationToken);
        if (centrosVisibles is not null && !centrosVisibles.Contains(request.CentroId))
            return [];

        var crudos = await (
            from cambio in documentosContext.CambiosEstadoAcreditacionPlataforma
            join acreditacion in documentosContext.AcreditacionesDocumentoPlataforma on cambio.AcreditacionId equals acreditacion.Id
            join canal in centrosContext.CanalesGestionDocumental on acreditacion.CanalGestionDocumentalId equals canal.Id
            join documento in documentosContext.Documentos on acreditacion.DocumentoId equals documento.Id
            where canal.CentroId == request.CentroId
            orderby cambio.MomentoUtc descending
            select new
            {
                cambio.Id, cambio.MomentoUtc, cambio.EstadoAnterior, cambio.Estado, cambio.Fecha, cambio.Motivo, cambio.Nota,
                cambio.UsuarioId, cambio.DeshechoUtc, canal.ProveedorPlataformaCaeId,
                documento.TipoDocumentoId, documento.TrabajadorId, documento.EmpresaId, documento.VehiculoId
            })
            .Take(Math.Clamp(request.Maximo, 1, 200))
            .ToListAsync(cancellationToken);
        if (crudos.Count == 0) return [];

        var trabajadoresVisibles = await alcanceDatos.ObtenerTrabajadorIdsVisiblesAsync(cancellationToken);
        var empresasVisibles = await alcanceDatos.ObtenerEmpresaIdsVisiblesAsync(cancellationToken);
        var vehiculosVisibles = await alcanceDatos.ObtenerVehiculoIdsVisiblesAsync(cancellationToken);
        static bool Visible(Guid? id, IReadOnlyList<Guid>? visibles) => id is { } valor && (visibles is null || visibles.Contains(valor));
        var visibles = crudos.Where(c =>
            Visible(c.TrabajadorId, trabajadoresVisibles) || Visible(c.EmpresaId, empresasVisibles) || Visible(c.VehiculoId, vehiculosVisibles))
            .ToList();
        if (visibles.Count == 0) return [];

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
        var usuarioIds = visibles.Where(c => c.UsuarioId is not null).Select(c => c.UsuarioId!.Value).Distinct().ToList();
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
            c.EstadoAnterior, c.Estado, c.Fecha, c.Motivo, c.Nota,
            c.UsuarioId is { } u ? nombres.GetValueOrDefault(u) : null,
            c.DeshechoUtc is not null)).ToList();
    }
}
