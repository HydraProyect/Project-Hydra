using Microsoft.AspNetCore.Components;

namespace CaeManager.Web.Components.DesignSystem;

/// <summary>
/// Una caja de la pestaña «Ficha» de una página 360 (<see cref="CajasFicha"/>).
/// </summary>
/// <param name="Clave">Identificador estable de la caja dentro de su tipo de ficha («plazo», «antelacion»).</param>
/// <param name="Contenido">La caja ya montada: normalmente una <see cref="Tarjeta"/> compacta con su título.</param>
public sealed record CajaFicha(string Clave, RenderFragment Contenido);
