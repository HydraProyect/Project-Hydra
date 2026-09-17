using CaeManager.Domain.Plataforma;

namespace CaeManager.Application.Plataforma;

/// <summary>
/// Qué capacidades tienen, HOY, un camino de escritura realmente construido.
///
/// Gemelo de <see cref="CapacidadesQuePuedenAbrirSesion"/> pero para una
/// distinción distinta:
/// <code>
/// la capacidad PERMITE escribir en el modelo   ≠   existe HOY un camino que lo ejecute
/// </code>
/// <see cref="SesionPrivilegiadaActiva.PermiteEscritura"/> es la primera —
/// <c>BreakGlass</c> y <c>Aprovisionamiento</c> permiten escribir por
/// definición de la capacidad—; esta lista es la segunda, y es
/// deliberadamente más estrecha.
///
/// <b>Por qué <c>BreakGlass</c> queda fuera.</b> Lo que le da sentido a un
/// acceso de emergencia —motivo, ventana acotada, traza íntegra y revisión
/// posterior obligatoria— es una fase propia que todavía no existe
/// (<c>AutorizacionEscrituraBehavior</c>, comentario de
/// <c>ErrorDeSesionPrivilegiada</c>). Que la capacidad exista en el enum no
/// construye ese camino; ponerla aquí lo afirmaría sin que el sistema lo sepa
/// honrar.
///
/// <b>Punto de extensión, no ampliación.</b> Igual que la lista gemela,
/// entrar aquí exige un cambio deliberado con su propio incremento: nunca
/// llega como efecto colateral de que <see cref="SesionPrivilegiadaActiva.PermiteEscritura"/>
/// se ampliara antes.
/// </summary>
public static class CapacidadesConCaminoDeEscritura
{
    private static readonly HashSet<CapacidadPrivilegio> Admitidas = [CapacidadPrivilegio.Aprovisionamiento];

    public static bool Admite(CapacidadPrivilegio capacidad) => Admitidas.Contains(capacidad);
}
