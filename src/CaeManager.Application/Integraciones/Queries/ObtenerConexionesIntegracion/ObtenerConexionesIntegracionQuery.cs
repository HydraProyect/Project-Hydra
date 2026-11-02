using CaeManager.Application.Common;
using CaeManager.Application.Empresas;
using CaeManager.Domain.Integraciones;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Integraciones.Queries.ObtenerConexionesIntegracion;

/// <param name="SoloPropiasYGenerales">
/// Cuando es true, excluye los buzones personales (<c>GestorPropietarioId</c>
/// no nulo) de OTROS gestores — sin este filtro, cualquier usuario con
/// acceso a Comunicaciones podía ver, y desde <c>Buzon.razor</c> incluso
/// enviar correo desde, el buzón personal de otro gestor cualquiera
/// (hueco de privacidad real, ver hydra-buzon-personal-rediseno-pendiente).
/// La pantalla de administración de Conexiones (rol Administrador, gestiona
/// TODAS las conexiones del tenant a propósito) sigue llamando con el valor
/// por defecto, sin restringir.
/// </param>
public record ObtenerConexionesIntegracionQuery(bool SoloPropiasYGenerales = false)
    : IRequest<IReadOnlyList<ConexionIntegracionListaDto>>;

public record ConexionIntegracionListaDto(
    Guid Id, string BuzonEmail, string Nombre, Guid? ClienteId, string? ClienteNombre, EstadoConexionIntegracion Estado,
    DateTime FechaConectadaUtc, Guid? GestorPropietarioId, string? UltimoError, Guid? ResponsableUsuarioId = null);

/// <remarks>
/// <c>UltimoError</c> se lee en una consulta aparte y solo fuera de Sesión Privilegiada: es el texto que
/// devuelve el proveedor al fallar, y <c>cae_app_soporte</c> no tiene <c>SELECT</c> sobre esa columna
/// (migración <c>SoporteSinColumnasSecretasRestantes</c>; OWASP ASVS 4.0.3 V4.1.3). Con una sola consulta,
/// Soporte TALVEG no vería el listado. La señal es la coordenada en bruto
/// (<c>IClienteActivoSeleccionado.SesionPrivilegiadaIdSeleccionada</c>) y no <c>ISesionPrivilegiadaActual</c>:
/// es con la que <c>TenantRlsConnectionInterceptor</c> adopta el rol, también con la sesión ya caducada.
/// </remarks>
public class ObtenerConexionesIntegracionQueryHandler(
    IIntegracionesQueryContext integracionesContext, IEmpresasQueryContext empresasContext,
    IAlcanceDatosService alcanceDatos, ICurrentUserService currentUserService,
    IClienteActivoSeleccionado clienteActivoSeleccionado)
    : IRequestHandler<ObtenerConexionesIntegracionQuery, IReadOnlyList<ConexionIntegracionListaDto>>
{
    public async Task<IReadOnlyList<ConexionIntegracionListaDto>> Handle(
        ObtenerConexionesIntegracionQuery request, CancellationToken cancellationToken)
    {
        var clienteIdsVisibles = await alcanceDatos.ObtenerClienteIdsVisiblesAsync(cancellationToken);

        var usuarioActualId = request.SoloPropiasYGenerales
            ? await currentUserService.ObtenerUsuarioActualIdAsync()
            : null;

        var query =
            from conexion in integracionesContext.ConexionesIntegracion
            // Solo buzones M365: las líneas WhatsApp tienen su propia tabla en
            // la misma pantalla (ObtenerLineasWhatsAppQuery) y sin este filtro
            // aparecían duplicadas como "buzón".
            where conexion.Proveedor == ProveedorIntegracion.Microsoft365
            where clienteIdsVisibles == null || conexion.ClienteId == null || clienteIdsVisibles.Contains(conexion.ClienteId!.Value)
            where !request.SoloPropiasYGenerales || conexion.GestorPropietarioId == null || conexion.GestorPropietarioId == usuarioActualId
            join cliente in empresasContext.Empresas on conexion.ClienteId equals cliente.Id into clientesUnidos
            from cliente in clientesUnidos.DefaultIfEmpty()
            orderby conexion.FechaConectadaUtc descending
            select new ConexionIntegracionListaDto(
                conexion.Id, conexion.BuzonEmail, conexion.Nombre, conexion.ClienteId, cliente!.RazonSocial, conexion.Estado,
                conexion.FechaConectadaUtc, conexion.GestorPropietarioId, null, conexion.ResponsableUsuarioId);

        var conexiones = await query.ToListAsync(cancellationToken);
        if (conexiones.Count == 0 || clienteActivoSeleccionado.SesionPrivilegiadaIdSeleccionada is not null)
            return conexiones;

        var ids = conexiones.Select(c => c.Id).ToList();
        var errores = await UltimosErroresAsync(integracionesContext, ids, cancellationToken);
        return conexiones
            .Select(c => errores.TryGetValue(c.Id, out var error) ? c with { UltimoError = error } : c)
            .ToList();
    }

    /// <summary>
    /// El último error de cada conexión que lo tenga. Fuera de Sesión Privilegiada: bajo
    /// <c>cae_app_soporte</c> esta lectura da 42501.
    /// </summary>
    internal static Task<Dictionary<Guid, string>> UltimosErroresAsync(
        IIntegracionesQueryContext integracionesContext, IReadOnlyCollection<Guid> conexionIds,
        CancellationToken cancellationToken) =>
        integracionesContext.ConexionesIntegracion
            .Where(c => conexionIds.Contains(c.Id) && c.UltimoError != null)
            .Select(c => new { c.Id, UltimoError = c.UltimoError! })
            .ToDictionaryAsync(c => c.Id, c => c.UltimoError, cancellationToken);
}
