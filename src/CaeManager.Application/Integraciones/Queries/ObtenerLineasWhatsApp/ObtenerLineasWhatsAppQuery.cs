using CaeManager.Application.Common;
using CaeManager.Application.Empresas;
using CaeManager.Application.Integraciones.Queries.ObtenerConexionesIntegracion;
using CaeManager.Domain.Integraciones;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Integraciones.Queries.ObtenerLineasWhatsApp;

public record ObtenerLineasWhatsAppQuery : IRequest<IReadOnlyList<LineaWhatsAppListaDto>>;

/// <summary>Sin TokenAcceso a propósito: el token nunca sale hacia la UI — solo se escribe (alta/rotación).</summary>
public record LineaWhatsAppListaDto(
    Guid LineaId,
    Guid ConexionId,
    Guid Version,
    string Nombre,
    string NumeroTelefono,
    string PhoneNumberId,
    string WabaId,
    ModoAsignacionLinea Modo,
    Guid? ComercialAsignadoId,
    IReadOnlyList<Guid> MiembrosPool,
    Guid? ClienteId,
    string? ClienteNombre,
    string? MensajeAutoTriage,
    EstadoConexionIntegracion Estado,
    string? UltimoError);

/// <remarks>
/// <c>UltimoError</c> se lee aparte y solo fuera de Sesión Privilegiada, como en
/// <see cref="ObtenerConexionesIntegracionQueryHandler"/>: <c>cae_app_soporte</c> no tiene <c>SELECT</c> sobre
/// esa columna.
/// </remarks>
public class ObtenerLineasWhatsAppQueryHandler(
    IIntegracionesQueryContext integracionesContext, IEmpresasQueryContext empresasContext,
    IClienteActivoSeleccionado clienteActivoSeleccionado)
    : IRequestHandler<ObtenerLineasWhatsAppQuery, IReadOnlyList<LineaWhatsAppListaDto>>
{
    public async Task<IReadOnlyList<LineaWhatsAppListaDto>> Handle(
        ObtenerLineasWhatsAppQuery request, CancellationToken cancellationToken)
    {
        var filas = await (
            from linea in integracionesContext.LineasWhatsApp
            join conexion in integracionesContext.ConexionesIntegracion on linea.ConexionIntegracionId equals conexion.Id
            join cliente in empresasContext.Empresas on conexion.ClienteId equals cliente.Id into clientesUnidos
            from cliente in clientesUnidos.DefaultIfEmpty()
            orderby conexion.FechaConectadaUtc descending
            select new
            {
                linea.Id,
                ConexionId = conexion.Id,
                conexion.Version,
                conexion.Nombre,
                linea.NumeroTelefono,
                linea.PhoneNumberId,
                linea.WabaId,
                linea.Modo,
                linea.ComercialAsignadoId,
                conexion.ClienteId,
                ClienteNombre = (string?)cliente!.RazonSocial,
                linea.MensajeAutoTriage,
                conexion.Estado,
            }).ToListAsync(cancellationToken);

        var errores = filas.Count == 0 || clienteActivoSeleccionado.SesionPrivilegiadaIdSeleccionada is not null
            ? new Dictionary<Guid, string>()
            : await ObtenerConexionesIntegracionQueryHandler.UltimosErroresAsync(
                integracionesContext, filas.Select(f => f.ConexionId).Distinct().ToList(), cancellationToken);

        var miembrosPorLinea = await integracionesContext.MiembrosPoolLinea
            .GroupBy(m => m.LineaWhatsAppId)
            .Select(g => new { LineaId = g.Key, Usuarios = g.Select(m => m.UsuarioId).ToList() })
            .ToDictionaryAsync(g => g.LineaId, g => (IReadOnlyList<Guid>)g.Usuarios, cancellationToken);

        return filas
            .Select(f => new LineaWhatsAppListaDto(
                f.Id, f.ConexionId, f.Version, f.Nombre, f.NumeroTelefono, f.PhoneNumberId, f.WabaId, f.Modo,
                f.ComercialAsignadoId, miembrosPorLinea.GetValueOrDefault(f.Id, []), f.ClienteId, f.ClienteNombre,
                f.MensajeAutoTriage, f.Estado, errores.GetValueOrDefault(f.ConexionId)))
            .ToList();
    }
}
