using CaeManager.Application.Common;
using CaeManager.Application.Common.Archivos;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Common;

/// <summary>
/// OWASP ASVS 4.0.3 V12.1/V12.2: el verificador decide por el contenido real del archivo almacenado, con la extensión de
/// la clave como única pista. Rechaza sin lanzar lo que no existe, lo que pesa más de 10 MB y lo que no es un documento
/// admitido, y acepta cada formato con su firma.
/// </summary>
public class VerificadorArchivoAlmacenadoTests
{
    private static readonly byte[] Pdf = "%PDF-1.7\n"u8.ToArray();
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00];
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00];
    private static readonly byte[] Zip = [0x50, 0x4B, 0x03, 0x04, 0x14, 0x00];
    private static readonly byte[] Ejecutable = [0x4D, 0x5A, 0x90, 0x00];

    [Theory]
    [InlineData("documento.pdf", nameof(Pdf))]
    [InlineData("foto.jpg", nameof(Jpeg))]
    [InlineData("foto.jpeg", nameof(Jpeg))]
    [InlineData("foto.png", nameof(Png))]
    [InlineData("informe.docx", nameof(Zip))]
    [InlineData("DOCUMENTO.PDF", nameof(Pdf))]
    public async Task Acepta_cada_formato_de_documento_con_su_firma(string nombre, string contenido)
    {
        var (verificador, clave) = await Preparar(nombre, Contenido(contenido));

        (await verificador.EsDocumentoValidoAsync(clave)).Should().BeTrue();
    }

    [Theory]
    [InlineData("documento.pdf", nameof(Ejecutable))]
    [InlineData("foto.jpg", nameof(Pdf))]
    [InlineData("foto.png", nameof(Zip))]
    [InlineData("informe.docx", nameof(Pdf))]
    [InlineData("programa.exe", nameof(Ejecutable))]
    [InlineData("paquete.zip", nameof(Zip))]
    [InlineData("pagina.html", nameof(Pdf))]
    [InlineData("sin-extension", nameof(Pdf))]
    public async Task Rechaza_la_firma_que_no_coincide_con_la_extension_y_las_extensiones_ajenas(string nombre, string contenido)
    {
        var (verificador, clave) = await Preparar(nombre, Contenido(contenido));

        (await verificador.EsDocumentoValidoAsync(clave)).Should().BeFalse();
    }

    [Fact]
    public async Task Rechaza_un_archivo_vacio()
    {
        var (verificador, clave) = await Preparar("documento.pdf", []);

        (await verificador.EsDocumentoValidoAsync(clave)).Should().BeFalse();
    }

    [Fact]
    public async Task Acepta_un_PDF_unificado_de_mas_de_10_MB_porque_el_tope_por_archivo_se_aplico_al_subir_cada_uno()
    {
        // La subida múltiple une varios archivos de hasta 10 MB cada uno en un solo PDF: ese PDF guardado puede pasar
        // legítimamente del tope por archivo y no puede rechazarse como «no válido» al guardar el Documento.
        var grande = new byte[LimitesArchivoSubido.TamanoMaximoBytes * 2];
        Pdf.CopyTo(grande, 0);
        var (verificador, clave) = await Preparar("documento.pdf", grande);

        (await verificador.EsDocumentoValidoAsync(clave)).Should().BeTrue();
    }

    [Fact]
    public async Task Un_archivo_corto_o_de_un_solo_byte_no_lanza_y_se_rechaza()
    {
        var (verificador, clave) = await Preparar("documento.pdf", [0x25]);

        (await verificador.EsDocumentoValidoAsync(clave)).Should().BeFalse();
    }

    [Theory]
    [InlineData(typeof(FileNotFoundException))]
    [InlineData(typeof(DirectoryNotFoundException))]
    [InlineData(typeof(KeyNotFoundException))]
    [InlineData(typeof(InvalidDataException))] // el blob no supera la integridad del almacenamiento cifrado
    [InlineData(typeof(System.Security.Cryptography.CryptographicException))]
    public async Task Rechaza_sin_lanzar_cuando_el_almacenamiento_no_puede_entregar_un_contenido_integro(Type excepcion)
    {
        var verificador = new VerificadorArchivoAlmacenado(new AlmacenQueFalla((Exception)Activator.CreateInstance(excepcion)!));

        (await verificador.EsDocumentoValidoAsync("tenant/abc.pdf")).Should().BeFalse();
    }

    [Fact]
    public async Task Un_fallo_de_infraestructura_del_almacenamiento_no_se_confunde_con_un_archivo_invalido()
    {
        var verificador = new VerificadorArchivoAlmacenado(new AlmacenQueFalla(new IOException("disco caido")));

        var accion = async () => await verificador.EsDocumentoValidoAsync("tenant/abc.pdf");

        await accion.Should().ThrowAsync<IOException>("un fallo transitorio de infraestructura no debe decirle al usuario que su archivo es falso");
    }

    [Theory]
    [InlineData("falso-99-inexistente.pdf")]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Rechaza_sin_lanzar_una_clave_que_no_existe_o_esta_en_blanco(string clave)
    {
        var verificador = new VerificadorArchivoAlmacenado(new FileStorageServiceFalso());

        (await verificador.EsDocumentoValidoAsync(clave)).Should().BeFalse();
    }


    private static byte[] Contenido(string nombre) => nombre switch
    {
        nameof(Pdf) => Pdf,
        nameof(Jpeg) => Jpeg,
        nameof(Png) => Png,
        nameof(Zip) => Zip,
        _ => Ejecutable
    };

    private static async Task<(VerificadorArchivoAlmacenado Verificador, string Clave)> Preparar(string nombre, byte[] contenido)
    {
        var almacen = new FileStorageServiceFalso();
        var clave = await almacen.GuardarAsync(new MemoryStream(contenido), nombre);
        return (new VerificadorArchivoAlmacenado(almacen), clave);
    }

    private sealed class AlmacenQueFalla(Exception excepcion) : IFileStorageService
    {
        public Task<string> GuardarAsync(Stream contenido, string nombreArchivoOriginal, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Stream> AbrirAsync(string identificador, CancellationToken cancellationToken = default) =>
            throw excepcion;

        public Task EliminarAsync(string identificador, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
