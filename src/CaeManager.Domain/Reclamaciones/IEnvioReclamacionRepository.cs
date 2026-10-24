namespace CaeManager.Domain.Reclamaciones;

public interface IEnvioReclamacionRepository
{
    /// <summary>
    /// El envío pendiente (sin reclamación registrada) más reciente de este usuario con esta huella, iniciado desde
    /// <paramref name="iniciadoDesdeUtc"/>; null si no hay ninguno. El filtro de Tenant lo pone el contexto.
    /// </summary>
    Task<EnvioReclamacion?> ObtenerPendienteAsync(
        Guid usuarioId, string huella, DateTime iniciadoDesdeUtc, CancellationToken cancellationToken);

    void Agregar(EnvioReclamacion envio);
}
