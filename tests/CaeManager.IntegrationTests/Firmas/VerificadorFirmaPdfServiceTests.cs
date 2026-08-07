using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using CaeManager.Application.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Infrastructure.Firmas;
using CaeManager.Web.Reportes;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Signatures;
using Xunit;

namespace CaeManager.IntegrationTests.Firmas;

/// <summary>
/// El motor de verificación se prueba con PDFs firmados generados en el
/// propio test (PDFsharp + certificados de CertificateRequest — cero
/// binarios versionados, patrón de LecturaFirmaPdfSpikeTests). Sin red: los
/// certificados de prueba no publican OCSP/CRL, así que la cadena degrada a
/// "revocación no disponible", que es exactamente el camino del plan § 4.
/// </summary>
public class VerificadorFirmaPdfServiceTests
{
    static VerificadorFirmaPdfServiceTests()
    {
        GlobalFontSettings.FontResolver ??= new EmbeddedFontResolver();
    }

    [Fact]
    public async Task Un_pdf_sin_firma_es_solo_lectura_sin_ser_un_error()
    {
        var servicio = CrearServicio(new X509Certificate2Collection());

        var resultado = await servicio.VerificarAsync(CrearPdfSinFirmar());

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.Nivel.Should().Be(NivelConfianzaDocumental.SoloLectura);
        resultado.Valor.Firmas.Should().BeEmpty();
        // Tiene texto con fuente embebida y ningún Producer de impresión: no
        // aparenta reimpresión, solo es un PDF sin firmar.
        resultado.Valor.AparentaReimpresion.Should().BeFalse();
    }

    [Fact]
    public async Task Un_pdf_sin_firma_y_sin_fuentes_aparenta_reimpresion()
    {
        // Página solo con un rectángulo — cero fuentes embebidas, el patrón
        // de una reimpresión/escaneo rasterizado (calibrado con muestras
        // reales de gestorías).
        using var documento = new PdfDocument();
        var pagina = documento.AddPage();
        using (var graficos = XGraphics.FromPdfPage(pagina))
            graficos.DrawRectangle(XBrushes.Gray, 10, 10, 200, 100);
        using var salida = new MemoryStream();
        documento.Save(salida);

        var resultado = await CrearServicio(new X509Certificate2Collection()).VerificarAsync(salida.ToArray());

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.Nivel.Should().Be(NivelConfianzaDocumental.SoloLectura);
        resultado.Valor.AparentaReimpresion.Should().BeTrue();
    }

    [Fact]
    public async Task Un_archivo_que_no_es_pdf_falla_con_archivo_invalido()
    {
        var servicio = CrearServicio(new X509Certificate2Collection());

        var resultado = await servicio.VerificarAsync([0x25, 0x50, 0x44, 0x46, 0x00]);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("VerificadorFirmaPdf.ArchivoInvalido");
    }

    [Fact]
    public async Task Una_firma_integra_de_emisor_fuera_del_almacen_no_es_confiable()
    {
        using var certificado = CrearCertificadoAutofirmado("CN=Firmante Desconocido");
        var pdf = CrearPdfFirmado(certificado);
        var servicio = CrearServicio(new X509Certificate2Collection());

        var resultado = await servicio.VerificarAsync(pdf);

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.Nivel.Should().Be(NivelConfianzaDocumental.FirmaIntegraEmisorNoConfiable);
        var firma = resultado.Valor.Firmas.Should().ContainSingle().Subject;
        firma.Estado.Should().Be(EstadoFirmaPdf.Valida);
        firma.CadenaConfiable.Should().BeFalse();
        firma.CubreDocumentoCompleto.Should().BeTrue();
    }

    [Fact]
    public async Task Una_firma_de_emisor_del_almacen_es_valida_con_revocacion_degradada()
    {
        using var certificado = CrearCertificadoAutofirmado("CN=Sede de Pruebas, O=Organismo de Pruebas");
        var pdf = CrearPdfFirmado(certificado);
        // El propio certificado autofirmado como raíz de confianza: cadena
        // completa sin red; sin OCSP/CRL publicados, degrada a NoDisponible.
        var servicio = CrearServicio([certificado]);

        var resultado = await servicio.VerificarAsync(pdf);

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.Nivel.Should().Be(NivelConfianzaDocumental.FirmaValidaSinRevocacion);
        var firma = resultado.Valor.Firmas.Should().ContainSingle().Subject;
        firma.CadenaConfiable.Should().BeTrue();
        firma.Revocacion.Should().Be(ComprobacionRevocacion.NoDisponible);
        firma.FirmanteNombre.Should().Be("Sede de Pruebas");
    }

