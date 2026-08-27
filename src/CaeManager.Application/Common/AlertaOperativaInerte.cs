namespace CaeManager.Application.Common;

/// <summary>
/// Implementación por defecto de <see cref="IAlertaOperativa"/>: no hace
/// nada. Registrada con <c>TryAddSingleton</c> en <c>AddApplication()</c>
/// para que cualquier host que monte el pipeline de MediatR sin pasar por
/// <c>AddInfrastructure()</c> —los fixtures de <c>CaeManager.IntegrationTests</c>
/// que construyen su propio <c>ServiceCollection</c> mínimo, igual que ya
/// hacen para <c>ICurrentUserService</c>/<c>IAlcanceDatosService</c>— pueda
/// resolver <see cref="VentanaSaludOperativa"/> (que la inyecta) sin tener
/// que registrar un fake en cada uno. <c>Program.cs</c> registra
/// <c>SentryAlertaOperativa</c> (Infrastructure) después de
/// <c>AddApplication()</c>, así que en la app real esa es la que gana —
/// mismo principio "inerte por defecto" que el resto de integraciones
/// opcionales de este despliegue.
/// </summary>
public class AlertaOperativaInerte : IAlertaOperativa
{
    public void Emitir(string mensaje, NivelAlertaOperativa nivel)
    {
    }

    public void CapturarExcepcion(Exception excepcion)
    {
    }

    public void DejarMigaDePan(string mensaje)
    {
    }

    public IDisposable IniciarAmbitoDeCaptura() => AmbitoInerte.Instance;

    private sealed class AmbitoInerte : IDisposable
    {
        public static readonly AmbitoInerte Instance = new();

        public void Dispose()
        {
        }
    }
}
