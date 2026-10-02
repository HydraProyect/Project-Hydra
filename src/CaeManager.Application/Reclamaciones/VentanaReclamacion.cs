using CaeManager.Domain.Documentos;

namespace CaeManager.Application.Reclamaciones;

/// <summary>
/// Ventana de lo reclamable: un documento con FechaVencimiento (sin ella no hay nada que pedir renovar)
/// y que vence como mucho dentro de <see cref="Meses"/> meses, sin límite inferior. Es el ÚNICO sitio donde
/// vive la regla: la vista previa del lote (<c>ObtenerLoteReclamacion*</c>), el envío (<c>EnviarReclamacion*</c>)
/// y la ficha de Trabajador 360 la leen de aquí, para no ofrecer lo que el envío rechaza.
///
/// <para>
/// Dos formas de la misma regla, porque una consulta EF no puede llamar a un método propio dentro de la
/// expresión: <see cref="EsReclamable"/> sobre valores en memoria y <see cref="Reclamables"/> sobre una consulta
/// (se traduce a SQL). Las dos se mantienen iguales con una prueba de tabla
/// (<c>CoherenciaDeLaVentanaDeReclamacionEntreSuperficiesTests</c>) y el ratchet
/// <c>ReglasDeNegocioSinCopiasTests</c> prohíbe, fuera de este fichero, el literal de 3 meses, la variable
/// <c>limiteVentana</c> y el uso directo de <see cref="Limite"/> (quien necesite la condición usa
/// <see cref="Reclamables"/> o <see cref="EsReclamable"/>).
/// </para>
/// </summary>
public static class VentanaReclamacion
{
    public const int Meses = 3;

    public static DateOnly Limite(DateOnly hoy) => hoy.AddMonths(Meses);

    public static bool EsReclamable(DateOnly? fechaVencimiento, DateOnly hoy) =>
        fechaVencimiento is { } fecha && fecha <= Limite(hoy);

    /// <summary>
    /// Los Documentos dentro de la ventana, para usarlos como origen de una consulta
    /// (<c>from documento in documentosContext.Documentos.Reclamables(hoy)</c>). Mismo criterio que
    /// <see cref="EsReclamable"/>: con fecha y no posterior a <see cref="Limite"/>.
    /// </summary>
    public static IQueryable<Documento> Reclamables(this IQueryable<Documento> documentos, DateOnly hoy)
    {
        var limite = Limite(hoy);
        return documentos.Where(d => d.FechaVencimiento != null && d.FechaVencimiento <= limite);
    }
}