    [Fact]
    public async Task Un_byte_alterado_tras_la_firma_es_manipulacion()
    {
        using var certificado = CrearCertificadoAutofirmado("CN=Firmante");
        var pdf = CrearPdfFirmado(certificado);
        // Se altera un byte dentro de los datos del primer stream (contenido
        // de página): cambia lo firmado sin romper la estructura del PDF —
        // el equivalente a retocar el documento con un editor.
        var posicionStream = IndiceDe(pdf, "stream"u8.ToArray()) + "stream\n"u8.Length + 4;
        pdf[posicionStream] ^= 0xFF;
        var servicio = CrearServicio([certificado]);

        var resultado = await servicio.VerificarAsync(pdf);

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.Nivel.Should().Be(NivelConfianzaDocumental.Manipulado);
        var firma = resultado.Valor.Firmas.Should().ContainSingle().Subject;
        firma.Estado.Should().Be(EstadoFirmaPdf.Invalida);
        firma.MotivoInvalidez.Should().NotBeNull();
    }

    [Fact]
    public async Task Un_sello_de_organo_se_reconoce_por_el_cif_del_subject()
    {
        // serialNumber con CIF de organismo público (Q...) — el patrón de un
        // sello de órgano eIDAS, a calibrar con certificados reales (PR-6).
        using var certificado = CrearCertificadoAutofirmado(
            "CN=Sello Organismo de Pruebas, O=Organismo de Pruebas, SERIALNUMBER=Q2827001H");
        var pdf = CrearPdfFirmado(certificado);
        var servicio = CrearServicio([certificado]);

        var resultado = await servicio.VerificarAsync(pdf);

        var firma = resultado.Valor.Firmas.Should().ContainSingle().Subject;
        firma.EsSelloDeOrgano.Should().BeTrue();
        firma.FirmanteNif.Should().Be("Q2827001H");
    }

    [Fact]
    public async Task Una_persona_fisica_se_reconoce_por_el_dni_del_subject()
    {
        using var certificado = CrearCertificadoAutofirmado(
            "CN=Nombre Apellido Prueba, SERIALNUMBER=IDCES-12345678Z");
        var pdf = CrearPdfFirmado(certificado);
        var servicio = CrearServicio([certificado]);

        var resultado = await servicio.VerificarAsync(pdf);

        var firma = resultado.Valor.Firmas.Should().ContainSingle().Subject;
        firma.EsSelloDeOrgano.Should().BeFalse();
        firma.FirmanteNif.Should().Be("12345678Z");
    }

    [Fact]
    public async Task La_fecha_de_firma_declarada_se_extrae_sin_contar_como_sello_de_tiempo()
    {
        using var certificado = CrearCertificadoAutofirmado("CN=Firmante");
        var pdf = CrearPdfFirmado(certificado);
        var servicio = CrearServicio([certificado]);

        var resultado = await servicio.VerificarAsync(pdf);

        var firma = resultado.Valor.Firmas.Should().ContainSingle().Subject;
        // PdfSharpDefaultSigner incluye signingTime como atributo firmado.
        firma.FechaFirmaUtc.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(5));
        firma.TieneSelloDeTiempo.Should().BeFalse();
    }

    private static VerificadorFirmaPdfService CrearServicio(X509Certificate2Collection raices) =>
        new(new AlmacenConfianzaFirmas(raices), NullLogger<VerificadorFirmaPdfService>.Instance);

    private static int IndiceDe(byte[] contenido, byte[] patron)
    {
        for (var i = 0; i <= contenido.Length - patron.Length; i++)
            if (contenido.AsSpan(i, patron.Length).SequenceEqual(patron)) return i;
        throw new InvalidOperationException("Patrón no encontrado en el PDF de prueba.");
    }

    private static X509Certificate2 CrearCertificadoAutofirmado(string subject)
    {
        using var rsa = RSA.Create(2048);
        var peticion = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return peticion.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    }

    private static byte[] CrearPdfFirmado(X509Certificate2 certificado)
    {
        using var documento = new PdfDocument();
        var pagina = documento.AddPage();
        using (var graficos = XGraphics.FromPdfPage(pagina))
        {
            var fuente = new XFont(EmbeddedFontResolver.NombreFuente, 12);
            graficos.DrawString("Documento oficial de prueba", fuente, XBrushes.Black, new XPoint(50, 50));
        }

        DigitalSignatureHandler.ForDocument(
            documento,
            new PdfSharpDefaultSigner(certificado, PdfMessageDigestType.SHA256),
            new DigitalSignatureOptions { Rectangle = new XRect(50, 100, 200, 50) });

        using var salida = new MemoryStream();
        documento.Save(salida);
        return salida.ToArray();
    }

    private static byte[] CrearPdfSinFirmar()
    {
        using var documento = new PdfDocument();
        var pagina = documento.AddPage();
        using (var graficos = XGraphics.FromPdfPage(pagina))
        {
            var fuente = new XFont(EmbeddedFontResolver.NombreFuente, 12);
            graficos.DrawString("Documento sin firmar", fuente, XBrushes.Black, new XPoint(50, 50));
        }
        using var salida = new MemoryStream();
        documento.Save(salida);
        return salida.ToArray();
    }
}
