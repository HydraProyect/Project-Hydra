using System.IO.Compression;
using CaeManager.Application.Documentos.Queries.ObtenerPaqueteAcreditacionEmpresa;
using ClosedXML.Excel;

namespace CaeManager.Web.Features.Documentos;

/// <summary>
/// Escribe el paquete de acreditación como ZIP directamente en el flujo de la respuesta, fichero a
/// fichero: nunca se acumula el paquete entero en memoria (solo un PDF cada vez, y el índice, que es
/// pequeño). El índice se escribe al FINAL porque recoge lo que de verdad pasó con cada fichero.
///
/// <para>
/// Tope de tamaño: al pasar <see cref="TopeBytesPorDefecto"/> de contenido ya escrito, los ficheros
/// siguientes se dejan fuera y se anotan en el índice («excluido: tope de tamaño»). Un fichero cuyo
/// blob no se puede abrir también se anota («archivo no disponible»), en vez de romper el paquete.
/// El acceso a un documento sensible se registra DESPUÉS de abrirlo, como en la descarga individual:
/// no queda el registro de un acceso que nunca entregó contenido.
/// </para>
/// </summary>
public static class PaqueteAcreditacionZip
{
    public const long TopeBytesPorDefecto = 250L * 1024 * 1024;

    public static async Task EscribirAsync(
        Stream destino,
        PaqueteAcreditacionDto paquete,
        Func<string, CancellationToken, Task<Stream>> abrirBlob,
        Func<Guid, CancellationToken, Task> registrarApertura,
        long topeBytes,
        CancellationToken cancellationToken)
    {
        var filas = paquete.Filas.ToList();
        long escritos = 0;

        await using (var zip = new ZipArchive(destino, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var entrada in paquete.Entradas)
            {
                if (escritos >= topeBytes)
                {
                    filas[entrada.IndiceFila] = filas[entrada.IndiceFila] with
                    {
                        Ruta = null, Resultado = IndicePaquete.ResultadoTopeTamano
                    };
                    continue;
                }

                Stream flujo;
                try
                {
                    flujo = await abrirBlob(entrada.ArchivoUrl, cancellationToken);
                }
                catch (Exception) when (!cancellationToken.IsCancellationRequested)
                {
                    filas[entrada.IndiceFila] = filas[entrada.IndiceFila] with
                    {
                        Ruta = null, Resultado = IndicePaquete.ResultadoArchivoNoDisponible
                    };
                    continue;
                }

                await using (flujo)
                {
                    await registrarApertura(entrada.DocumentoId, cancellationToken);

                    var fichero = zip.CreateEntry($"{paquete.NombreRaiz}/{entrada.Ruta}", CompressionLevel.Fastest);
                    await using var salida = await fichero.OpenAsync(cancellationToken);
                    await flujo.CopyToAsync(salida, cancellationToken);
                    escritos += flujo.CanSeek ? flujo.Length : fichero.Length;
                }
            }

            var indice = zip.CreateEntry($"{paquete.NombreRaiz}/00-Indice.xlsx", CompressionLevel.Fastest);
            await using var indiceSalida = await indice.OpenAsync(cancellationToken);
            await GenerarIndice(filas, indiceSalida, cancellationToken);
        }
    }

    public static async Task GenerarIndice(IReadOnlyList<FilaIndicePaquete> filas, Stream destino, CancellationToken cancellationToken)
    {
        using var libro = new XLWorkbook();
        var hoja = libro.Worksheets.Add(IndicePaquete.NombreHoja);

        for (var i = 0; i < IndicePaquete.Cabeceras.Count; i++)
            hoja.Cell(1, i + 1).Value = IndicePaquete.Cabeceras[i];
        hoja.Row(1).Style.Font.Bold = true;

        for (var f = 0; f < filas.Count; f++)
        {
            var fila = filas[f];
            var r = f + 2;
            hoja.Cell(r, 1).Value = fila.Ambito;
            hoja.Cell(r, 2).Value = fila.Titular;
            hoja.Cell(r, 3).Value = fila.DniNie ?? "";
            hoja.Cell(r, 4).Value = fila.Tipo;
            hoja.Cell(r, 5).Value = fila.Emision.ToDateTime(TimeOnly.MinValue);
            hoja.Cell(r, 6).Value = fila.Vigencia;
            hoja.Cell(r, 7).Value = fila.DocumentoId.ToString();
            hoja.Cell(r, 8).Value = fila.Ruta ?? "";
            hoja.Cell(r, 9).Value = fila.Resultado;
            hoja.Cell(r, 10).Value = fila.Advertencia ?? "";
        }

        hoja.Column(5).Style.NumberFormat.Format = "yyyy-mm-dd";
        hoja.Columns().AdjustToContents();

        // ClosedXML escribe en un flujo con búsqueda: el índice es pequeño, se pasa por memoria.
        using var memoria = new MemoryStream();
        libro.SaveAs(memoria);
        memoria.Position = 0;
        await memoria.CopyToAsync(destino, cancellationToken);
    }
}
