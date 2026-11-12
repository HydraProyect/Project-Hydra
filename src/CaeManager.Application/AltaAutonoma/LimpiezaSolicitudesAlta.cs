using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CaeManager.Application.AltaAutonoma;

/// <summary>
/// Caducidad y borrado de datos personales de las Solicitudes de alta (DA-7). <c>SolicitudesAlta</c> está cerrada a
/// los roles de la aplicación, así que cada operación es una función <c>SECURITY DEFINER</c> con los plazos escritos
/// dentro, que decide con el reloj de la base y devuelve solo un recuento: ni filas ni datos personales.
/// </summary>
public interface ILimpiezaSolicitudesAlta
{
    /// <summary>
    /// Pasa a <c>Caducada</c> hasta <paramref name="tamanoLote"/> Solicitudes de alta cuyo plazo de 7 días venció y
    /// que no han pagado. Devuelve cuántas caducó.
    /// </summary>
    Task<int> CaducarVencidasAsync(int tamanoLote, CancellationToken cancellationToken);

    /// <summary>
    /// Borra los datos personales de hasta <paramref name="tamanoLote"/> Solicitudes de alta <c>Caducada</c> con
    /// 30 días desde su creación. Devuelve cuántas limpió.
    /// </summary>
    Task<int> BorrarDatosPersonalesAsync(int tamanoLote, CancellationToken cancellationToken);
}

/// <summary>
/// Opciones del trabajo de limpieza (<c>AltaAutonoma:Limpieza</c>). Activo por defecto, a diferencia de la bandera
/// <see cref="AltaAutonomaOptions.Habilitada"/>: con el alta apagada no hay filas que tocar, y el día que se
/// encienda la minimización de datos no depende de acordarse de un segundo ajuste.
/// </summary>
public class LimpiezaSolicitudesAltaOptions
{
    public const string SeccionConfiguracion = "AltaAutonoma:Limpieza";
    public const int TamanoLoteMaximo = 1000;

    /// <summary>Con false el servicio de fondo no hace nada.</summary>
    public bool Activa { get; set; } = true;

    /// <summary>Cada cuánto mira el líder si hay algo que caducar o borrar. Una hora por defecto; mínimo un minuto.</summary>
    public TimeSpan Intervalo { get; set; } = TimeSpan.FromHours(1);

    /// <summary>Filas por llamada a la función, entre 1 y <see cref="TamanoLoteMaximo"/>.</summary>
    public int TamanoLote { get; set; } = 200;

    /// <summary>Tope de lotes por operación y pasada: un atasco no monopoliza la conexión.</summary>
    public int MaximoLotesPorPasada { get; set; } = 50;
}

public readonly record struct ResultadoLimpiezaAlta(int Caducadas, int Borradas);

/// <summary>
/// Una pasada de limpieza: primero caduca lo vencido y después borra los datos de lo ya caducado con 30 días, cada
/// operación por lotes hasta que un lote sale incompleto o se agota <see cref="LimpiezaSolicitudesAltaOptions.MaximoLotesPorPasada"/>.
/// Es idempotente: sin nada pendiente devuelve ceros.
/// </summary>
public sealed class ServicioLimpiezaSolicitudesAlta(
    ILimpiezaSolicitudesAlta limpieza, IOptions<LimpiezaSolicitudesAltaOptions> opciones, ILogger<ServicioLimpiezaSolicitudesAlta> logger)
{
    public async Task<ResultadoLimpiezaAlta> EjecutarAsync(CancellationToken cancellationToken)
    {
        var configuracion = opciones.Value;
        var lote = Math.Clamp(configuracion.TamanoLote, 1, LimpiezaSolicitudesAltaOptions.TamanoLoteMaximo);
        var maximoLotes = Math.Max(1, configuracion.MaximoLotesPorPasada);

        var caducadas = await EnLotesAsync(limpieza.CaducarVencidasAsync, lote, maximoLotes, cancellationToken);
        var borradas = await EnLotesAsync(limpieza.BorrarDatosPersonalesAsync, lote, maximoLotes, cancellationToken);

        if (caducadas > 0 || borradas > 0)
            logger.LogInformation(
                "Limpieza de Solicitudes de alta: {Caducadas} caducadas, {Borradas} con los datos personales borrados.",
                caducadas, borradas);

        return new ResultadoLimpiezaAlta(caducadas, borradas);
    }

    private static async Task<int> EnLotesAsync(
        Func<int, CancellationToken, Task<int>> operacion, int lote, int maximoLotes, CancellationToken cancellationToken)
    {
        var total = 0;
        for (var i = 0; i < maximoLotes; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var hechas = await operacion(lote, cancellationToken);
            total += hechas;

            // Un lote incompleto es que ya no queda nada que cumpla la condición (o que lo que queda está bloqueado
            // por otra transacción y se deja para la próxima pasada).
            if (hechas < lote)
                break;
        }

        return total;
    }
}
