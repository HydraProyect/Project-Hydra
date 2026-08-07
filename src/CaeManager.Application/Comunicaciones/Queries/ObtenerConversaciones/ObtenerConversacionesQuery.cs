using System.Text.RegularExpressions;
using CaeManager.Application.Common;
using CaeManager.Application.Clientes;
using CaeManager.Application.Comunicaciones;
using CaeManager.Domain.Comunicaciones;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Comunicaciones.Queries.ObtenerConversaciones;

/// <summary>
/// Filtros de la bandeja compartida (ver ARQUITECTURA-INTEGRACIONES.md § 12.6,
/// pantalla "Bandeja"). <see cref="Anio"/>/<see cref="Mes"/> van juntos (mes
/// concreto de un año) porque la pantalla ofrece un único selector de mes, no
/// un rango de fechas libre. <see cref="SoloAsignadasAMi"/> usa
/// <see cref="ICurrentUserService"/> para resolver "a mí" — no recibe el
/// usuario como parámetro para que el Command no pueda usarse para consultar
/// la bandeja de otro usuario.
/// </summary>
public record ObtenerConversacionesQuery(
    EstadoConversacion? Estado = null,
    int? Anio = null,
    int? Mes = null,
    Guid? ClienteId = null,
    bool SoloAsignadasAMi = false,
    bool SoloSinAsignar = false,
    string? Busqueda = null,
    CanalConversacion? Canal = null)
    : IRequest<IReadOnlyList<ConversacionListaDto>>;

public record ConversacionListaDto(
    Guid Id,
    Guid? ClienteId,
    string? ClienteRazonSocial,
    string Asunto,
    EstadoConversacion Estado,
    Guid? EjecutivoAsignadoId,
    string RemitentePrincipal,
    string PreviewUltimoMensaje,
    DateTime FechaUltimoMensajeUtc,
    int TotalMensajes,
    CanalConversacion Canal,
    string? TelefonoContacto);

