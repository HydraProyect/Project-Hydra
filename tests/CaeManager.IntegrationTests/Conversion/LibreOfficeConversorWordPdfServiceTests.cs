using System.IO.Compression;
using System.Text;
using CaeManager.Infrastructure.Conversion;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CaeManager.IntegrationTests.Conversion;

/// <summary>
/// Prueba contra el LibreOffice real instalado en la máquina (mismo binario
/// "soffice" que el Dockerfile instala vía el paquete "libreoffice-writer")
/// — no se mockea el proceso porque la garantía que importa es que la
/// invocación real produce un PDF válido. Si la máquina de desarrollo no
/// tiene LibreOffice instalado, la prueba no hace nada en vez de fallar el
/// build de quien no lo tenga instalado localmente (Railway sí lo tiene,
/// ver Dockerfile).
/// </summary>
public class LibreOfficeConversorWordPdfServiceTests
{
    [Fact]
    public async Task Convierte_un_docx_valido_en_un_pdf_valido()
    {
        if (!HayLibreOfficeInstalado()) return;

        var servicio = new LibreOfficeConversorWordPdfService(
            Options.Create(new LibreOfficeConversorWordPdfServiceOptions()));

        var pdf = await servicio.ConvertirAPdfAsync(CrearDocxMinimoValido("Prueba de conversión Word a PDF."));

        pdf.Should().NotBeEmpty();
        Encoding.ASCII.GetString(pdf, 0, 5).Should().Be("%PDF-");
    }

    [Fact]
    public async Task Dos_conversiones_concurrentes_no_interfieren_entre_si()
    {
        if (!HayLibreOfficeInstalado()) return;

        var servicio = new LibreOfficeConversorWordPdfService(
            Options.Create(new LibreOfficeConversorWordPdfServiceOptions()));

        var docxUno = CrearDocxMinimoValido("Documento uno.");
        var docxDos = CrearDocxMinimoValido("Documento dos.");

        var resultados = await Task.WhenAll(
            servicio.ConvertirAPdfAsync(docxUno),
            servicio.ConvertirAPdfAsync(docxDos));

        resultados.Should().OnlyContain(pdf => pdf.Length > 0);
    }

    private static bool HayLibreOfficeInstalado()
    {
        try
        {
            using var proceso = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "soffice",
                ArgumentList = { "--version" },
                RedirectStandardOutput = true,
                UseShellExecute = false,
            });
            proceso!.WaitForExit(5000);
            return proceso.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Construye a mano el .docx mínimo que Word/LibreOffice reconocen como válido, sin depender de ninguna librería de generación de Office.</summary>
    private static byte[] CrearDocxMinimoValido(string texto)
    {
        using var memoria = new MemoryStream();
        using (var zip = new ZipArchive(memoria, ZipArchiveMode.Create, leaveOpen: true))
        {
            EscribirEntrada(zip, "[Content_Types].xml", $"""
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
                <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
                <Default Extension="xml" ContentType="application/xml"/>
                <Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/>
                </Types>
                """);

            EscribirEntrada(zip, "_rels/.rels", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/>
                </Relationships>
                """);

            EscribirEntrada(zip, "word/document.xml", $"""
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
                <w:body><w:p><w:r><w:t>{texto}</w:t></w:r></w:p></w:body>
                </w:document>
                """);
        }

        return memoria.ToArray();
    }

    private static void EscribirEntrada(ZipArchive zip, string nombre, string contenido)
    {
        var entrada = zip.CreateEntry(nombre);
        using var escritor = new StreamWriter(entrada.Open(), Encoding.UTF8);
        escritor.Write(contenido);
    }
}
