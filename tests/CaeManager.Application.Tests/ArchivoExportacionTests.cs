using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CaeManager.Application.Exportacion;
using FluentAssertions;

namespace CaeManager.Application.Tests;

public class ArchivoExportacionTests
{
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
        using (var archivo = new ArchivoExportacion(destino, tenant, new RelojFijo(fecha)))
        {
            await archivo.AgregarAsync("datos/Empresa.json", new MemoryStream(bytes));
            await archivo.EscribirAsync("documentos/1/documento.pdf", salida => salida.WriteAsync(binario).AsTask());
            archivo.RegistrarRecuento("Empresa", 1);
            archivo.RegistrarDocumentoSinFichero(sinFichero);
            await archivo.TerminarAsync();
        }

        destino.Position = 0;
        using var zip = new ZipArchive(destino, ZipArchiveMode.Read);
        zip.Entries.Select(e => e.FullName).Should().BeEquivalentTo(
            ["datos/Empresa.json", "documentos/1/documento.pdf", ArchivoExportacion.RutaManifiesto]);

        using var manifiesto = await JsonDocument.ParseAsync(zip.GetEntry(ArchivoExportacion.RutaManifiesto)!.Open());
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
    }

    [Fact]
    public async Task ElHashDelManifiestoEsElDelContenidoDescomprimido()
    {
        var binario = new byte[300_000];
        Random.Shared.NextBytes(binario);
        using var destino = new MemoryStream();
        using (var archivo = new ArchivoExportacion(destino, Guid.NewGuid()))
        {
            await archivo.AgregarAsync("documentos/a/documento.pdf", new MemoryStream(binario));
            await archivo.TerminarAsync();
        }

        destino.Position = 0;
        using var zip = new ZipArchive(destino, ZipArchiveMode.Read);
        using var copia = new MemoryStream();
        await zip.GetEntry("documentos/a/documento.pdf")!.Open().CopyToAsync(copia);
        using var manifiesto = await JsonDocument.ParseAsync(zip.GetEntry(ArchivoExportacion.RutaManifiesto)!.Open());

        copia.ToArray().Should().Equal(binario);
        manifiesto.RootElement.GetProperty("sha256").GetProperty("documentos/a/documento.pdf").GetString()
            .Should().Be(Convert.ToHexString(SHA256.HashData(copia.ToArray())).ToLowerInvariant());
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
        using var archivo = new ArchivoExportacion(new MemoryStream(), Guid.NewGuid());
        await Assert.ThrowsAsync<ArgumentException>(() => archivo.AgregarAsync(ruta, Stream.Null));
    }

    [Fact]
    public async Task RechazaRutaDuplicadaYEscribirTrasElManifiesto()
    {
        using var archivo = new ArchivoExportacion(new MemoryStream(), Guid.NewGuid());
        await archivo.AgregarAsync("datos/Empresa.json", Stream.Null);

        await Assert.ThrowsAsync<ArgumentException>(() => archivo.AgregarAsync("datos/Empresa.json", Stream.Null));

        await archivo.TerminarAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => archivo.AgregarAsync("datos/Otra.json", Stream.Null));
        Assert.Throws<InvalidOperationException>(() => archivo.RegistrarRecuento("Otra", 1));
    }

    [Fact]
    public void ExigeTenantPropietario() =>
        Assert.Throws<ArgumentException>(() => new ArchivoExportacion(new MemoryStream(), Guid.Empty));

    [Fact]
    public void UnaFilaDeOtroTenantAbortaLaExportacion()
    {
        var pedido = Guid.NewGuid();

        GuardaTenantExportacion.Exigir(pedido, pedido, "Empresa");
        var error = Assert.Throws<InvalidOperationException>(() => GuardaTenantExportacion.Exigir(pedido, Guid.NewGuid(), "Empresa"));

        error.Message.Should().Contain("Empresa").And.NotContain(pedido.ToString());
    }

    private sealed class RelojFijo(DateTimeOffset ahora) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => ahora;
    }
}
