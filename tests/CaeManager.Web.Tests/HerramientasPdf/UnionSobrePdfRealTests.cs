using System.Text;
using CaeManager.Application.HerramientasPdf;
using CaeManager.Domain.Common;
using CaeManager.Infrastructure.HerramientasPdf;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PdfSharp.Pdf.IO;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using Xunit;

namespace CaeManager.Web.Tests.HerramientasPdf;

/// <summary>
/// Unir sobre PDF reales fabricados aquí, con datos inventados: PDFsharp inspecciona y une, y PdfPig lee el texto del
/// resultado para comprobar que las páginas llegan todas y en el orden pedido. Sin servicios externos.
/// </summary>
public sealed class UnionSobrePdfRealTests
{
    private readonly PdfSharpUnidorPdf _unidor = new(NullLogger<PdfSharpUnidorPdf>.Instance);

    /// <summary>Un PDF con una página por texto.</summary>
    internal static byte[] Pdf(params string[] textos)
    {
        var constructor = new PdfDocumentBuilder();
        var fuente = constructor.AddStandard14Font(Standard14Font.Helvetica);

        foreach (var texto in textos)
            constructor.AddPage(595, 842).AddText(texto, 14, new PdfPoint(40, 780), fuente);

        return constructor.Build();
    }

    private static string[] TextosDePaginas(byte[] pdf)
    {
        using var documento = PdfDocument.Open(pdf);
        return documento.GetPages().Select(p => string.Join(' ', p.GetWords().Select(w => w.Text))).ToArray();
    }

    [Fact]
    public void Une_en_el_orden_recibido_y_conserva_todas_las_paginas()
    {
        var resultado = _unidor.Unir([Pdf("Contrato A1", "Contrato A2"), Pdf("Seguro B1"), Pdf("Acta C1", "Acta C2", "Acta C3")]);

        resultado.EsExitoso.Should().BeTrue(resultado.EsExitoso ? "" : resultado.Error.Codigo);
        resultado.Valor.NumeroPaginas.Should().Be(6);
        TextosDePaginas(resultado.Valor.Contenido).Should().Equal(
            "Contrato A1", "Contrato A2", "Seguro B1", "Acta C1", "Acta C2", "Acta C3");
    }

    [Fact]
    public void Cambiar_el_orden_de_la_entrada_cambia_el_del_PDF_unido()
    {
        var a = Pdf("Primero");
        var b = Pdf("Segundo");

        TextosDePaginas(_unidor.Unir([a, b]).Valor.Contenido).Should().Equal("Primero", "Segundo");
        TextosDePaginas(_unidor.Unir([b, a]).Valor.Contenido).Should().Equal("Segundo", "Primero");
    }

    [Fact]
    public void Un_mismo_PDF_repetido_se_une_las_veces_que_aparece()
    {
        var a = Pdf("Anexo");

        TextosDePaginas(_unidor.Unir([a, Pdf("Cuerpo"), a]).Valor.Contenido).Should().Equal("Anexo", "Cuerpo", "Anexo");
    }

    [Fact]
    public void El_resultado_es_un_PDF_nuevo_que_se_vuelve_a_abrir_con_sus_paginas()
    {
        var unido = _unidor.Unir([Pdf("Uno"), Pdf("Dos")]).Valor.Contenido;

        _unidor.Inspeccionar(unido).Valor.Should().Be(new InfoPdfParaUnir(2, false));
    }

    [Fact]
    public void Inspeccionar_cuenta_paginas_y_marca_el_PDF_firmado_y_el_normal_no()
    {
        var normal = Pdf("Uno", "Dos", "Tres");

        _unidor.Inspeccionar(normal).Valor.Should().Be(new InfoPdfParaUnir(3, false));
        _unidor.Inspeccionar(ConCampoDeFirma(normal)).Valor.Should().Be(new InfoPdfParaUnir(3, true));
    }

    [Fact]
    public void Un_comentario_con_las_letras_de_una_firma_no_la_finge()
    {
        var comentado = Pdf("Uno").Concat(Encoding.ASCII.GetBytes("\n%/ByteRange [0 1 2 3] /Type /Sig\n")).ToArray();

        _unidor.Inspeccionar(comentado).Valor.EstaFirmado.Should().BeFalse("la firma se lee de la estructura, no de los bytes");
    }

    [Fact]
    public void Una_firma_con_los_nombres_codificados_se_detecta()
    {
        // /F#54 /S#69g es /FT /Sig y /Byte#52ange es /ByteRange (ISO 32000-1 § 7.3.5): una búsqueda literal no los ve.
        var firmado = PdfACano(
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [4 0 R] >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] >>",
            "<< /F#54 /S#69g /T (Firma) /V 5 0 R >>",
            "<< /Filter /Adobe.PPKLite /Byte#52ange [0 10 20 30] /Contents <00> >>");

