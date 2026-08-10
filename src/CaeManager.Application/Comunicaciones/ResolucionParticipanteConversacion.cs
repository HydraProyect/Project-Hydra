using CaeManager.Application.Asignaciones;
using CaeManager.Application.Centros;
using CaeManager.Application.Trabajadores;
using CaeManager.Domain.Comunicaciones;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Comunicaciones;

/// <summary>
/// Resuelve a qué Trabajador pertenece el email de un participante de una
/// Conversacion — hasta ahora todo participante se creaba con
/// <see cref="TipoParticipanteOrigen.Desconocido"/> en los tres puntos de
/// alta (ingesta de webhook, redacción nueva, migración de WhatsApp a
/// correo), así que <see cref="ParticipanteConversacion.EntidadRelacionadaId"/>
/// nunca se rellenaba en ningún camino real (docs/blueprints — ronda de
/// reducción de ruido en Comunicaciones). Sin esto, ni el patrón "trabajador
/// fuera de ventana" ni el criterio "Mismo Trabajador" del Conversation
/// Matching Engine (docs/COMUNICACIONES.md § 13.2) tienen ninguna señal real
/// que usar.
///
/// Mismo alcance que <see cref="Deteccion.SugerenciaGestionCorreoService"/>:
/// solo Trabajadores con alta activa en algún Centro del Cliente de la
/// conversación son candidatos — sin Cliente resuelto no hay a quién
/// comparar (fail closed, la conversación sigue en triage).
/// </summary>
public interface IResolucionParticipanteConversacionService
{
    Task<(TipoParticipanteOrigen TipoOrigen, Guid? EntidadRelacionadaId)> ResolverAsync(
        string email, Guid? clienteId, CancellationToken cancellationToken = default);
}

public class ResolucionParticipanteConversacionService(
    ICentrosQueryContext centrosContext,
    IAsignacionesQueryContext asignacionesContext,
    ITrabajadoresQueryContext trabajadoresContext) : IResolucionParticipanteConversacionService
{
    public async Task<(TipoParticipanteOrigen TipoOrigen, Guid? EntidadRelacionadaId)> ResolverAsync(
        string email, Guid? clienteId, CancellationToken cancellationToken = default)
    {
        if (clienteId is not { } clienteIdValor)
            return (TipoParticipanteOrigen.Desconocido, null);

        var emailNormalizado = email.Trim();

        var centroIds = await centrosContext.Centros
            .Where(c => c.ClienteId == clienteIdValor)
            .Select(c => c.Id)
            .ToListAsync(cancellationToken);

        if (centroIds.Count == 0)
            return (TipoParticipanteOrigen.Desconocido, null);

        var trabajadorIds = await asignacionesContext.Asignaciones
            .Where(a => centroIds.Contains(a.CentroId) && a.FechaBaja == null)
            .Select(a => a.TrabajadorId)
            .Distinct()
            .ToListAsync(cancellationToken);

        if (trabajadorIds.Count == 0)
            return (TipoParticipanteOrigen.Desconocido, null);

        var trabajadorId = await trabajadoresContext.Trabajadores
            .Where(t => trabajadorIds.Contains(t.Id) && t.Email != null && t.Email.ToLower() == emailNormalizado.ToLower())
            .Select(t => (Guid?)t.Id)
            .FirstOrDefaultAsync(cancellationToken);

        return trabajadorId is not null
            ? (TipoParticipanteOrigen.Trabajador, trabajadorId)
            : (TipoParticipanteOrigen.Desconocido, null);
    }
}
