namespace CaeManager.Web.Components.DesignSystem;

/// <summary>
/// Una entrada del resumen por estado de <see cref="CabeceraGrupoLista"/>: el color del punto y el texto ya
/// redactado por la pantalla, con su cifra («2 vencidos»).
/// </summary>
public sealed record ResumenEstadoGrupo(TonoBadge Tono, string Texto);

/// <summary>
/// Tinte de <see cref="CabeceraGrupoLista"/>. La pantalla lo decide con el mismo criterio con el que tiñe sus
/// filas: <see cref="Peligro"/> si el grupo contiene alguna fila «con problema», <see cref="Aviso"/> si contiene
/// alguna «por vencer» que tiña, y <see cref="Ninguno"/> (cabecera blanca) en otro caso.
/// </summary>
public enum TinteGrupoLista
{
    Ninguno = 0,
    Aviso = 1,
    Peligro = 2,
}
