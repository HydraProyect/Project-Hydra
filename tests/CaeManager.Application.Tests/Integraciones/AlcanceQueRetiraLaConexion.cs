using CaeManager.Application.Common;
using CaeManager.Application.Tests.Clientes;

namespace CaeManager.Application.Tests.Integraciones;

/// <summary>
/// Alcance que deja de ver la conexión tras <paramref name="consultasVisibles"/> consultas: simula que el alcance del
/// usuario cambió entre la carga y la relectura dentro del cerrojo (bajo B5 del revisor-puente, ronda 13).
/// </summary>
public sealed class AlcanceQueRetiraLaConexion(int consultasVisibles) : AlcanceDatosServiceFalso(), IAlcanceDatosService
{
    public int Consultas { get; private set; }

    Task<bool> IAlcanceDatosService.ConexionIntegracionVisibleAsync(Guid conexionIntegracionId, CancellationToken cancellationToken) =>
        Task.FromResult(++Consultas <= consultasVisibles);
}
