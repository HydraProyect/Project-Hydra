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

    /// <summary>
    /// Imita el rastreo de EF: con él, <see cref="ObtenerPorIdAsync"/> devuelve la instancia que ya devolvió antes aunque
    /// la «base» (<see cref="Conexiones"/>) tenga otra versión, y solo <see cref="ObtenerActualPorIdAsync"/> lee la de la
    /// base. Sin él, las dos leen la lista (comportamiento previo de los tests).
    /// </summary>
    public bool SimularRastreo { get; set; }

    private readonly Dictionary<Guid, ConexionIntegracion> _rastreadas = [];

    /// <summary>Cuántas veces se leyó la versión de la base con <see cref="ObtenerActualPorIdAsync"/>.</summary>
    public int VecesLeidaActual { get; private set; }

    public Task<ConexionIntegracion?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        if (SimularRastreo && _rastreadas.TryGetValue(id, out var rastreada))
            return Task.FromResult<ConexionIntegracion?>(rastreada);

        return Task.FromResult(Rastrear(Conexiones.FirstOrDefault(c => c.Id == id)));
    }

    /// <summary>Se invoca en cada lectura de la versión de la base, con su número (1 = la primera); puede lanzar.</summary>
    public Action<int>? AlLeerActual { get; set; }

    public Task<ConexionIntegracion?> ObtenerActualPorIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        VecesLeidaActual++;
        AlLeerActual?.Invoke(VecesLeidaActual);
        return Task.FromResult(Rastrear(Conexiones.FirstOrDefault(c => c.Id == id)));
    }

    private ConexionIntegracion? Rastrear(ConexionIntegracion? conexion)
    {
        if (SimularRastreo && conexion is not null)
            _rastreadas[conexion.Id] = conexion;
        return conexion;
    }

    /// <summary>
    /// Otra operación guardó la conexión: la «base» pasa a tener otra instancia (mismo Id, buzón, nombre e identidad) con
    /// el estado que deje <paramref name="configurar"/>. La instancia que el llamador tenía queda obsoleta.
    /// </summary>
    public ConexionIntegracion SustituirEnBase(Guid id, Action<ConexionIntegracion> configurar)
    {
        var indice = Conexiones.FindIndex(c => c.Id == id);
        var original = Conexiones[indice];
        var copia = new ConexionIntegracion(original.BuzonEmail, original.Nombre);
        typeof(CaeManager.Domain.Common.Entity).GetProperty(nameof(CaeManager.Domain.Common.Entity.Id))!.SetValue(copia, original.Id);
        if (original.IdentidadBuzon is { } identidad)
            copia.FijarIdentidadBuzon(identidad);
        configurar(copia);
        Conexiones[indice] = copia;
        return copia;
    }

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
