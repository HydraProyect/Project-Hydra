using CaeManager.Application.Common;
using CaeManager.Web.Documentos;
using FluentAssertions;
using PdfSharp.Pdf;
using Xunit;

namespace CaeManager.Web.Tests;

/// <summary>
/// UnificarAsync copiaba las páginas de cada PDF de entrada sin mirar
/// cuántas declaraba. Mismo vector que la bomba de píxeles de
/// <see cref="DimensionesImagenTests"/>, pero para el número de páginas en
/// vez de para las dimensiones de una imagen: un árbol de páginas compacto
/// puede declarar decenas de miles de páginas casi vacías muy por debajo del
/// tope de 10 MB de la subida, y ni ese tope ni el presupuesto del lote (que
/// cuentan bytes de fichero) lo detectan.
/// </summary>
public class ConversorArchivosPdfTests
{
    /// <summary>PDF con el número de páginas pedido, todas en blanco — el coste que se mide es el de copiarlas, no el de dibujar nada en ellas.</summary>
    private static byte[] CrearPdfConPaginas(int numeroPaginas)
    {
        using var documento = new PdfDocument();
        for (var i = 0; i < numeroPaginas; i++)
            documento.AddPage();

        using var salida = new MemoryStream();
        documento.Save(salida);
        return salida.ToArray();
    }

    [Fact]
    public async Task Un_pdf_que_declara_demasiadas_paginas_se_rechaza()
    {
        var bomba = CrearPdfConPaginas(2001);

        var convertir = async () => await ConversorArchivosPdf.UnificarAsync(
            [(bomba, "bomba.pdf")], new ConversorWordFalso());

        await convertir.Should().ThrowAsync<InvalidDataException>();
    }

    [Fact]
    public async Task Varios_pdfs_moderados_que_suman_demasiadas_paginas_tambien_se_rechazan()
    {
        // No basta con mirar cada archivo por separado: el límite es sobre
        // el combinado completo, no por archivo.
        (byte[] Contenido, string NombreArchivo)[] archivos =
        [
            (CrearPdfConPaginas(1200), "parte1.pdf"),
            (CrearPdfConPaginas(1200), "parte2.pdf"),
        ];

        var convertir = async () => await ConversorArchivosPdf.UnificarAsync(archivos, new ConversorWordFalso());

        await convertir.Should().ThrowAsync<InvalidDataException>();
    }

    [Fact]
    public async Task Un_pdf_de_tamano_normal_se_sigue_combinando()
    {
        // Control positivo: sin él, un guardia que rechazara todo también
        // pasaría el caso de arriba.
        var normal = CrearPdfConPaginas(5);

        var pdf = await ConversorArchivosPdf.UnificarAsync([(normal, "documento.pdf")], new ConversorWordFalso());

        System.Text.Encoding.ASCII.GetString(pdf, 0, 5).Should().Be("%PDF-");
    }

    private sealed class ConversorWordFalso : IConversorWordPdfService
    {
        public Task<byte[]> ConvertirAPdfAsync(byte[] contenidoDocx, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
