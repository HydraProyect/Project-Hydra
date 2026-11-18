namespace CaeManager.Application.Common;

/// <summary>
/// Guarda de pestaña obsoleta (D-6 b, R-2; propuesta de buzones de varios Tenants § 5.4). La cookie de contexto es
/// del navegador y la comparten sus pestañas: un cambio de contexto en una cambia la selección vigente de todas.
/// Cada ámbito —un circuito de Blazor o una petición— escribe con la selección con la que pintó, y si ya no es la
/// vigente en el servidor, el comando no se ejecuta y la pestaña pide recargar. Con DN-6 (a) no hay excepción: un
/// comando siempre corre con la selección vigente, también después de un cambio de contexto desde la vista.
///
/// <para>
/// La comprueba <see cref="GuardaPestanaObsoletaBehavior{TRequest,TResponse}"/> para todo
/// <see cref="ICommandBase"/>. No autoriza nada: la autorización de la escritura sigue en
/// <see cref="AutorizacionEscrituraBehavior{TRequest,TResponse}"/>.
/// </para>
/// </summary>
public interface IGuardaSeleccionVigente
{
    /// <summary><c>null</c> si este ámbito puede escribir; si no, por qué no.</summary>
    SeleccionNoVigente? Comprobar();
}

public enum SeleccionNoVigente
{
    /// <summary>Otra pestaña del mismo navegador cambió la selección después de que este ámbito pintara.</summary>
    CambiadaEnOtraPestana = 1,

    /// <summary>La selección con la que este ámbito pintó se retiró (revocación, caducidad, cierre de la operación).</summary>
    RetiradaTrasPintar = 2,
}

/// <summary>
/// Fuera de Web no hay cookie de contexto ni pestañas (siembra, procesos de fondo, tests de Application): la
/// selección no puede quedar obsoleta. Web registra la suya.
/// </summary>
internal sealed class SinGuardaDeSeleccion : IGuardaSeleccionVigente
{
    public SeleccionNoVigente? Comprobar() => null;
}
