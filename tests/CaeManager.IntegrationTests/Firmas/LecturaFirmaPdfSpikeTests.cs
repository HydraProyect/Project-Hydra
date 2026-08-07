using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using CaeManager.Web.Reportes;
using FluentAssertions;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;
using PdfSharp.Pdf.Signatures;
using Xunit;

namespace CaeManager.IntegrationTests.Firmas;

/// <summary>
/// Spike de PLAN-FIRMA-DIGITAL-PDF.md § 5 fase 1: ¿puede PDFsharp 6.2.4
/// (ya en el proyecto) localizar la firma de un PDF en lectura — el
/// /ByteRange y el /Contents del diccionario de firma — con la fidelidad
/// suficiente para verificarla criptográficamente con SignedCms?
///
/// El PDF firmado se genera aquí mismo (PDFsharp sabe firmar y el
/// certificado de prueba se crea con CertificateRequest), manteniendo la
/// convención del repo de no versionar binarios de prueba. El /Contents se
/// extrae de los bytes crudos del hueco que el propio /ByteRange define —
/// no de la PdfString decodificada — porque es el único camino que no
/// depende de cómo trate PDFsharp un literal hexadecimal binario; el
/// /ByteRange sí se lee del modelo de objetos (enteros, sin ambigüedad).
/// </summary>
public class LecturaFirmaPdfSpikeTests
{
    static LecturaFirmaPdfSpikeTests()
    {
        GlobalFontSettings.FontResolver ??= new EmbeddedFontResolver();
    }

    [Fact]
    public void PdfSharp_localiza_el_ByteRange_y_SignedCms_verifica_la_firma()
    {
        using var certificado = CrearCertificadoDePrueba();
        var pdfFirmado = CrearPdfFirmado(certificado, "Certificado de prueba firmado");

        var firma = LocalizarPrimeraFirma(pdfFirmado);

        firma.ByteRange.Should().HaveCount(4);
        firma.ByteRange[0].Should().Be(0);
        // El ByteRange cubre el archivo entero salvo el hueco de /Contents.
        (firma.ByteRange[2] + firma.ByteRange[3]).Should().Be(pdfFirmado.Length);

        var cms = new SignedCms(new ContentInfo(ExtraerRangoFirmado(pdfFirmado, firma.ByteRange)), detached: true);
        cms.Decode(firma.ContenidoCms);

        var verificar = () => cms.CheckSignature(verifySignatureOnly: true);
        verificar.Should().NotThrow("la firma recién creada debe verificar sobre los bytes del ByteRange");
        cms.SignerInfos.Count.Should().Be(1);
        cms.SignerInfos[0].Certificate!.Subject.Should().Contain("Spike CAE Manager");
    }

    [Fact]
    public void Un_byte_alterado_dentro_del_rango_firmado_rompe_la_verificacion()
    {
        using var certificado = CrearCertificadoDePrueba();
        var pdfFirmado = CrearPdfFirmado(certificado, "Documento que sera manipulado");

        var firma = LocalizarPrimeraFirma(pdfFirmado);

        // Se altera un byte del contenido de la primera página, bien dentro
        // del primer tramo del ByteRange — simula editar el PDF tras firmarlo.
        var manipulado = (byte[])pdfFirmado.Clone();
        manipulado[firma.ByteRange[1] / 2] ^= 0xFF;

        var cms = new SignedCms(new ContentInfo(ExtraerRangoFirmado(manipulado, firma.ByteRange)), detached: true);
        cms.Decode(firma.ContenidoCms);

        var verificar = () => cms.CheckSignature(verifySignatureOnly: true);
        verificar.Should().Throw<CryptographicException>();
    }

    [Fact]
    public void Un_pdf_sin_firma_no_tiene_diccionario_de_firma()
    {
        var pdf = CrearPdfSinFirmar("Documento corriente sin firmar");

        LocalizarFirmas(pdf).Should().BeEmpty();
    }

    private sealed record FirmaLocalizada(int[] ByteRange, byte[] ContenidoCms, string SubFilter);

    private static FirmaLocalizada LocalizarPrimeraFirma(byte[] pdf) =>
        LocalizarFirmas(pdf).Should().ContainSingle().Subject;

