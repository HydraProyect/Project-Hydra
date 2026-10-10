using ClosedXML.Excel;

namespace CaeManager.Web.Exportacion;

/// <summary>
/// El libro de Excel de un listado: una hoja, la fila de cabeceras en negrita y una fila por
/// elemento. Devuelve también cuántas filas escribió, que es lo que consta en el rastro de la
/// descarga (<c>IRegistroExportacionService</c>).
/// </summary>
public static class LibroDeListado
{
    public const string TipoContenido = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    public static async Task<(MemoryStream Libro, int Filas)> EscribirAsync(
        string hoja, IReadOnlyList<string> cabeceras, IAsyncEnumerable<IReadOnlyList<XLCellValue>> filas,
        CancellationToken cancellationToken = default)
    {
        using var libro = new XLWorkbook();
        var pagina = libro.Worksheets.Add(hoja);

        for (var columna = 0; columna < cabeceras.Count; columna++)
            pagina.Cell(1, columna + 1).Value = cabeceras[columna];
        pagina.Row(1).Style.Font.Bold = true;

        var escritas = 0;
        await foreach (var fila in filas.WithCancellation(cancellationToken))
        {
            for (var columna = 0; columna < fila.Count; columna++)
                pagina.Cell(escritas + 2, columna + 1).Value = fila[columna];
            escritas++;
        }

        pagina.Columns().AdjustToContents();

        var stream = new MemoryStream();
        libro.SaveAs(stream);
        stream.Position = 0;
        return (stream, escritas);
    }

    /// <summary>Una fecha como fecha de Excel (sin hora), o celda vacía.</summary>
    public static XLCellValue Fecha(DateOnly? fecha) =>
        fecha is { } valor ? valor.ToDateTime(TimeOnly.MinValue) : Blank.Value;
}
