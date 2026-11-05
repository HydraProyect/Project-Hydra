using CaeManager.Application.Plataforma;

namespace CaeManager.Application.Tests.Comercial;

/// <summary>
/// Concesión vigente en la primera comprobación y revocada desde la segunda: simula que se
/// revoca mientras el comando espera el candado del Tenant. Solo cuenta lo que se pregunta
/// sobre un Tenant; lo global no lo usan los comandos comerciales.
/// </summary>
public class AutorizacionRevocadaDuranteLaEsperaFalsa : IAutorizacionAdminPlataforma
{
    public int VecesConsultada { get; private set; }

    public Task<bool> PuedeSobreTenantAsync(Guid usuarioId, Guid tenantObjetivoId, CancellationToken cancellationToken = default) =>
        Task.FromResult(++VecesConsultada == 1);

    public Task<bool> PuedeGlobalmenteAsync(Guid usuarioId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
}