    /// <summary>
    /// El camino de lectura que usaría el verificador real: modelo de objetos
    /// de PDFsharp para llegar a AcroForm → Fields → /FT /Sig → /V, y de ahí
    /// el /ByteRange como enteros; el CMS sale de los bytes crudos del hueco.
    /// </summary>
    private static IReadOnlyList<FirmaLocalizada> LocalizarFirmas(byte[] pdf)
    {
        using var flujo = new MemoryStream(pdf);
        using var documento = PdfReader.Open(flujo, PdfDocumentOpenMode.Import);

        var acroForm = documento.Internals.Catalog.Elements.GetDictionary("/AcroForm");
        if (acroForm is null) return [];

        var campos = acroForm.Elements.GetArray("/Fields");
        if (campos is null) return [];

        var firmas = new List<FirmaLocalizada>();
        foreach (var elemento in campos)
        {
            var campo = elemento is PdfReference referencia ? referencia.Value as PdfDictionary : elemento as PdfDictionary;
            if (campo?.Elements.GetName("/FT") != "/Sig") continue;

            var valorFirma = campo.Elements.GetDictionary("/V");
            var byteRangeArray = valorFirma?.Elements.GetArray("/ByteRange");
            if (valorFirma is null || byteRangeArray is null) continue;

            var byteRange = byteRangeArray.Elements.Items
                .Select(item => ((PdfInteger)item).Value)
                .ToArray();

            firmas.Add(new FirmaLocalizada(
                byteRange,
                ExtraerContenidoCmsDelHueco(pdf, byteRange),
                valorFirma.Elements.GetName("/SubFilter")));
        }

        return firmas;
    }

    /// <summary>
    /// El /Contents vive en el hueco entre los dos tramos del /ByteRange,
    /// escrito como literal hexadecimal (&lt;...&gt;). Se decodifica de los bytes
    /// crudos del archivo, no de la PdfString, para no depender de la
    /// codificación de cadenas de PDFsharp con binario arbitrario.
    /// </summary>
    private static byte[] ExtraerContenidoCmsDelHueco(byte[] pdf, int[] byteRange)
    {
        var inicioHueco = byteRange[0] + byteRange[1];
        var finHueco = byteRange[2];
        var hueco = Encoding.ASCII.GetString(pdf, inicioHueco, finHueco - inicioHueco).Trim();

        hueco.Should().StartWith("<").And.EndWith(">", "el /Contents de una firma se escribe como literal hexadecimal");

        // El relleno de ceros a la derecha (espacio reservado sin usar) es
        // parte del literal y Convert lo tolera; SignedCms ignora el sobrante
        // porque el DER declara su propia longitud.
        return Convert.FromHexString(hueco[1..^1]);
    }

    private static byte[] ExtraerRangoFirmado(byte[] pdf, int[] byteRange)
    {
        var rango = new byte[byteRange[1] + byteRange[3]];
        Array.Copy(pdf, byteRange[0], rango, 0, byteRange[1]);
        Array.Copy(pdf, byteRange[2], rango, byteRange[1], byteRange[3]);
        return rango;
    }

    private static X509Certificate2 CrearCertificadoDePrueba()
    {
        using var rsa = RSA.Create(2048);
        var peticion = new CertificateRequest(
            "CN=Spike CAE Manager, O=Pruebas", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return peticion.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    }

    private static byte[] CrearPdfFirmado(X509Certificate2 certificado, string texto)
    {
        using var documento = new PdfDocument();
        var pagina = documento.AddPage();
        using (var graficos = XGraphics.FromPdfPage(pagina))
        {
            var fuente = new XFont(EmbeddedFontResolver.NombreFuente, 12);
            graficos.DrawString(texto, fuente, XBrushes.Black, new XPoint(50, 50));
        }

        DigitalSignatureHandler.ForDocument(
            documento,
            new PdfSharpDefaultSigner(certificado, PdfMessageDigestType.SHA256),
            new DigitalSignatureOptions
            {
                Reason = "Spike de lectura de firmas",
                Location = "Tests",
                Rectangle = new XRect(50, 100, 200, 50),
            });

        using var salida = new MemoryStream();
        documento.Save(salida);
        return salida.ToArray();
    }

    private static byte[] CrearPdfSinFirmar(string texto)
    {
        using var documento = new PdfDocument();
        var pagina = documento.AddPage();
        using (var graficos = XGraphics.FromPdfPage(pagina))
        {
            var fuente = new XFont(EmbeddedFontResolver.NombreFuente, 12);
            graficos.DrawString(texto, fuente, XBrushes.Black, new XPoint(50, 50));
        }
        using var salida = new MemoryStream();
        documento.Save(salida);
        return salida.ToArray();
    }
}