public class ObtenerConversacionesQueryHandler(
    IClientesQueryContext clientesContext, IComunicacionesQueryContext comunicacionesContext, IAlcanceDatosService alcanceDatos, ICurrentUserService currentUserService)
    : IRequestHandler<ObtenerConversacionesQuery, IReadOnlyList<ConversacionListaDto>>
{
    private const int LongitudPreview = 140;

    // Mismo literal repetido que en AutorizacionEscrituraBehavior y por el
    // mismo motivo: Application no puede referenciar Infrastructure.Identity.Roles
    // sin invertir la dependencia entre capas.
    private const string RolCliente = "Cliente";

    public async Task<IReadOnlyList<ConversacionListaDto>> Handle(
        ObtenerConversacionesQuery request, CancellationToken cancellationToken)
    {
        var consulta = comunicacionesContext.Conversaciones.AsQueryable();

        var clienteIdsVisibles = await alcanceDatos.ObtenerClienteIdsVisiblesAsync(cancellationToken);
        if (clienteIdsVisibles is not null)
        {
            // La cola de triage (ClienteId null) queda visible pese a la
            // cartera: por definición todavía no tiene cliente resuelto, así
            // que no se puede acotar a una (ver § 12.4). Pero eso es cierto
            // solo para los roles de gestión CAE — al rol Cliente, un contacto
            // de una empresa cliente externa, le daba acceso de lectura al
            // correo sin triar de las demás (hallazgo N-2 de
            // INFORME-AUDITORIA-2.md).
            //
            // La comprobación se repite aquí y en [Authorize] de Bandeja.razor
            // a propósito: la página cierra la puerta de entrada, esto cierra
            // el dato para cualquier otra UI o API que llegue después.
            var rol = await currentUserService.ObtenerRolActualAsync();

            consulta = rol == RolCliente
                ? consulta.Where(c => c.ClienteId != null && clienteIdsVisibles.Contains(c.ClienteId!.Value))
                : consulta.Where(c => c.ClienteId == null || clienteIdsVisibles.Contains(c.ClienteId!.Value));
        }

        if (request.Estado is not null)
            consulta = consulta.Where(c => c.Estado == request.Estado);

        if (request.Canal is not null)
            consulta = consulta.Where(c => c.Canal == request.Canal);

        if (request.Anio is not null && request.Mes is not null)
            consulta = consulta.Where(c =>
                c.FechaUltimoMensajeUtc.Year == request.Anio && c.FechaUltimoMensajeUtc.Month == request.Mes);

        if (request.ClienteId is not null)
            consulta = consulta.Where(c => c.ClienteId == request.ClienteId);

        if (request.SoloSinAsignar)
            consulta = consulta.Where(c => c.EjecutivoAsignadoId == null);

        if (request.SoloAsignadasAMi)
        {
            var usuarioId = await currentUserService.ObtenerUsuarioActualIdAsync();
            consulta = consulta.Where(c => c.EjecutivoAsignadoId == usuarioId);
        }

        if (!string.IsNullOrWhiteSpace(request.Busqueda))
        {
            var busqueda = request.Busqueda.ToUpper();
            consulta = consulta.Where(c => c.Asunto.ToUpper().Contains(busqueda));
        }

        var conversaciones = await (
            from c in consulta
            join cliente in clientesContext.Clientes on c.ClienteId equals cliente.Id into clientesUnidos
            from cliente in clientesUnidos.DefaultIfEmpty()
            orderby c.FechaUltimoMensajeUtc descending
            select new
            {
                c.Id,
                c.ClienteId,
                ClienteRazonSocial = cliente != null ? cliente.RazonSocial : null,
                c.Asunto,
                c.Estado,
                c.EjecutivoAsignadoId,
                c.FechaUltimoMensajeUtc,
                c.Canal,
                c.TelefonoContacto
            })
            .ToListAsync(cancellationToken);

        if (conversaciones.Count == 0) return [];

        var conversacionIds = conversaciones.Select(c => c.Id).ToList();

        var mensajes = await comunicacionesContext.Mensajes
            .Where(m => conversacionIds.Contains(m.ConversacionId))
            .Select(m => new { m.ConversacionId, m.CuerpoHtml, m.FechaUtc })
            .ToListAsync(cancellationToken);

        var remitentes = await comunicacionesContext.ParticipantesConversacion
            .Where(p => conversacionIds.Contains(p.ConversacionId) && p.Rol == RolParticipante.De)
            .Select(p => new { p.ConversacionId, p.Email })
            .ToListAsync(cancellationToken);

        var mensajesPorConversacion = mensajes.GroupBy(m => m.ConversacionId).ToDictionary(g => g.Key, g => g.ToList());
        var remitentesPorConversacion = remitentes.GroupBy(p => p.ConversacionId).ToDictionary(g => g.Key, g => g.First().Email);

        return conversaciones.Select(c =>
        {
            var mensajesDeConversacion = mensajesPorConversacion.GetValueOrDefault(c.Id, []);
            var ultimoMensaje = mensajesDeConversacion.OrderByDescending(m => m.FechaUtc).FirstOrDefault();

            return new ConversacionListaDto(
                c.Id, c.ClienteId, c.ClienteRazonSocial, c.Asunto, c.Estado, c.EjecutivoAsignadoId,
                // WhatsApp no tiene participantes de correo: el remitente es el teléfono del contacto.
                remitentesPorConversacion.GetValueOrDefault(c.Id) ?? c.TelefonoContacto ?? "Remitente desconocido",
                TruncarParaPreview(ultimoMensaje?.CuerpoHtml),
                c.FechaUltimoMensajeUtc, mensajesDeConversacion.Count, c.Canal, c.TelefonoContacto);
        }).ToList();
    }

    private static string TruncarParaPreview(string? cuerpoHtml)
    {
        if (string.IsNullOrWhiteSpace(cuerpoHtml)) return string.Empty;

        var textoPlano = Regex.Replace(cuerpoHtml, "<.*?>", " ");
        textoPlano = Regex.Replace(textoPlano, "\\s+", " ").Trim();

        return textoPlano.Length > LongitudPreview ? textoPlano[..LongitudPreview] + "…" : textoPlano;
    }
}
