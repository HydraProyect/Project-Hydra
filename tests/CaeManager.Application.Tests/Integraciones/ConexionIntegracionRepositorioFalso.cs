using CaeManager.Domain.Integraciones;

namespace CaeManager.Application.Tests.Integraciones;

public class ConexionIntegracionRepositorioFalso : IConexionIntegracionRepository
{
    public List<ConexionIntegracion> Conexiones { get; } = [];

    /// <summary>
    /// Conexiones que, releídas de la base, ya no están operativas aunque la instancia cargada diga lo contrario:
    /// otro proceso las pasó a ConError o las desconectó después de la carga.
    /// </summary>
    public HashSet<Guid> NoOperativasEnBase { get; } = [];

    /// <summary>Cuántas veces se releyó el estado antes de una llamada a Graph.</summary>
    public int VecesReleido { get; private set; }

    /// <summary>Se invoca en cada relectura, antes de responder: permite cambiar el estado entre una página y otra.</summary>
    public Action<int>? AlReleer { get; set; }

    public Task<ConexionIntegracion?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(Conexiones.FirstOrDefault(c => c.Id == id));

    public Task<bool> SigueOperativaVerificadaAsync(Guid id, DateTime ahoraUtc, CancellationToken cancellationToken = default)
    {
        VecesReleido++;
        AlReleer?.Invoke(VecesReleido);
        return Task.FromResult(
            !NoOperativasEnBase.Contains(id)
            && Conexiones.FirstOrDefault(c => c.Id == id) is { } conexion
            && conexion.OperativaVerificada(ahoraUtc));
    }

    public void Agregar(ConexionIntegracion conexion) => Conexiones.Add(conexion);
}
