using System.IO.Compression;
using CaeManager.Application.Documentos.Queries.ObtenerPaqueteAcreditacionEmpresa;
using CaeManager.Web.Features.Documentos;
using ClosedXML.Excel;
using FluentAssertions;

namespace CaeManager.Web.Tests;

public class PaqueteAcreditacionZipTests
{
    /// <summary>Como el cuerpo de una respuesta HTTP: solo escritura, sin búsqueda ni Length.</summary>
    private sealed class FlujoSoloEscritura : Stream
    {
        public MemoryStream Interno { get; } = new();
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => Interno.Write(buffer, offset, count);
    }

    private static readonly Guid Doc1 = Guid.Parse("11111111-0000-0000-0000-000000000001");
    private static readonly Guid Doc2 = Guid.Parse("11111111-0000-0000-0000-000000000002");
    private static readonly Guid Doc3 = Guid.Parse("11111111-0000-0000-0000-000000000003");

    private static FilaIndicePaquete Fila(Guid id, string ruta, string? dni = null) =>
        new("Trabajador", "Perez Ana", dni, "Entrega de EPI", new DateOnly(2026, 3, 14), "No caduca", id, ruta, "incluido", null);

    private static PaqueteAcreditacionDto Paquete() => new(
        "Acme - Documentacion - 2026-10-01", new DateOnly(2026, 10, 1),
        [
            new EntradaPaquete(Doc1, "blob-1", "02-Documentacion de Trabajadores/Perez Ana/04-Entrega de EPI (2026-03-14).pdf", 0),
            new EntradaPaquete(Doc2, "blob-2", "02-Documentacion de Trabajadores/Perez Ana/01-Aptitud medica (2026-03-14).pdf", 1),
            new EntradaPaquete(Doc3, "blob-3", "01-Documentacion de Empresa/01-Seguro (2026-01-01).pdf", 2)
        ],
        [
            Fila(Doc1, "02-Documentacion de Trabajadores/Perez Ana/04-Entrega de EPI (2026-03-14).pdf", "12345678Z"),
            Fila(Doc2, "02-Documentacion de Trabajadores/Perez Ana/01-Aptitud medica (2026-03-14).pdf", "12345678Z"),
            Fila(Doc3, "01-Documentacion de Empresa/01-Seguro (2026-01-01).pdf")
        ]);

    private static Func<string, CancellationToken, Task<Stream>> Blobs(Func<string, byte[]?> contenido) =>
        (clave, _) => contenido(clave) is { } bytes
            ? Task.FromResult<Stream>(new MemoryStream(bytes))
            : throw new FileNotFoundException(clave);

    private static async Task<(ZipArchive Zip, List<Guid> Registrados)> Generar(
        PaqueteAcreditacionDto paquete, Func<string, byte[]?> contenido, long tope)
    {
        var salida = new FlujoSoloEscritura();
        var registrados = new List<Guid>();
        await PaqueteAcreditacionZip.EscribirAsync(
            salida, paquete, Blobs(contenido), (id, _) => { registrados.Add(id); return Task.CompletedTask; }, tope, CancellationToken.None);
        return (new ZipArchive(new MemoryStream(salida.Interno.ToArray()), ZipArchiveMode.Read), registrados);
    }

    private static List<string[]> FilasDelIndice(ZipArchive zip, string raiz)
    {
        using var flujo = zip.GetEntry($"{raiz}/00-Indice.xlsx")!.Open();
        using var libro = new XLWorkbook(flujo);
        return libro.Worksheet(1).RowsUsed().Skip(1)
            .Select(r => Enumerable.Range(1, 10).Select(c => r.Cell(c).GetString()).ToArray()).ToList();
    }

    [Fact]
    public async Task Escribe_en_un_flujo_sin_busqueda_la_estructura_pedida_y_el_indice_al_final()
    {
        var (zip, registrados) = await Generar(Paquete(), clave => [1, 2, 3, (byte)clave[^1]], long.MaxValue);
        using var _ = zip;
        var raiz = "Acme - Documentacion - 2026-10-01";

        zip.Entries.Select(e => e.FullName).Should().BeEquivalentTo(
        [
            $"{raiz}/02-Documentacion de Trabajadores/Perez Ana/04-Entrega de EPI (2026-03-14).pdf",
            $"{raiz}/02-Documentacion de Trabajadores/Perez Ana/01-Aptitud medica (2026-03-14).pdf",
            $"{raiz}/01-Documentacion de Empresa/01-Seguro (2026-01-01).pdf",
            $"{raiz}/00-Indice.xlsx"
        ]);
        zip.Entries.Last().FullName.Should().EndWith("00-Indice.xlsx");
        registrados.Should().Equal(Doc1, Doc2, Doc3);

        using var contenido = zip.GetEntry($"{raiz}/01-Documentacion de Empresa/01-Seguro (2026-01-01).pdf")!.Open();
        var leido = new MemoryStream();
        await contenido.CopyToAsync(leido);
        leido.ToArray().Should().Equal(1, 2, 3, (byte)'3');
    }

    [Fact]
    public async Task El_DNI_va_en_el_indice_y_en_ninguna_ruta()
    {
        var (zip, _) = await Generar(Paquete(), _ => [1], long.MaxValue);
        using var __ = zip;
        zip.Entries.Select(e => e.FullName).Should().NotContain(n => n.Contains("12345678Z"));
        FilasDelIndice(zip, "Acme - Documentacion - 2026-10-01").Select(f => f[2]).Should().Contain("12345678Z");
    }

    [Fact]
    public async Task Un_blob_que_no_se_abre_se_anota_en_el_indice_y_no_se_registra_su_acceso()
    {
        var (zip, registrados) = await Generar(Paquete(), clave => clave == "blob-2" ? null : [1], long.MaxValue);
        using var _ = zip;
        var filas = FilasDelIndice(zip, "Acme - Documentacion - 2026-10-01");

        registrados.Should().Equal(Doc1, Doc3);
        filas.Single(f => f[6] == Doc2.ToString())[8].Should().Be("excluido: archivo no disponible");
        filas.Single(f => f[6] == Doc2.ToString())[7].Should().BeEmpty();
        zip.Entries.Should().HaveCount(3);
    }

    [Fact]
    public async Task Pasado_el_tope_los_siguientes_se_dejan_fuera_y_se_anotan()
    {
        var (zip, registrados) = await Generar(Paquete(), _ => new byte[100], tope: 100);
        using var _ = zip;
        var filas = FilasDelIndice(zip, "Acme - Documentacion - 2026-10-01");

        registrados.Should().Equal(Doc1);
        filas.Single(f => f[6] == Doc1.ToString())[8].Should().Be("incluido");
        filas.Where(f => f[6] != Doc1.ToString()).Should().OnlyContain(f => f[8] == "excluido: tope de tamaño del paquete");
    }

    [Fact]
    public async Task El_indice_recoge_las_filas_excluidas_de_la_seleccion_sin_copiar_su_fichero()
    {
        var paquete = Paquete() with
        {
            Filas = [.. Paquete().Filas, new FilaIndicePaquete("Empresa", "Acme", null, "Seguro", new DateOnly(2024, 1, 1), "Vencido el 01/01/2025",
                Guid.NewGuid(), null, "excluido: vencido", null)]
        };
        var (zip, _) = await Generar(paquete, _ => [1], long.MaxValue);
        using var __ = zip;
        FilasDelIndice(zip, "Acme - Documentacion - 2026-10-01").Should().Contain(f => f[8] == "excluido: vencido");
        zip.Entries.Should().HaveCount(4);
    }
}
