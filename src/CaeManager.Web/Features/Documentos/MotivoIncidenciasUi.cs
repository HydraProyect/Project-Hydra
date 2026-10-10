using System.Globalization;
using CaeManager.Application.Documentos;
using CaeManager.Domain.Documentos;
using CaeManager.Web.Features.Documentos.Recursos;

namespace CaeManager.Web.Features.Documentos;

/// <summary>
/// El motivo que va bajo la pastilla de estado de un propietario en un listado: dice lo que la pastilla no
/// dice, a partir de las incidencias de la fila (<see cref="IncidenciaDocumentalDto"/>). Es una pieza
/// compartida, no de un listado: hoy la usan los de Trabajadores y Vehículos; siempre con el vocabulario de
/// <see cref="EstadoDocumentoUi"/>.
///
/// <list type="bullet">
/// <item>Sin incidencias: no hay motivo.</item>
/// <item>Una sola: el nombre del Tipo de documento y, si está por vencer, cuánto le queda
/// («Formación Art. 19 · Caduca en 5 días»). De un vencido o un sin confirmar basta el nombre: la pastilla ya
/// dice qué le pasa.</item>
/// <item>Varias: cuántas hay de cada clase, de la más grave a la menos («2 vencidos · 1 por vencer ·
/// 1 sin confirmar»). Urgente y Próximo suman en «por vencer», como en la pastilla y en la franja.</item>
/// </list>
/// </summary>
public static class MotivoIncidenciasUi
{
    /// <summary>Separador de las partes del motivo. Es puntuación, no texto: no se localiza.</summary>
    public const string Separador = " · ";

    public static string? Texto(IReadOnlyList<IncidenciaDocumentalDto> incidencias, DateOnly hoy)
    {
        if (incidencias.Count == 0)
            return null;

        if (incidencias.Count == 1)
        {
            var unica = incidencias[0];
            var nombre = NombreDeTipo(unica);
            return unica.Estado is EstadoDocumento.Urgente or EstadoDocumento.Proximo
                && EstadoDocumentoUi.MotivoDeDocumento(unica.Estado, unica.FechaVencimiento, hoy) is { } cuanto
                ? nombre + Separador + cuanto
                : nombre;
        }

        var vencidos = incidencias.Count(i => i.Estado == EstadoDocumento.Vencido);
        var porVencer = incidencias.Count(i => i.Estado is EstadoDocumento.Urgente or EstadoDocumento.Proximo);
        var sinConfirmar = incidencias.Count(i => i.Estado == EstadoDocumento.SinConfirmar);

        var partes = new List<string>(3);
        if (vencidos > 0) partes.Add(Recuento(vencidos, "MotivoIncidenciasUnVencido", "MotivoIncidenciasVencidos"));
        if (porVencer > 0) partes.Add(Recuento(porVencer, "MotivoIncidenciasUnoPorVencer", "MotivoIncidenciasPorVencer"));
        if (sinConfirmar > 0) partes.Add(Recuento(sinConfirmar, "MotivoIncidenciasUnoSinConfirmar", "MotivoIncidenciasSinConfirmar"));

        return string.Join(Separador, partes);
    }

    /// <summary>Título del desglose: cuántos documentos piden atención.</summary>
    public static string Titulo(int cantidad) =>
        Recuento(cantidad, "VentanaIncidenciasTituloUna", "VentanaIncidenciasTitulo");

    /// <summary>El nombre del Tipo, o «Documento» si el Tipo ya no es legible.</summary>
    public static string NombreDeTipo(IncidenciaDocumentalDto incidencia) =>
        string.IsNullOrWhiteSpace(incidencia.TipoDocumentoNombre)
            ? TextosVigenciaDocumento.Texto("IncidenciaSinTipo")
            : incidencia.TipoDocumentoNombre;

    /// <summary>
    /// «8/10»: de los documentos registrados, los que están al día. <c>null</c> sin documentos registrados (la
    /// celda pinta entonces una raya, no «0/0»).
    /// </summary>
    public static string? RegistradosVigentes(int vigentes, int registrados) =>
        registrados <= 0
            ? null
            : string.Format(CultureInfo.CurrentCulture, TextosVigenciaDocumento.Texto("RegistradosVigentes"), vigentes, registrados);

    /// <summary>Lo mismo dicho entero, para quien no ve la tabla: «8 de 10 documentos registrados al día».</summary>
    public static string RegistradosVigentesAccesible(int vigentes, int registrados) =>
        registrados <= 0
            ? TextosVigenciaDocumento.Texto("RegistradosVigentesSinDocumentos")
            : string.Format(CultureInfo.CurrentCulture, TextosVigenciaDocumento.Texto("RegistradosVigentesAccesible"), vigentes, registrados);

    private static string Recuento(int cantidad, string claveUno, string claveVarios) =>
        cantidad == 1
            ? TextosVigenciaDocumento.Texto(claveUno)
            : string.Format(CultureInfo.CurrentCulture, TextosVigenciaDocumento.Texto(claveVarios), cantidad);
}
