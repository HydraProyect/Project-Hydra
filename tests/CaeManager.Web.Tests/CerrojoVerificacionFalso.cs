using CaeManager.Infrastructure.Coordinacion;

namespace CaeManager.Web.Tests;

/// <summary>
/// Cerrojo de prueba para las páginas que serializan una verificación por cuenta. Con
/// <see cref="Concede"/> a falso se comporta como si otra petición ya estuviera dentro:
/// no ejecuta el trabajo. Guarda la última clave para comprobar el prefijo compartido.
/// </summary>
internal sealed class CerrojoVerificacionFalso : IEleccionLiderService
{
    public bool Concede { get; set; } = true;
    public string? ClaveRecibida { get; private set; }

    public async Task<bool> IntentarEjecutarComoLiderAsync(
        string clave, Func<CancellationToken, Task> trabajo, CancellationToken cancellationToken)
    {
        ClaveRecibida = clave;
        if (!Concede)
            return false;
        await trabajo(cancellationToken);
        return true;
    }
}
