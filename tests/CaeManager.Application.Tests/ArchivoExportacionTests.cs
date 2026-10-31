using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CaeManager.Application.Exportacion;
using FluentAssertions;
using ICSharpCode.SharpZipLib.Zip;

namespace CaeManager.Application.Tests;

public class ArchivoExportacionTests
{
    private const string Contrasena = "contrasena-de-prueba-suficiente";

    [Fact]
    public async Task ManifiestoContieneFechaTenantRecuentoYHashDelContenido()
    {
        var tenant = Guid.NewGuid();
        var sinFichero = Guid.NewGuid();
        var fecha = new DateTimeOffset(2026, 10, 27, 9, 30, 0, TimeSpan.Zero);
        var bytes = Encoding.UTF8.GetBytes("[{\"Id\":1}]");
        var binario = new byte[200_000];
        Random.Shared.NextBytes(binario);
        using var destino = new MemoryStream();
        using (var archivo = new ArchivoExportacion(destino, tenant, Contrasena, new RelojFijo(fecha)))
        {
            await archivo.AgregarAsync("datos/Empresa.json", new MemoryStream(bytes));
            await archivo.EscribirAsync("documentos/1/documento.pdf", salida => salida.WriteAsync(binario).AsTask());
            archivo.RegistrarRecuento("Empresa", 1);
            archivo.RegistrarDocumentoSinFichero(sinFichero);
            archivo.RegistrarNoIncluido("Filas con borrado lógico", "El filtro global las oculta.");
            await archivo.TerminarAsync();
        }

        using var zip = Abrir(destino, Contrasena);
        zip.Cast<ZipEntry>().Select(e => e.Name).Should().BeEquivalentTo(
            ["datos/Empresa.json", "documentos/1/documento.pdf", ArchivoExportacion.RutaManifiesto]);

        using var manifiesto = await JsonDocument.ParseAsync(zip.GetInputStream(zip.GetEntry(ArchivoExportacion.RutaManifiesto)));
        var raiz = manifiesto.RootElement;
        raiz.GetProperty("tenantId").GetGuid().Should().Be(tenant);
        raiz.GetProperty("fechaUtc").GetDateTimeOffset().Should().Be(fecha);
        raiz.GetProperty("recuentos").GetProperty("Empresa").GetInt32().Should().Be(1);
        raiz.GetProperty("sha256").GetProperty("datos/Empresa.json").GetString()
            .Should().Be(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        raiz.GetProperty("sha256").GetProperty("documentos/1/documento.pdf").GetString()
            .Should().Be(Convert.ToHexString(SHA256.HashData(binario)).ToLowerInvariant());
        raiz.GetProperty("sha256").TryGetProperty(ArchivoExportacion.RutaManifiesto, out _).Should().BeFalse();
        raiz.GetProperty("documentosSinFichero").EnumerateArray().Select(e => e.GetGuid()).Should().Equal(sinFichero);
        raiz.GetProperty("noIncluido").GetProperty("Filas con borrado lógico").GetString().Should().Be("El filtro global las oculta.");
    }

    [Fact]
    public async Task ElHashDelManifiestoEsElDelContenidoDescomprimido()
    {
        var binario = new byte[300_000];
        Random.Shared.NextBytes(binario);
        using var destino = new MemoryStream();
        using (var archivo = new ArchivoExportacion(destino, Guid.NewGuid(), Contrasena))
        {
            await archivo.AgregarAsync("documentos/a/documento.pdf", new MemoryStream(binario));
            await archivo.TerminarAsync();
        }

        using var zip = Abrir(destino, Contrasena);
        using var copia = new MemoryStream();
        await zip.GetInputStream(zip.GetEntry("documentos/a/documento.pdf")).CopyToAsync(copia);
        using var manifiesto = await JsonDocument.ParseAsync(zip.GetInputStream(zip.GetEntry(ArchivoExportacion.RutaManifiesto)));

        copia.ToArray().Should().Equal(binario);
        manifiesto.RootElement.GetProperty("sha256").GetProperty("documentos/a/documento.pdf").GetString()
            .Should().Be(Convert.ToHexString(SHA256.HashData(copia.ToArray())).ToLowerInvariant());
    }

    [Fact]
    public async Task CadaEntradaVaEnAes256YElContenidoNoEstaEnClaro()
    {
        var marca = Encoding.UTF8.GetBytes("marca-que-no-puede-verse-en-claro-" + new string('x', 200));
        using var destino = new MemoryStream();
        using (var archivo = new ArchivoExportacion(destino, Guid.NewGuid(), Contrasena))
        {
            await archivo.AgregarAsync("datos/Empresa.json", new MemoryStream(marca));
            await archivo.TerminarAsync();
        }

        using var zip = Abrir(destino, Contrasena);
        zip.Cast<ZipEntry>().Should().HaveCount(2).And.OnlyContain(e => e.IsCrypted && e.AESKeySize == 256);

        using var sinContrasena = Abrir(destino, null);
        Assert.Throws<ZipException>(() => sinContrasena.GetInputStream(sinContrasena.GetEntry("datos/Empresa.json")));
    }

    [Fact]
    public async Task UnJsonGrandeConVolcadosAMitadDeEntradaSeLeeIgual()
    {
        var filas = Enumerable.Range(0, 40_000).Select(i => $"fila {i}").ToArray();
        using var destino = new MemoryStream();
        using (var archivo = new ArchivoExportacion(destino, Guid.NewGuid(), Contrasena))
        {
            await archivo.EscribirAsync("datos/Empresa.json", async salida =>
            {
                await using var escritor = new Utf8JsonWriter(salida);
                escritor.WriteStartArray();
                foreach (var fila in filas)
                {
                    escritor.WriteStringValue(fila);
                    if (escritor.BytesPending > 16 * 1024)
                        await escritor.FlushAsync();
                }
                escritor.WriteEndArray();
            });
            await archivo.TerminarAsync();
        }

        using var zip = Abrir(destino, Contrasena);
        var leidas = await JsonSerializer.DeserializeAsync<string[]>(zip.GetInputStream(zip.GetEntry("datos/Empresa.json")));

        leidas.Should().Equal(filas);
    }

    [Fact]
    public async Task UnaContrasenaEquivocadaNoAbreElContenido()
    {
        using var destino = new MemoryStream();
        using (var archivo = new ArchivoExportacion(destino, Guid.NewGuid(), Contrasena))
        {
            await archivo.AgregarAsync("datos/Empresa.json", new MemoryStream([1, 2, 3]));
            await archivo.TerminarAsync();
        }

        using var zip = Abrir(destino, "otra-contrasena-de-la-misma-talla");

        Assert.Throws<ZipException>(() => zip.GetInputStream(zip.GetEntry("datos/Empresa.json")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("corta")]
    public void NoHayExportacionSinContrasena(string contrasena) =>
        Assert.Throws<ArgumentException>(() => new ArchivoExportacion(new MemoryStream(), Guid.NewGuid(), contrasena));

    [Fact]
    public void LaContrasenaGeneradaEsLargaYCadaExportacionTieneLaSuya()
    {
        var generadas = Enumerable.Range(0, 50).Select(_ => ArchivoExportacion.GenerarContrasena()).ToList();

        generadas.Should().OnlyHaveUniqueItems().And.OnlyContain(c => c.Length == 32 && c.All(char.IsAsciiLetterOrDigit));
    }

    [Theory]
    [InlineData("../secreto")]
    [InlineData("/absoluta")]
    [InlineData("documentos/../otro")]
    [InlineData("documentos//doble")]
    [InlineData("documentos/./x")]
    [InlineData("documentos\\x")]
    [InlineData("C:/x")]
    [InlineData("manifiesto.json")]
    [InlineData(" ")]
    public async Task RechazaRutasInseguras(string ruta)
    {
        using var archivo = new ArchivoExportacion(new MemoryStream(), Guid.NewGuid(), Contrasena);
        await Assert.ThrowsAsync<ArgumentException>(() => archivo.AgregarAsync(ruta, Stream.Null));
    }

    [Fact]
    public async Task RechazaRutaDuplicadaYEscribirTrasElManifiesto()
    {
        using var archivo = new ArchivoExportacion(new MemoryStream(), Guid.NewGuid(), Contrasena);
        await archivo.AgregarAsync("datos/Empresa.json", Stream.Null);

        await Assert.ThrowsAsync<ArgumentException>(() => archivo.AgregarAsync("datos/Empresa.json", Stream.Null));

        await archivo.TerminarAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => archivo.AgregarAsync("datos/Otra.json", Stream.Null));
        Assert.Throws<InvalidOperationException>(() => archivo.RegistrarRecuento("Otra", 1));
    }

    [Fact]
    public void ExigeTenantPropietario() =>
        Assert.Throws<ArgumentException>(() => new ArchivoExportacion(new MemoryStream(), Guid.Empty, Contrasena));

    [Fact]
    public void UnaFilaDeOtroTenantAbortaLaExportacion()
    {
        var pedido = Guid.NewGuid();

        GuardaTenantExportacion.Exigir(pedido, pedido, "Empresa");
        var error = Assert.Throws<InvalidOperationException>(() => GuardaTenantExportacion.Exigir(pedido, Guid.NewGuid(), "Empresa"));

        error.Message.Should().Contain("Empresa").And.NotContain(pedido.ToString());
    }

    private static ZipFile Abrir(MemoryStream destino, string? contrasena)
    {
        destino.Position = 0;
        return new ZipFile(destino, leaveOpen: true) { Password = contrasena };
    }

    private sealed class RelojFijo(DateTimeOffset ahora) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => ahora;
    }
}
