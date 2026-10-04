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
///
/// <para>
/// Lo que no tiene fecha NO es «reclamable» en este sentido y no entra en la ventana: es lo que se <b>pide</b> por otro
/// camino del mismo flujo. Dos casos, y solo dos: un Documento «Sin confirmar» sin fecha de vencimiento
/// (<see cref="EsSinConfirmarSinFecha"/> / <see cref="SinConfirmarSinFecha"/>: existe, pero nadie anotó hasta cuándo vale) y un
/// documento que nunca se subió (no hay Documento: lo decide <c>IPendientesDeReclamacionService</c> contra el requisito del
/// Centro). «No caduca» confirmado no se pide nunca: no hay nada que pedir.
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
        // Solo lo operativo: reclamar la renovación de un documento ya sustituido pediría lo que ya se renovó.
        return documentos.Operativos().Where(d => d.FechaVencimiento != null && d.FechaVencimiento <= limite);
    }

    /// <summary>
    /// Un documento cuya vigencia nadie ha anotado: «Sin confirmar» y sin fecha de vencimiento. No vence nunca a ojos de la
    /// ventana, pero tampoco está al día: lo que se le pide a quien lo aporta es su vigencia. Misma regla que
    /// <see cref="SinConfirmarSinFecha"/>, en memoria (la prueba de tabla las mantiene iguales).
    /// </summary>
    public static bool EsSinConfirmarSinFecha(EstadoVigenciaDocumento estadoVigencia, DateOnly? fechaVencimiento) =>
        estadoVigencia == EstadoVigenciaDocumento.SinConfirmar && fechaVencimiento is null;

    /// <summary>Los Documentos operativos «Sin confirmar» y sin fecha, para usarlos como origen de una consulta. Mismo criterio que <see cref="EsSinConfirmarSinFecha"/>.</summary>
    public static IQueryable<Documento> SinConfirmarSinFecha(this IQueryable<Documento> documentos) =>
        documentos.Operativos().Where(d => d.EstadoVigencia == EstadoVigenciaDocumento.SinConfirmar && d.FechaVencimiento == null);
}
