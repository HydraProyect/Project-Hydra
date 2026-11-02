using CaeManager.Infrastructure.DocumentosIa;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using Xunit;

namespace CaeManager.IntegrationTests.DocumentosIa;

/// <summary>
/// Tope del bitmap que PdfToPngRasterizadorPaginasPdfService pide a la
/// librería nativa (ASVS 4.0.3 V12.1.1, API4). Medido con PDFtoImage 5.2.1: un
/// PDF de unos 330 bytes con una página de 139 pulgadas reservaba 1,7 GiB y
/// tardaba ~30 s; a las 200 pulgadas la librería ya no podía reservar. El tope
/// (192 MiB de BGRA a 150 ppp) está en la constante del servicio: un plano A0
/// (2384 x 3370 pt, ~133 MiB) es un documento legítimo y debe leerse. Los
/// tamaños de estos tests quedan a un lado y otro: 3.300 pt son 180,3 MiB y
/// 3.500 pt 202,9 MiB. No necesitan base de datos.
/// </summary>
public class RasterizadorPaginasPdfTopeTests
{
    private const string CodigoDemasiadoGrande = "Rasterizador.PaginaDemasiadoGrande";

    private readonly PdfToPngRasterizadorPaginasPdfService _servicio =
        new(NullLogger<PdfToPngRasterizadorPaginasPdfService>.Instance);

    [Fact]
    public void Una_pagina_A4_se_rasteriza_a_png()
    {
        var resultado = _servicio.RasterizarPagina(CrearPdf((595, 842)), 0);

        resultado.EsExitoso.Should().BeTrue();
        EsPng(resultado.Valor).Should().BeTrue();
    }

    [Fact]
    public void Una_pagina_justo_por_debajo_del_tope_se_rasteriza()
    {
        var resultado = _servicio.RasterizarPagina(CrearPdf((3300, 3300)), 0);

        resultado.EsExitoso.Should().BeTrue();
        EsPng(resultado.Valor).Should().BeTrue();
    }

    [Fact]
    public void Un_plano_A0_se_rasteriza_a_png()
    {
        var resultado = _servicio.RasterizarPagina(CrearPdf((2384, 3370)), 0);

        resultado.EsExitoso.Should().BeTrue();
        EsPng(resultado.Valor).Should().BeTrue();
    }

    [Fact]
    public void Un_plano_del_doble_de_lado_que_un_A0_se_rechaza()
    {
        // 4.768 x 6.740 pt: ~533 MiB de bitmap, cuatro veces el A0.
        var resultado = _servicio.RasterizarPagina(CrearPdf((4768, 6740)), 0);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be(CodigoDemasiadoGrande);
    }

    [Fact]
    public void Una_pagina_justo_por_encima_del_tope_se_rechaza_sin_rasterizar()
    {
        var resultado = _servicio.RasterizarPagina(CrearPdf((3500, 3500)), 0);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be(CodigoDemasiadoGrande);
    }

    [Fact]
    public void Una_pagina_de_cien_pulgadas_se_rechaza_en_vez_de_reservar_cerca_de_un_gibibyte()
    {
        // 7.200 pt = 100 pulgadas: medido ~900 MiB y 17 s sin el tope.
        var resultado = _servicio.RasterizarPagina(CrearPdf((7200, 7200)), 0);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be(CodigoDemasiadoGrande);
    }

    [Fact]
    public void Se_rechaza_solo_la_pagina_grande_de_un_documento_con_otras_normales()
    {
        var pdf = CrearPdf((595, 842), (7200, 7200));

        _servicio.RasterizarPagina(pdf, 0).EsExitoso.Should().BeTrue();
        _servicio.RasterizarPagina(pdf, 1).Error.Codigo.Should().Be(CodigoDemasiadoGrande);
    }

    [Fact]
    public void El_mensaje_del_fallo_no_incluye_dimensiones_ni_contenido_del_documento()
    {
        var resultado = _servicio.RasterizarPagina(CrearPdf((7200, 7200)), 0);

        resultado.Error.Mensaje.Should().Be("Una página de este documento es demasiado grande para convertirla.");
    }

    [Fact]
    public void La_miniatura_de_una_pagina_grande_pero_proporcionada_se_genera_porque_su_bitmap_es_pequeno()
    {
        var resultado = _servicio.RasterizarMiniatura(CrearPdf((7200, 7200)), 0, 48);

        resultado.EsExitoso.Should().BeTrue();
        EsPng(resultado.Valor).Should().BeTrue();
    }

    [Fact]
    public void La_miniatura_de_una_pagina_extremadamente_alargada_se_rechaza_por_el_alto_que_daria_la_proporcion()
    {
        // 100 x 14.000 pt a 4.000 px de ancho pedirían 4.000 x 560.000 px.
        var resultado = _servicio.RasterizarMiniatura(CrearPdf((100, 14000)), 0, 4000);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be(CodigoDemasiadoGrande);
    }

    private static bool EsPng(byte[] bytes) =>
        bytes.Length > 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47;

    private static byte[] CrearPdf(params (double Ancho, double Alto)[] paginas)
    {
        using var documento = new PdfDocument();
        foreach (var (ancho, alto) in paginas)
        {
            var pagina = documento.AddPage();
            pagina.Width = XUnit.FromPoint(ancho);
            pagina.Height = XUnit.FromPoint(alto);
        }

        using var ms = new MemoryStream();
        documento.Save(ms, closeStream: false);
        return ms.ToArray();
    }
}
