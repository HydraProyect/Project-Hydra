using System.IO.Compression;
using CaeManager.Infrastructure.Importacion;
using ClosedXML.Excel;
using FluentAssertions;
using Xunit;

namespace CaeManager.Web.Tests;

/// <summary>
/// S-C-4: el tope de la pantalla de importación (5 / 20 MB) acota el .xlsx
/// comprimido, no lo que sale de él. Un .xlsx es un .zip y un paquete diminuto
/// puede declarar gigabytes de XML; ClosedXML lo cargaría entero en el proceso
/// web único que atiende a todos los Tenants. Estos casos fijan que
/// <see cref="InspectorXlsxSeguro"/> corta antes de que ClosedXML abra nada,
/// contando los bytes realmente descomprimidos y no lo que declara la cabecera.
/// </summary>
public class InspectorXlsxSeguroTests
{
    private static byte[] CrearZipDeCeros(params (string Nombre, long Bytes)[] entradas)
    {
        using var memoria = new MemoryStream();
        using (var paquete = new ZipArchive(memoria, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (nombre, bytes) in entradas)
                EscribirCeros(paquete, nombre, bytes, CompressionLevel.Optimal);
        }

        return memoria.ToArray();
    }

    private static void EscribirCeros(ZipArchive paquete, string nombre, long bytes, CompressionLevel nivel)
    {
        var entrada = paquete.CreateEntry(nombre, nivel);
        using var flujo = entrada.Open();
        var bloque = new byte[64 * 1024];
        for (long escritos = 0; escritos < bytes; escritos += bloque.Length)
            flujo.Write(bloque, 0, (int)Math.Min(bloque.Length, bytes - escritos));
    }

    private static byte[] CrearLibroValido(int filas)
    {
        using var libro = new XLWorkbook();
        var hoja = libro.Worksheets.Add("Datos");
        for (var fila = 1; fila <= filas; fila++)
        {
            hoja.Cell(fila, 1).Value = $"Trabajador {fila}";
            hoja.Cell(fila, 2).Value = $"{10000000 + fila * 7}A";
            hoja.Cell(fila, 3).Value = fila * 3.5m;
            hoja.Cell(fila, 4).Value = new DateTime(2026, 1, 1).AddDays(fila % 365);
        }

        using var memoria = new MemoryStream();
        libro.SaveAs(memoria);
        return memoria.ToArray();
    }

    [Fact]
    public void Un_xlsx_valido_pasa_y_el_flujo_queda_listo_para_abrirse()
    {
        using var flujo = new MemoryStream(CrearLibroValido(filas: 50));

        InspectorXlsxSeguro.Verificar(flujo);

        flujo.Position.Should().Be(0, "ClosedXML lee el paquete desde el principio");
        using var libro = new XLWorkbook(flujo);
        libro.Worksheet("Datos").Cell(1, 1).GetString().Should().Be("Trabajador 1");
    }

    [Fact]
    public void Un_xlsx_valido_grande_no_da_falso_positivo()
    {
        // 30.000 filas son decenas de veces más que cualquier plantilla de
        // importación real: el ratio y el tope total no pueden tocar a un
        // libro legítimo de ese tamaño.
        using var flujo = new MemoryStream(CrearLibroValido(filas: 30_000));

        var verificar = () => InspectorXlsxSeguro.Verificar(flujo);

        verificar.Should().NotThrow();
    }

    [Fact]
    public void Una_entrada_que_se_expande_por_encima_del_tope_se_rechaza_sin_agotar_memoria()
    {
        // 200 MiB de ceros caben en unos cientos de KB: muy por debajo de
        // cualquier tope de subida, y el doble del tope descomprimido.
        var bomba = CrearZipDeCeros(("xl/worksheets/sheet1.xml", 200L * 1024 * 1024));
        bomba.Length.Should().BeLessThan(5 * 1024 * 1024,
            "el paquete de entrada tiene que pasar el tope de subida para que el caso sea el real");
        using var flujo = new MemoryStream(bomba);

        var antes = GC.GetAllocatedBytesForCurrentThread();
        var verificar = () => InspectorXlsxSeguro.Verificar(flujo);
        verificar.Should().ThrowExactly<InvalidDataException>();
        var asignados = GC.GetAllocatedBytesForCurrentThread() - antes;

        // Se corta al pasar el umbral del ratio (~1 MiB leído), no tras
        // expandir los 200 MiB: lo asignado es el búfer y poco más.
        asignados.Should().BeLessThan(16L * 1024 * 1024,
            "la inspección descarta los bytes según los lee en vez de materializar el contenido");
        flujo.Position.Should().Be(0);
    }

