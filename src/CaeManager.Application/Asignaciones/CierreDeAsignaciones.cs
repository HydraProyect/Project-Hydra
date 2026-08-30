using CaeManager.Domain.Asignaciones;

namespace CaeManager.Application.Asignaciones;

/// <summary>
/// <b>Una asignación activa no puede sobrevivir a la desaparición de uno de
/// sus dos extremos.</b> Cuando un Centro o un Trabajador pasa a
/// <c>EstaEliminado</c>, sus asignaciones activas se cierran aquí.
/// </summary>
/// <remarks>
/// <para>
/// Antes de este cierre, borrar un centro era <b>tres clics</b> —crear el
/// centro, asignar un trabajador, borrar el centro— y dejaba la asignación
/// con <c>FechaBaja IS NULL</c> colgando de un centro muerto. No saltaba
/// nada porque los filtros globales esconden el centro: la asignación
/// quedaba viva y su extremo invisible, así que el sistema respondía
/// «no hay violación» sin poder verla. Es el <i>cero por ausencia</i> de
/// CLAUDE.md §2 — un resultado vacío no es una ausencia hasta comprobar que
/// el instrumento podía observar lo que se buscaba.
/// </para>
/// <para>
/// Está extraído a un sitio único a propósito: son <b>cinco</b> rutas de
/// borrado (Centro singular y lote; Trabajador singular, lote y la
/// resolución de detección ausente) y cada una tiene su propio test. El
/// diseño de F5 inventariaba solo cuatro —daba «dos rutas de Trabajador»
/// cuando son tres—, y esa quinta ruta habría dejado la invariante rota por
/// un camino que nadie estaba mirando. El ratchet
/// <c>CierreDeAsignacionesEnBorradosTests</c> congela el inventario para que
/// una sexta ruta futura no pueda añadirse en silencio.
/// </para>
/// </remarks>
public static class CierreDeAsignaciones
{
    /// <summary>Cierra las asignaciones activas del centro que acaba de eliminarse.</summary>
    /// <returns>Cuántas se cerraron.</returns>
    public static Task<int> PorCentroEliminadoAsync(
        IAsignacionRepository asignaciones, Guid centroId, CancellationToken cancellationToken = default) =>
        CerrarAsync(asignaciones.ObtenerActivasPorCentroAsync(centroId, cancellationToken));

    /// <summary>Cierra las asignaciones activas del trabajador que acaba de eliminarse.</summary>
    /// <returns>Cuántas se cerraron.</returns>
    public static Task<int> PorTrabajadorEliminadoAsync(
        IAsignacionRepository asignaciones, Guid trabajadorId, CancellationToken cancellationToken = default) =>
        CerrarAsync(asignaciones.ObtenerActivasPorTrabajadorAsync(trabajadorId, cancellationToken));

    /// <remarks>
    /// La fecha se toma aquí y no llega por el comando porque ninguna de las
    /// cinco rutas de borrado la pide al usuario — igual que
    /// <c>EditarSubcontrataCommand</c> resuelve su <c>ahora</c> en el handler.
    /// No se guarda: el <c>SaveChangesAsync</c> es el de la ruta que llama,
    /// de modo que el borrado y el cierre entran en la misma transacción y no
    /// existe la ventana en la que el centro está muerto y la asignación viva.
    /// </remarks>
    private static async Task<int> CerrarAsync(Task<IReadOnlyList<Asignacion>> consulta)
    {
        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);
        var activas = await consulta;

        foreach (var asignacion in activas)
            asignacion.CerrarPorAmbitoEliminado(hoy);

        return activas.Count;
    }
}
