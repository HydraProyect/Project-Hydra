namespace CaeManager.Domain.ApiKeys;

public interface IRastroUsoApi
{
    /// <summary>Suma una petición a la fila del día, de forma atómica: dos peticiones simultáneas no se pisan.</summary>
    Task RegistrarPeticionAsync(
        Guid tenantId, Guid apiKeyId, TipoClaveUsoApi tipoClave, DateOnly dia, string endpoint,
        CancellationToken cancellationToken = default);
}
