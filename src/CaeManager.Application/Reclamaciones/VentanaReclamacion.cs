namespace CaeManager.Application.Reclamaciones;

/// <summary>
/// Ventana de lo reclamable: un documento con FechaVencimiento (sin ella no hay nada que pedir renovar)
/// y que vence como mucho dentro de <see cref="Meses"/> meses, sin límite inferior. La comparten el envío y la ficha de
/// Trabajador 360, para no ofrecer lo que el envío rechaza. Las vistas previas del lote (ObtenerLoteReclamacion*) repiten
/// el mismo <c>AddMonths(3)</c> a mano: si cambia la ventana hay que cambiarlas también.
/// </summary>
public static class VentanaReclamacion
{
    public const int Meses = 3;

    public static DateOnly Limite(DateOnly hoy) => hoy.AddMonths(Meses);

    public static bool EsReclamable(DateOnly? fechaVencimiento, DateOnly hoy) =>
        fechaVencimiento is { } fecha && fecha <= Limite(hoy);
}
