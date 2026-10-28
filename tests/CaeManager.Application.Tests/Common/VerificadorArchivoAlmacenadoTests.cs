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
    public async Task Rechaza_un_archivo_de_mas_de_10_MB_aunque_su_firma_sea_valida()
    {
        var grande = new byte[LimitesArchivoSubido.TamanoMaximoBytes + 1];
        Pdf.CopyTo(grande, 0);
        var (verificador, clave) = await Preparar("documento.pdf", grande);

        (await verificador.EsDocumentoValidoAsync(clave)).Should().BeFalse();

        var justo = new byte[LimitesArchivoSubido.TamanoMaximoBytes];
        Pdf.CopyTo(justo, 0);
        var (verificadorJusto, claveJusta) = await Preparar("documento.pdf", justo);
        (await verificadorJusto.EsDocumentoValidoAsync(claveJusta)).Should().BeTrue("control: en el límite exacto sí pasa");
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

    [Fact]
    public async Task Rechaza_sin_lanzar_cuando_el_almacenamiento_dice_que_el_archivo_no_existe()
    {
        var verificador = new VerificadorArchivoAlmacenado(new AlmacenQueNoEncuentra());

        (await verificador.EsDocumentoValidoAsync("tenant/abc.pdf")).Should().BeFalse();
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

    private sealed class AlmacenQueNoEncuentra : IFileStorageService
    {
        public Task<string> GuardarAsync(Stream contenido, string nombreArchivoOriginal, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Stream> AbrirAsync(string identificador, CancellationToken cancellationToken = default) =>
            throw new FileNotFoundException("No encontramos el archivo solicitado.", identificador);

        public Task EliminarAsync(string identificador, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
