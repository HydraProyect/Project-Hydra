using CaeManager.Application.Common;

namespace CaeManager.IntegrationTests;

/// <summary>Identidad de auditoría fija para los handlers que la piden. Sin argumento, sin identidad resuelta.</summary>
public class ActorAuditoriaFijoFalso(ActorAuditoria? actor = null) : IActorAuditoria
{
    private readonly ActorAuditoria _actor = actor ?? ActorAuditoria.SinResolver;

    public Task<ActorAuditoria> ObtenerAsync() => Task.FromResult(_actor);

    public ActorAuditoria? ObtenerSiYaEstaResuelto() => _actor;
}
