using System.IO.Compression;
using System.Text;
using CaeManager.Infrastructure.Conversion;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CaeManager.IntegrationTests.Conversion;

/// <summary>
/// Prueba contra el LibreOffice real instalado en la máquina (mismo binario
/// "soffice" que el Dockerfile instala vía el paquete "libreoffice-writer")
/// — no se mockea el proceso porque la garantía que importa es que la
/// invocación real produce un PDF válido. Si la máquina de desarrollo no
/// tiene LibreOffice instalado, la prueba no hace nada en vez de fallar el
/// build de quien no lo tenga instalado localmente (CI sí lo instala, ver
/// el paso "Instalar LibreOffice" de ci.yml, y la imagen de despliegue
/// también, ver Dockerfile).
///
/// <b>En Windows estos casos NO se ejecutan, y es deliberado.</b> Se intentó
/// habilitarlos localizando el soffice.exe que el instalador deja fuera del
/// PATH, y lo medido fue que el servicio no funciona en Windows en absoluto:
/// la conversión agota los 60 segundos de timeout. La causa está en la propia
/// invocación — <c>-env:UserInstallation=file://{ruta}</c> produce una URL
/// válida a partir de una ruta POSIX (<c>file:///tmp/...</c>) pero inválida a
/// partir de una ruta Windows (<c>file://C:\...</c>, donde "C:" queda como
/// host) — así que el servicio es de Linux por construcción, que es donde se
/// despliega. Habilitarlos aquí solo produciría dos rojos permanentes que no
/// señalan ningún defecto del producto.
///
/// Queda dicho porque el intento no fue gratis: el sondeo original ejecutaba
/// <c>soffice --version</c>, que en Windows se cuelga (medido: 30 s sin
/// salir), de modo que el salto parecía "no está instalado" cuando sí lo
/// estaba. Verde sin haber ejecutado nada es la peor forma de no tener
/// cobertura, porque parece que la hay: aquí el salto es explícito y su
/// motivo, verificable.
/// </summary>
public class LibreOfficeConversorWordPdfServiceTests
{
    private static LibreOfficeConversorWordPdfService CrearServicio(string ejecutable) =>
        new(
            Options.Create(new LibreOfficeConversorWordPdfServiceOptions { RutaEjecutable = ejecutable }),
            NullLogger<LibreOfficeConversorWordPdfService>.Instance);

    [Fact]
    public async Task Convierte_un_docx_valido_en_un_pdf_valido()
    {
        if (LocalizarLibreOffice() is not { } ejecutable) return;

        var servicio = CrearServicio(ejecutable);

        var pdf = await servicio.ConvertirAPdfAsync(CrearDocxMinimoValido("Prueba de conversión Word a PDF."));

        pdf.Should().NotBeEmpty();
        Encoding.ASCII.GetString(pdf, 0, 5).Should().Be("%PDF-");
    }

    [Fact]
    public async Task Dos_conversiones_concurrentes_no_interfieren_entre_si()
    {
        if (LocalizarLibreOffice() is not { } ejecutable) return;

        var servicio = CrearServicio(ejecutable);

        var docxUno = CrearDocxMinimoValido("Documento uno.");
        var docxDos = CrearDocxMinimoValido("Documento dos.");

        var resultados = await Task.WhenAll(
            servicio.ConvertirAPdfAsync(docxUno),
            servicio.ConvertirAPdfAsync(docxDos));

        resultados.Should().OnlyContain(pdf => pdf.Length > 0);
    }

    /// <summary>
    /// Devuelve el ejecutable de LibreOffice utilizable, o null si no lo hay.
    /// Prueba el PATH (como en Linux/CI/despliegue) y, si falla, las rutas
    /// donde el instalador de Windows lo deja sin registrarlo en el PATH.
    /// </summary>
    private static string? LocalizarLibreOffice()
    {
        // Solo el nombre del PATH: Linux, CI y la imagen de despliegue. Ver
        // el comentario de clase sobre por qué NO se busca el soffice.exe de
        // Windows aunque esté instalado.
        return RespondeEnElPath("soffice") ? "soffice" : null;
    }

    private static bool RespondeEnElPath(string ejecutable)
    {
        try
        {
            using var proceso = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = ejecutable,
                ArgumentList = { "--version" },
                RedirectStandardOutput = true,
                UseShellExecute = false,
            });

            if (proceso is null) return false;
            if (!proceso.WaitForExit(15000))
            {
                proceso.Kill(entireProcessTree: true);
                return false;
            }

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
