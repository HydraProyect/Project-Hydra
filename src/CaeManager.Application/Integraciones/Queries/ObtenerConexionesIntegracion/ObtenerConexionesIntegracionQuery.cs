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
    DateTime FechaConectadaUtc, Guid? GestorPropietarioId, string? UltimoError);

public class ObtenerConexionesIntegracionQueryHandler(
    IIntegracionesQueryContext integracionesContext, IEmpresasQueryContext empresasContext,
    IAlcanceDatosService alcanceDatos, ICurrentUserService currentUserService)
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
                conexion.FechaConectadaUtc, conexion.GestorPropietarioId, conexion.UltimoError);

        return await query.ToListAsync(cancellationToken);
    }
}
