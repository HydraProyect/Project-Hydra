using CaeManager.Domain.Reclamaciones;

namespace CaeManager.Application.Tests.Reclamaciones;

/// <summary>
/// Doble en memoria de <see cref="IEnvioReclamacionRepository"/>: lo agregado cuenta como guardado. Los tests de
/// idempotencia que necesitan distinguir «agregado» de «guardado» usan su propio doble ligado al guardado.
/// </summary>
public sealed class EnvioReclamacionRepositorioFalso : IEnvioReclamacionRepository
{
    public List<EnvioReclamacion> Envios { get; } = [];

    public Task<EnvioReclamacion?> ObtenerPendienteAsync(
        Guid usuarioId, string huella, DateTime iniciadoDesdeUtc, CancellationToken cancellationToken) =>
        Task.FromResult(Envios
            .Where(e => e.UsuarioId == usuarioId && e.Huella == huella && !e.EstaRegistrado && e.IniciadoEnUtc >= iniciadoDesdeUtc)
            .OrderByDescending(e => e.IniciadoEnUtc)
            .FirstOrDefault());

    public void Agregar(EnvioReclamacion envio) => Envios.Add(envio);
}