    [Fact]
    public void Una_cabecera_que_declara_menos_no_hace_que_se_descomprima_mas_de_lo_declarado()
    {
        // Quien fabrica el archivo elige lo que declara el directorio central:
        // aquí la bomba de 200 MiB declara 1.000 bytes. System.IO.Compression
        // (la misma capa que usa ClosedXML) corta la lectura de la entrada en
        // lo declarado, así que mentir a la baja no sirve para colar bytes: la
        // inspección termina sin expandir nada y sin materializar memoria. La
        // mentira que sí importa, declarar poco y traer mucho, queda cubierta
        // por el conteo real de bytes que hace Verificar en los demás casos.
        var bomba = CrearZipDeCeros(("xl/worksheets/sheet1.xml", 200L * 1024 * 1024));
        const int firmaDirectorioCentral = 0x02014B50;
        var falsificado = false;
        for (var i = 0; i < bomba.Length - 28; i++)
        {
            if (BitConverter.ToInt32(bomba, i) != firmaDirectorioCentral) continue;
            BitConverter.GetBytes(1000u).CopyTo(bomba, i + 24); // tamaño sin comprimir declarado
            falsificado = true;
            break;
        }

        falsificado.Should().BeTrue("el caso solo vale si la cabecera se ha falsificado de verdad");
        using var flujo = new MemoryStream(bomba);
        using (var paquete = new ZipArchive(flujo, ZipArchiveMode.Read, leaveOpen: true))
            paquete.Entries[0].Length.Should().Be(1000, "la cabecera declara poco");
        flujo.Position = 0;

        var antes = GC.GetAllocatedBytesForCurrentThread();
        InspectorXlsxSeguro.Verificar(flujo);

        (GC.GetAllocatedBytesForCurrentThread() - antes).Should().BeLessThan(16L * 1024 * 1024);
    }

    [Fact]
    public void Un_ratio_anomalo_se_rechaza_aunque_el_total_quede_bajo_el_tope()
    {
        // 50 MiB de ceros: por debajo del tope de 100 MiB, pero con un ratio
        // de ~1000:1 que ningún libro real tiene.
        using var flujo = new MemoryStream(CrearZipDeCeros(("xl/sharedStrings.xml", 50L * 1024 * 1024)));

        var verificar = () => InspectorXlsxSeguro.Verificar(flujo);

        verificar.Should().ThrowExactly<InvalidDataException>().WithMessage("*compresión*");
    }

    [Fact]
    public void Un_total_descomprimido_por_encima_del_tope_se_rechaza_aunque_el_ratio_sea_normal()
    {
        // Sin compresión el ratio es 1:1: aquí solo puede frenarlo el tope
        // absoluto de bytes descomprimidos (3 x 40 MiB = 120 MiB). Fichero
        // temporal para no tener 120 MiB en memoria dentro del propio test.
        var ruta = Path.Combine(Path.GetTempPath(), $"xlsx-tope-{Guid.NewGuid():N}.zip");
        try
        {
            using (var escritura = new FileStream(ruta, FileMode.Create, FileAccess.Write))
            using (var paquete = new ZipArchive(escritura, ZipArchiveMode.Create))
            {
                for (var i = 0; i < 3; i++)
                    EscribirCeros(paquete, $"xl/worksheets/sheet{i}.xml", 40L * 1024 * 1024, CompressionLevel.NoCompression);
            }

            using var flujo = new FileStream(ruta, FileMode.Open, FileAccess.Read);

            var verificar = () => InspectorXlsxSeguro.Verificar(flujo);

            verificar.Should().ThrowExactly<InvalidDataException>().WithMessage("*tamaño descomprimido*");
        }
        finally
        {
            File.Delete(ruta);
        }
    }

    [Fact]
    public void Demasiadas_entradas_se_rechazan()
    {
        var entradas = Enumerable.Range(0, InspectorXlsxSeguro.MaximoEntradas + 1)
            .Select(i => ($"xl/parte{i}.xml", 10L)).ToArray();
        using var flujo = new MemoryStream(CrearZipDeCeros(entradas));

        var verificar = () => InspectorXlsxSeguro.Verificar(flujo);

        verificar.Should().ThrowExactly<InvalidDataException>().WithMessage("*demasiadas partes*");
    }

    [Fact]
    public void El_numero_maximo_de_entradas_exacto_se_admite()
    {
        var entradas = Enumerable.Range(0, InspectorXlsxSeguro.MaximoEntradas)
            .Select(i => ($"xl/parte{i}.xml", 10L)).ToArray();
        using var flujo = new MemoryStream(CrearZipDeCeros(entradas));

        var verificar = () => InspectorXlsxSeguro.Verificar(flujo);

        verificar.Should().NotThrow();
    }

    [Fact]
    public void Un_archivo_que_no_es_un_zip_se_rechaza_como_dato_invalido()
    {
        using var flujo = new MemoryStream("esto no es un xlsx"u8.ToArray());

        var verificar = () => InspectorXlsxSeguro.Verificar(flujo);

        verificar.Should().Throw<InvalidDataException>();
    }
}
