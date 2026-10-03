using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Application.Reportes.Queries;
using CaeManager.Web.Features.Documentos;
using ClosedXML.Excel;

namespace CaeManager.Web.Reportes;

/// <summary>
/// Construye los bytes de PDF/Excel de un informe ya generado — factorizado
/// de ReportesEndpoints.cs para que "Enviar por Comunicaciones…" (Reportes.razor)
/// pueda adjuntar el mismo archivo que generan los enlaces de descarga sin
/// duplicar el dibujado.
/// </summary>
public static class ConstructorInformeArchivos
{
    /// <summary>
    /// Texto de la columna «Vencimiento» de una fila del informe de vigencia: la fecha, o, sin fecha, el rótulo de
    /// <see cref="EstadoDocumentoUi.TextoSinFechaDeVencimiento"/> («Sin caducidad» / «Sin confirmar» / «—»). Lo usan
    /// la hoja de la vista previa (Reportes.razor) y el PDF, para que lo que se ve y lo que se descarga digan lo mismo.
    /// </summary>
    public static string TextoVencimiento(FilaReporteDocumentoDto fila) =>
        fila.FechaVencimiento?.ToString("dd/MM/yyyy") ?? EstadoDocumentoUi.TextoSinFechaDeVencimiento(fila.Estado);

    public static byte[] ExcelVigencia(InformeVigenciaDto informe)
    {
        using var libro = new XLWorkbook();
        var hoja = libro.Worksheets.Add("Informe");

        string[] cabeceras = ["Estado", "Trabajador", "Empresa", "Tipo de documento", "Vencimiento"];
        for (var i = 0; i < cabeceras.Length; i++) hoja.Cell(1, i + 1).Value = cabeceras[i];
        hoja.Row(1).Style.Font.Bold = true;

        var fila = 2;
        foreach (var documento in informe.Filas)
        {
            hoja.Cell(fila, 1).Value = EstadoDocumentoUi.Texto(documento.Estado);
            hoja.Cell(fila, 2).Value = documento.TrabajadorNombre;
            hoja.Cell(fila, 3).Value = documento.EmpresaRazonSocial;
            hoja.Cell(fila, 4).Value = documento.TipoDocumentoNombre;
            // Con fecha, una fecha real de Excel (ordenable y filtrable); sin ella, el rótulo solo en «Sin caducidad» y
            // «Sin confirmar»; en cualquier otro estado sin fecha, la celda vacía como siempre.
            if (documento.FechaVencimiento is not null)
                hoja.Cell(fila, 5).Value = documento.FechaVencimiento.Value.ToDateTime(TimeOnly.MinValue);
            else if (EstadoDocumentoUi.RotulaSinFechaDeVencimiento(documento.Estado))
                hoja.Cell(fila, 5).Value = EstadoDocumentoUi.TextoSinFechaDeVencimiento(documento.Estado);
            fila++;
        }

        hoja.Columns().AdjustToContents();
        using var stream = new MemoryStream();
        libro.SaveAs(stream);
        return stream.ToArray();
    }

    public static byte[] PdfVigencia(InformeVigenciaDto informe, bool incluirVigentes)
    {
        var titulo = incluirVigentes ? "Informe de vigencia documental" : "Informe de incidencias";
        var subtitulo = $"{Marca.Nombre} — {informe.Alcance} — generado el {DiaDeNegocio.AhoraEnHoraPeninsular():dd/MM/yyyy HH:mm} (hora peninsular) — {informe.Filas.Count} fila(s)";
        var filas = informe.Filas.Select(f => new[]
        {
            EstadoDocumentoUi.Texto(f.Estado), f.TrabajadorNombre, f.EmpresaRazonSocial,
            f.TipoDocumentoNombre, TextoVencimiento(f)
        }).ToList();

        return GeneradorPdfInforme.Generar(titulo, subtitulo, ["Estado", "Trabajador", "Empresa", "Tipo de documento", "Vencimiento"], [55, 130, 120, 130, 80], filas);
    }

    public static byte[] ExcelAsignaciones(InformeAsignacionesDto informe)
    {
        using var libro = new XLWorkbook();
        var hoja = libro.Worksheets.Add("Informe");
        string[] cabeceras = ["Trabajador", "Centro", "Asignado desde"];
        for (var i = 0; i < cabeceras.Length; i++) hoja.Cell(1, i + 1).Value = cabeceras[i];
        hoja.Row(1).Style.Font.Bold = true;

        var fila = 2;
        foreach (var asignacion in informe.Filas)
        {
            hoja.Cell(fila, 1).Value = asignacion.TrabajadorNombre;
            hoja.Cell(fila, 2).Value = asignacion.CentroNombre;
            hoja.Cell(fila, 3).Value = asignacion.FechaAlta.ToDateTime(TimeOnly.MinValue);
            fila++;
        }

        hoja.Columns().AdjustToContents();
        using var stream = new MemoryStream();
        libro.SaveAs(stream);
        return stream.ToArray();
    }

    public static byte[] PdfAsignaciones(InformeAsignacionesDto informe)
    {
        var subtitulo = $"{Marca.Nombre} — {informe.Alcance} — generado el {DiaDeNegocio.AhoraEnHoraPeninsular():dd/MM/yyyy HH:mm} (hora peninsular) — {informe.Filas.Count} fila(s)";
        var filas = informe.Filas.Select(f => new[] { f.TrabajadorNombre, f.CentroNombre, f.FechaAlta.ToString("dd/MM/yyyy") }).ToList();

        return GeneradorPdfInforme.Generar("Informe de asignaciones activas", subtitulo, ["Trabajador", "Centro", "Asignado desde"], [180, 180, 155], filas);
    }
}
