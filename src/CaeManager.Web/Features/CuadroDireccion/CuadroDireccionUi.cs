using System.Globalization;
using CaeManager.Application.Dashboard.Queries;

namespace CaeManager.Web.Features.CuadroDireccion;

/// <summary>
/// Umbral de anonimato del Cuadro de dirección (decisión de Dirección, 2026-10-17): una medida no se muestra
/// cuando se apoya en menos de <see cref="MinimoPersonas"/> personas o en menos de
/// <see cref="MinimoConversaciones"/> conversaciones. Es una regla de privacidad del equipo, no de calidad del
/// dato: con pocas personas o pocas conversaciones la medida deja de ser del equipo y pasa a ser de alguien.
/// </summary>
public static class UmbralAnonimatoCuadro
{
    public const int MinimoPersonas = 3;
    public const int MinimoConversaciones = 20;

    /// <summary>Una celda o una barra con 1 a <c>MinimoPorCelda - 1</c> conversaciones no enseña su recuento.</summary>
    public const int MinimoPorCelda = 5;

    public static bool EsPoco(int cantidad) => cantidad > 0 && cantidad < MinimoPorCelda;

    /// <summary>
    /// Si la medida se puede mostrar. <paramref name="personas"/> es null cuando la medida no se calcula sobre
    /// la actividad de personas (volumen de solicitudes de una Empresa): entonces solo cuenta el mínimo de
    /// conversaciones. Con un recuento de personas, ambos mínimos han de cumplirse.
    /// </summary>
    public static bool Cumple(int? personas, int conversaciones) =>
        conversaciones >= MinimoConversaciones && (personas is null || personas >= MinimoPersonas);
}

/// <summary>Una fila de la tabla «Por equipo» de Comunicaciones; los tiempos son medidas del equipo, nunca de una persona.</summary>
public sealed record FilaEquipoComunicaciones(
    string Equipo, int Personas, int Conversaciones,
    double PrimeraRespuestaHoras, double ResolucionDias, int UnaRespuestaPct, int ReabiertasPct, int EnSlaPct, double CambiosDeManos);

/// <summary>Una celda del mapa de calor de solicitudes: día laborable (1 = lunes … 5 = viernes), hora y peso sobre el total.</summary>
public sealed record CeldaSolicitudes(int Dia, int Hora, int Cantidad, double PesoPorcentaje);

public static class CuadroDireccionUi
{
    public static readonly CultureInfo Castellano = CultureInfo.GetCultureInfo("es-ES");

    private static readonly string[] MesesCortos = ["ene", "feb", "mar", "abr", "may", "jun", "jul", "ago", "sep", "oct", "nov", "dic"];

    /// <summary>«Oct 2026»: el rótulo de mes de la maqueta (mes abreviado y año, con la primera en mayúscula).</summary>
    public static string EtiquetaMes(int anyo, int mes) =>
        mes is >= 1 and <= 12
            ? char.ToUpper(MesesCortos[mes - 1][0], Castellano) + MesesCortos[mes - 1][1..] + " " + anyo.ToString(Castellano)
            : "—";

    /// <summary>
    /// Las solicitudes de lunes a viernes con su peso sobre el total de esos días. Los fines de semana no se
    /// pintan (la maqueta solo tiene cinco filas) y tampoco entran en el denominador del peso.
    /// </summary>
    public static IReadOnlyList<CeldaSolicitudes> CeldasLaborables(IEnumerable<FranjaSolicitudesDto> franjas)
    {
        var laborables = franjas
            .Where(f => f.Dia is >= DayOfWeek.Monday and <= DayOfWeek.Friday && f.Cantidad > 0)
            .ToList();
        var total = laborables.Sum(f => f.Cantidad);
        return total == 0
            ? []
            : laborables
                .Select(f => new CeldaSolicitudes((int)f.Dia, f.Hora, f.Cantidad, f.Cantidad * 100.0 / total))
                .OrderBy(c => c.Dia).ThenBy(c => c.Hora)
                .ToList();
    }
}
