using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.Empresas;
using CaeManager.Domain.Comunicaciones;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Comunicaciones.Queries.ObtenerSugerenciasVisitaCorreoPendientes;

/// <summary>
/// Fase F: sugerencias de visita detectadas por IA (correo o WhatsApp, ver
/// <see cref="Conversacion.Canal"/>) todavía sin resolver — el punto de
/// entrada que la Bandeja del gestor usa para saber si hay una "visita
/// sorpresa" pendiente de confirmar, sin tener que abrir /comunicaciones.
/// </summary>
public record ObtenerSugerenciasVisitaCorreoPendientesQuery : IRequest<IReadOnlyList<SugerenciaVisitaCorreoPendienteDto>>;

/// <param name="ClienteId">Cliente del Centro detectado — null cuando la IA no pudo resolver un Centro. Alimenta la agrupación "por situación" del rediseño de Inicio (hallazgo P-03 de la auditoría de producto 2026-08-16).</param>
public record SugerenciaVisitaCorreoPendienteDto(
    Guid Id, Guid? CentroId, string? CentroNombre, DateOnly? FechaInicioSugerida, string Resumen, CanalConversacion Canal,
    DateTime CreadaEnUtc, Guid? ClienteId = null, string? ClienteNombre = null);

public class ObtenerSugerenciasVisitaCorreoPendientesQueryHandler(
    IComunicacionesQueryContext comunicacionesContext, ICentrosQueryContext centrosContext, IEmpresasQueryContext empresasContext,
    IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerSugerenciasVisitaCorreoPendientesQuery, IReadOnlyList<SugerenciaVisitaCorreoPendienteDto>>
{
    public async Task<IReadOnlyList<SugerenciaVisitaCorreoPendienteDto>> Handle(
        ObtenerSugerenciasVisitaCorreoPendientesQuery request, CancellationToken cancellationToken)
    {
        var centroIdsVisibles = await alcanceDatos.ObtenerCentroIdsVisiblesAsync(cancellationToken);

        var consulta =
            from sugerencia in comunicacionesContext.SugerenciasVisitaCorreo
            where !sugerencia.Resuelta
            join mensaje in comunicacionesContext.Mensajes on sugerencia.MensajeId equals mensaje.Id
            join conversacion in comunicacionesContext.Conversaciones on mensaje.ConversacionId equals conversacion.Id
            select new { sugerencia, conversacion.Canal };

        // Sin Centro resuelto no hay forma de acotar por cartera — se
        // excluye en vez de mostrarla a todo el mundo (fail closed, mismo
        // criterio que el resto del alcance de datos).
        if (centroIdsVisibles is not null)
            consulta = consulta.Where(x => x.sugerencia.CentroId != null && centroIdsVisibles.Contains(x.sugerencia.CentroId.Value));

        var pendientes = await consulta.ToListAsync(cancellationToken);
        if (pendientes.Count == 0) return [];

        var centroIds = pendientes.Where(p => p.sugerencia.CentroId is not null).Select(p => p.sugerencia.CentroId!.Value).Distinct().ToList();
        var nombresCentro = await centrosContext.Centros
            .Where(c => centroIds.Contains(c.Id))
            .Select(c => new { c.Id, c.Nombre })
            .ToDictionaryAsync(c => c.Id, c => c.Nombre, cancellationToken);

        // Centro.ClienteId repunta contra Empresas desde F3b: "Cliente" es una
        // Empresa contraparte (Empresa.CrearComoCliente).
        var clientesPorCentro = await (
            from centro in centrosContext.Centros
            where centroIds.Contains(centro.Id)
            join cliente in empresasContext.Empresas on centro.ClienteId equals cliente.Id
            select new { centro.Id, ClienteId = cliente.Id, cliente.RazonSocial })
            .ToDictionaryAsync(x => x.Id, x => (x.ClienteId, x.RazonSocial), cancellationToken);

        return pendientes
            .Select(p =>
            {
                var cliente = p.sugerencia.CentroId is { } centroId && clientesPorCentro.TryGetValue(centroId, out var c)
                    ? c : ((Guid?)null, (string?)null);

                return new SugerenciaVisitaCorreoPendienteDto(
                    p.sugerencia.Id,
                    p.sugerencia.CentroId,
                    p.sugerencia.CentroId is { } cid && nombresCentro.TryGetValue(cid, out var nombre) ? nombre : null,
                    p.sugerencia.FechaInicioSugerida,
                    p.sugerencia.Resumen,
                    p.Canal,
                    p.sugerencia.CreadaEnUtc,
                    ClienteId: cliente.Item1,
                    ClienteNombre: cliente.Item2);
            })
            .ToList();
    }
}
