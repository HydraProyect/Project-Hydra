using System.Globalization;
using CaeManager.Application.Documentos;
using CaeManager.Domain.Documentos;
using CaeManager.Web.Features.Documentos.Recursos;

namespace CaeManager.Web.Features.Documentos;

/// <summary>
/// El texto de vigencia de un documento en la fila desplegada de un listado de propietarios (Trabajadores,
/// Empresas), pegado a las maquetas («Lista Trabajadores TALVEG.dc.html», «Empresas TALVEG.dc.html»): una fecha
/// absoluta en lugar del «Hace N días», que ya dice el motivo de la fila de arriba. Vive aquí, una sola vez, para
/// que las dos listas no vuelvan a divergir en el formato ni en el caso «sin confirmar».
///
/// <para>
/// Las fechas se formatean siempre con la cultura invariante (<c>dd/MM/yyyy</c>), como los demás listados de
/// fechas del producto, para que el texto no dependa de la cultura del servidor ni del navegador.
/// </para>
///
/// <para>
/// Cada lista usa su propio texto según su maqueta: Trabajadores dice «Desde el …» (vencido) y «Caduca el …»
/// (por vencer); Empresas dice «Venció el …» (vencido) y el plazo «Caduca en N días» (por vencer).
/// </para>
/// </summary>
public static class DetalleVigenciaDocumentoUi
{
    /// <summary>
    /// Trabajadores: «Desde el dd/MM/yyyy» si venció, «Caduca el dd/MM/yyyy» si está por vencer, la falta de
    /// fecha si está sin confirmar y vacío en cualquier otro caso.
    /// </summary>
    public static string DeTrabajador(IncidenciaDocumentalDto incidencia)
    {
        if (incidencia.Estado == EstadoDocumento.SinConfirmar)
            return FaltaLaFecha(incidencia);
        if (incidencia.FechaVencimiento is not { } fecha)
            return string.Empty;

        return incidencia.Estado switch
        {
            EstadoDocumento.Vencido or EstadoDocumento.EnTolerancia => Con("DetalleDesdeFecha", fecha),
            EstadoDocumento.Urgente or EstadoDocumento.Proximo => Con("DetalleCaducaFecha", fecha),
            _ => string.Empty
        };
    }

    /// <summary>
    /// Empresas: «Venció el dd/MM/yyyy» si venció, el plazo («Caduca en N días») si está por vencer, la falta
    /// de fecha si está sin confirmar y vacío en cualquier otro caso.
    /// </summary>
    public static string DeEmpresa(IncidenciaDocumentalDto incidencia, DateOnly hoy)
    {
        if (incidencia.Estado == EstadoDocumento.SinConfirmar)
            return FaltaLaFecha(incidencia);
        if (incidencia.FechaVencimiento is not { } fecha)
            return string.Empty;

        return incidencia.Estado is EstadoDocumento.Vencido or EstadoDocumento.EnTolerancia
            ? Con("DetalleVencioFecha", fecha)
            : EstadoDocumentoUi.MotivoDeDocumento(incidencia.Estado, fecha, hoy) ?? string.Empty;
    }

    private static string FaltaLaFecha(IncidenciaDocumentalDto incidencia) =>
        incidencia.EsContratoDeTrabajo
            ? TextosVigenciaDocumento.Texto("MotivoFaltaFechaFin")
            : TextosVigenciaDocumento.Texto("MotivoFaltaFechaVencimiento");

    private static string Con(string clave, DateOnly fecha) =>
        string.Format(CultureInfo.InvariantCulture, TextosVigenciaDocumento.Texto(clave),
            fecha.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture));
}
