using CaeManager.Application.Comunicaciones;
using CaeManager.Domain.Comunicaciones;

namespace CaeManager.Application.Tests.Comunicaciones;

/// <summary>Fake en memoria — nunca resuelve nada salvo que el test registre explícitamente una coincidencia.</summary>
public class ResolucionParticipanteConversacionServiceFalso : IResolucionParticipanteConversacionService
{
    private readonly Dictionary<string, Guid> _trabajadorPorEmail = new(StringComparer.OrdinalIgnoreCase);

    public void RegistrarTrabajador(string email, Guid trabajadorId) => _trabajadorPorEmail[email] = trabajadorId;

    public Task<(TipoParticipanteOrigen TipoOrigen, Guid? EntidadRelacionadaId)> ResolverAsync(
        string email, Guid? clienteId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_trabajadorPorEmail.TryGetValue(email, out var trabajadorId)
            ? (TipoParticipanteOrigen.Trabajador, (Guid?)trabajadorId)
            : (TipoParticipanteOrigen.Desconocido, null));
}