        Encoding.ASCII.GetString(firmado).Should().NotContain("/ByteRange");
        _unidor.Inspeccionar(firmado).Valor.Should().Be(new InfoPdfParaUnir(1, true));
    }

    [Theory]
    [InlineData("<< /FT 7 /V 3 0 R >>")]
    [InlineData("<< /FT [/Sig] /V 3 0 R >>")]
    [InlineData("<< /Type (Sig) /ByteRange [0 1 2 3] >>")]
    [InlineData("<< /Type 12 /ByteRange [0 1 2 3] >>")]
    public void Un_PDF_con_claves_de_firma_que_no_son_nombres_se_trata_como_ilegible_y_no_lanza(string objetoHostil)
    {
        // Un PDF ajeno puede poner en /FT o /Type cualquier cosa; recorrerlo para buscar firmas no puede escapar del tratamiento de errores.
        var hostil = PdfACano(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] >>",
            objetoHostil);

        Result<InfoPdfParaUnir>? resultado = null;
        var inspeccionar = () => resultado = _unidor.Inspeccionar(hostil);

        inspeccionar.Should().NotThrow();
        if (resultado!.EsFallido)
            resultado.Error.Codigo.Should().Be(ErroresUnirPdf.Ilegible);
    }

    // ---- Coste de la estructura (bombas PDF)

    [Fact]
    public void Un_flujo_de_objetos_que_se_descomprime_por_encima_del_tope_se_rechaza_sin_abrir()
    {
        // Pocos KB que se descomprimen a 40 MB, con el nombre del tipo codificado para que una búsqueda literal no lo vea.
        var bomba = PdfConFlujoDeObjetos("/Type /Obj#53tm /N 1 /First 4", 40 * 1024 * 1024);
        bomba.Length.Should().BeLessThan(1024 * 1024);

        var resultado = _unidor.Inspeccionar(bomba);

        resultado.Error.Codigo.Should().Be(ErroresUnirPdf.DemasiadoComplejo);
        _unidor.Unir([Pdf("Uno"), bomba]).Error.Codigo.Should().Be(ErroresUnirPdf.DemasiadoComplejo);
    }

    [Fact]
    public void Un_flujo_de_objetos_que_declara_mas_objetos_que_el_tope_se_rechaza_sin_descomprimirlo()
    {
        var muchos = PdfConFlujoDeObjetos($"/Type /ObjStm /N {LimitesUnionPdf.MaximoObjetosPorPdf + 1} /First 4", 64);

        _unidor.Inspeccionar(muchos).Error.Codigo.Should().Be(ErroresUnirPdf.DemasiadoComplejo);
    }

    [Fact]
    public void Un_flujo_de_objetos_normal_se_abre_y_su_estructura_cuenta()
    {
        var normal = PdfConFlujoDeObjetos("/Type /ObjStm /N 1 /First 4", 2048);

        var info = _unidor.Inspeccionar(normal);

        info.EsExitoso.Should().BeTrue(info.EsExitoso ? "" : info.Error.Codigo);
        info.Valor.NumeroPaginas.Should().Be(1);
        info.Valor.BytesEstructura.Should().BeGreaterThanOrEqualTo(2048, "los bytes descomprimidos del flujo de objetos cuentan");
    }

    [Fact]
    public void Un_PDF_firmado_se_une_y_el_resultado_ya_no_lleva_la_firma()
    {
        var firmado = ConCampoDeFirma(Pdf("Firmado"));

        var unido = _unidor.Unir([firmado, Pdf("Otro")]).Valor.Contenido;

        _unidor.Inspeccionar(unido).Valor.EstaFirmado.Should().BeFalse("el unido es un PDF nuevo y no conserva la firma");
    }

    [Fact]
    public void Un_PDF_con_contrasena_se_distingue_de_uno_roto_al_inspeccionar_y_al_unir()
    {
        var protegido = ConContrasena(Pdf("Secreto"));

        _unidor.Inspeccionar(protegido).Error.Codigo.Should().Be(ErroresUnirPdf.ConContrasena);
        _unidor.Unir([Pdf("Uno"), protegido]).Error.Codigo.Should().Be(ErroresUnirPdf.ConContrasena);
        _unidor.Inspeccionar(Encoding.ASCII.GetBytes("esto no es un PDF")).Error.Codigo.Should().Be(ErroresUnirPdf.Ilegible);
        _unidor.Inspeccionar([]).Error.Codigo.Should().Be(ErroresUnirPdf.Ilegible);
    }

    [Fact]
    public void Si_las_paginas_del_unido_pasan_del_maximo_no_se_une()
    {
        var trescientas = Pdf(Enumerable.Range(1, 300).Select(i => $"Pagina {i}").ToArray());

        var resultado = _unidor.Unir([trescientas, trescientas]);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be(ErroresUnirPdf.DemasiadasPaginas);
    }

    [Fact]
    public void Quinientas_paginas_justas_si_se_unen()
    {
        var doscientas = Pdf(Enumerable.Range(1, 200).Select(i => $"Pagina {i}").ToArray());
        var trescientas = Pdf(Enumerable.Range(1, 300).Select(i => $"Hoja {i}").ToArray());

        var resultado = _unidor.Unir([doscientas, trescientas]);

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.NumeroPaginas.Should().Be(LimitesUnionPdf.MaximoPaginasTotales);
    }

    [Fact]
    public void Un_PDF_de_mas_del_tope_total_de_bytes_no_se_abre()
    {
        var pasado = new byte[LimitesUnionPdf.TamanoMaximoTotalBytes + 1];
        Pdf("Uno").CopyTo(pasado, 0);

        _unidor.Inspeccionar(pasado).Error.Codigo.Should().Be(ErroresUnirPdf.DemasiadoGrande);
    }

    /// <summary>El PDF con un campo de firma /FT /Sig cuyo valor es un diccionario de firma con /ByteRange (sin firma criptográfica real: basta para el aviso).</summary>
    private static byte[] ConCampoDeFirma(byte[] pdf)
    {
        using var entrada = new MemoryStream(pdf);
        using var documento = PdfReader.Open(entrada, PdfDocumentOpenMode.Modify);
        var firma = new PdfSharp.Pdf.PdfDictionary(documento);
        firma.Elements["/Type"] = new PdfSharp.Pdf.PdfName("/Sig");
        firma.Elements["/Filter"] = new PdfSharp.Pdf.PdfName("/Adobe.PPKLite");
        firma.Elements["/ByteRange"] = new PdfSharp.Pdf.PdfArray(documento,
            new PdfSharp.Pdf.PdfInteger(0), new PdfSharp.Pdf.PdfInteger(10), new PdfSharp.Pdf.PdfInteger(20), new PdfSharp.Pdf.PdfInteger(30));
        documento.Internals.AddObject(firma);
        var campo = new PdfSharp.Pdf.PdfDictionary(documento);
        campo.Elements["/FT"] = new PdfSharp.Pdf.PdfName("/Sig");
        campo.Elements["/T"] = new PdfSharp.Pdf.PdfString("Firma1");
        campo.Elements.SetReference("/V", firma);
        documento.Internals.AddObject(campo);
        var campos = new PdfSharp.Pdf.PdfArray(documento);
        campos.Elements.Add(campo.Reference!);
        var formulario = new PdfSharp.Pdf.PdfDictionary(documento);
        formulario.Elements["/Fields"] = campos;
        documento.Internals.Catalog.Elements["/AcroForm"] = formulario;
        using var salida = new MemoryStream();
        documento.Save(salida, closeStream: false);
        return salida.ToArray();
    }

    /// <summary>Un PDF clásico escrito a mano, con un objeto por texto (el 1 es el catálogo) y su tabla de referencias.</summary>
    internal static byte[] PdfACano(params string[] objetos)
    {
        var pdf = new StringBuilder("%PDF-1.4\n");
        var posiciones = new List<int>();
        for (var i = 0; i < objetos.Length; i++)
        {
            posiciones.Add(pdf.Length);
            pdf.Append(i + 1).Append(" 0 obj\n").Append(objetos[i]).Append("\nendobj\n");
        }

        var xref = pdf.Length;
        pdf.Append("xref\n0 ").Append(objetos.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (var posicion in posiciones)
            pdf.Append(posicion.ToString("D10")).Append(" 00000 n \n");
        pdf.Append("trailer\n<< /Size ").Append(objetos.Length + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.Latin1.GetBytes(pdf.ToString());
    }

    /// <summary>
    /// Un PDF de una página con un flujo de objetos Flate (objeto 4) cuyo contenido descomprimido mide
    /// <paramref name="bytesDescomprimidos"/>: un objeto vacío y relleno de blancos, que comprime casi a nada.
    /// </summary>
    private static byte[] PdfConFlujoDeObjetos(string diccionario, int bytesDescomprimidos)
    {
        var claro = new byte[bytesDescomprimidos];
        Array.Fill(claro, (byte)' ');
        "5 0 << >>"u8.CopyTo(claro);
        using var comprimido = new MemoryStream();
        using (var zlib = new System.IO.Compression.ZLibStream(comprimido, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
            zlib.Write(claro);
        var datos = Encoding.Latin1.GetString(comprimido.ToArray());

        return PdfACano(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] >>",
            $"<< {diccionario} /Filter /FlateDecode /Length {comprimido.Length} >>\nstream\n{datos}\nendstream");
    }

    private static byte[] ConContrasena(byte[] pdf)
    {
        using var entrada = new MemoryStream(pdf);
        using var documento = PdfReader.Open(entrada, PdfDocumentOpenMode.Modify);
        documento.SecuritySettings.UserPassword = "usuario";
        documento.SecuritySettings.OwnerPassword = "propietario";
        using var salida = new MemoryStream();
        documento.Save(salida, closeStream: false);
        return salida.ToArray();
    }
}
