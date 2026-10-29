using System.IO.Compression;
using CaeManager.Web.Documentos;
using FluentAssertions;
using Xunit;

namespace CaeManager.Web.Tests;

/// <summary>
/// El tope de 10 MB de la subida acota el .zip comprimido, no lo que sale de
/// él. Medido contra el código anterior: un .zip de 99,7 KB producía 100 MB
/// —factor 1027— sin que nada lo detuviera, así que un archivo dentro del
/// límite de subida podía expandirse a más de 10 GB dentro del proceso web y
/// tumbar todos los circuitos Blazor de la réplica, cruzando tenants.
///
/// Estos casos fijan que los límites se aplican sobre los bytes realmente
/// descomprimidos y mientras se descomprimen, no sobre lo que declara la
/// cabecera del .zip (que la elige quien fabrica el archivo). El presupuesto
/// es la reserva del <see cref="AlmacenTemporalSubidaMasiva"/> de la pantalla,
/// y las entradas acaban en sus temporales, no en memoria (S-C-3).
/// </summary>
public sealed class ExtractorZipTests : IDisposable
{
    private const int MaximoEntradas = 60;
    private const long MaximoPorEntrada = 10 * 1024 * 1024;
    private const long PresupuestoHolgado = 600L * 1024 * 1024;

    private readonly TemporalesDePrueba _temporales = new();

    public void Dispose() => _temporales.Dispose();

    private static byte[] CrearZip(params (string Nombre, int Bytes)[] entradas)
    {
        using var memoria = new MemoryStream();
        using (var archivo = new ZipArchive(memoria, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (nombre, bytes) in entradas)
            {
                var entrada = archivo.CreateEntry(nombre, CompressionLevel.Optimal);
                using var flujo = entrada.Open();
                var bloque = new byte[64 * 1024];
                for (var escritos = 0; escritos < bytes; escritos += bloque.Length)
                    flujo.Write(bloque, 0, Math.Min(bloque.Length, bytes - escritos));
            }
        }

        return memoria.ToArray();
    }

    private (IReadOnlyList<(ArchivoTemporal Archivo, string NombreArchivo)> Extraido, AlmacenTemporalSubidaMasiva Almacen) ExtraerEn(
        byte[] zip, long presupuesto, int maximoEntradas = MaximoEntradas, long maximoPorEntrada = MaximoPorEntrada)
    {
        var almacen = _temporales.Cuota(porCircuito: presupuesto).CrearAlmacen(Guid.NewGuid());
        using var flujo = new MemoryStream(zip);
        return (ExtractorZip.Extraer(flujo, maximoEntradas, maximoPorEntrada, almacen), almacen);
    }

    private Action Extraer(byte[] zip, long presupuesto, int maximoEntradas = MaximoEntradas, long maximoPorEntrada = MaximoPorEntrada) =>
        () => ExtraerEn(zip, presupuesto, maximoEntradas, maximoPorEntrada);

    [Fact]
    public void Un_zip_que_cabe_en_la_subida_no_puede_expandirse_mas_alla_del_presupuesto()
    {
        // 20 entradas de 9 MB: 180 MB de ceros que el .zip deja en unos
        // cientos de KB, muy por debajo del tope de 10 MB de la subida. Es
        // exactamente el archivo que antes pasaba.
        //
        // Ninguna entrada llega al máximo por archivo (10 MB) a propósito: si
        // alguna lo superara, la pararía ese tope y este caso no diría nada
        // sobre el presupuesto.
        var bomba = CrearZip(Enumerable.Range(0, 20).Select(i => ($"bomba{i}.pdf", 9 * 1024 * 1024)).ToArray());
        bomba.Length.Should().BeLessThan(10 * 1024 * 1024,
            "el .zip de entrada tiene que pasar el tope de subida para que el caso sea el real");

        Extraer(bomba, presupuesto: 60L * 1024 * 1024).Should().ThrowExactly<LimiteDeZipSuperadoException>();
    }

    [Fact]
    public void El_presupuesto_es_acumulativo_entre_las_entradas_del_zip()
    {
        // Tres entradas de 5 MB: ninguna supera el máximo por archivo (10 MB)
        // ni el presupuesto por sí sola. Lo único que puede detenerlas es la
        // suma de lo que ya retiene la pantalla.
        var zip = CrearZip(
            ("uno.pdf", 5 * 1024 * 1024),
            ("dos.pdf", 5 * 1024 * 1024),
            ("tres.pdf", 5 * 1024 * 1024));

        Extraer(zip, presupuesto: 8 * 1024 * 1024).Should().ThrowExactly<LimiteDeZipSuperadoException>()
            .WithMessage("El contenido descomprimido del .zip no cabe en lo que queda de esta subida.");
    }

    [Fact]
    public void Un_zip_rechazado_a_medias_borra_lo_ya_extraido_y_devuelve_su_reserva()
    {
        var zip = CrearZip(("uno.pdf", 3000), ("dos.pdf", 3000), ("tres.pdf", 3000));
        var cuota = _temporales.Cuota(porCircuito: 7000);
        using var almacen = cuota.CrearAlmacen(Guid.NewGuid());
        using var flujo = new MemoryStream(zip);

        var extraer = () => ExtractorZip.Extraer(flujo, MaximoEntradas, MaximoPorEntrada, almacen);

        extraer.Should().ThrowExactly<LimiteDeZipSuperadoException>("control: el tercero no cabe");
        almacen.Reservado.Should().Be(0);
        cuota.ReservadoGlobal.Should().Be(0);
        Directory.GetFiles(almacen.Directorio).Should().BeEmpty("los dos que sí cupieron no se quedan huérfanos");
    }

    [Fact]
    public void Un_zip_con_mas_archivos_de_los_admitidos_se_rechaza()
    {
        var entradas = Enumerable.Range(0, 5).Select(i => ($"doc{i}.pdf", 1024)).ToArray();

        Extraer(CrearZip(entradas), PresupuestoHolgado, maximoEntradas: 4).Should().ThrowExactly<LimiteDeZipSuperadoException>();
    }

    [Fact]
    public void Un_archivo_mas_grande_que_el_maximo_por_archivo_se_rechaza()
    {
        var zip = CrearZip(("gordo.pdf", 3 * 1024 * 1024));

        Extraer(zip, PresupuestoHolgado, maximoPorEntrada: 1024 * 1024).Should().ThrowExactly<LimiteDeZipSuperadoException>();
    }

    /// <summary>
    /// SubidaMasiva enseña al usuario el mensaje de un límite superado, y solo ese: un
    /// .zip dañado lo rechaza ZipArchive con InvalidDataException y texto técnico del
    /// framework, que no puede confundirse con un límite (P1-E1b).
    /// </summary>
    [Fact]
    public void Un_zip_danado_no_se_confunde_con_un_limite_superado()
    {
        var danado = new byte[] { 0x50, 0x4B, 0x03, 0x04, 0x00, 0x01, 0x02, 0x03, 0x04, 0x05 };

        Extraer(danado, PresupuestoHolgado).Should().Throw<InvalidDataException>(
            "no es un límite superado: su mensaje es del framework y no se enseña, va al aviso genérico de .zip ilegible");
    }

    [Fact]
    public async Task Un_zip_normal_se_extrae_a_temporales_con_su_contenido_y_sus_nombres()
    {
        var zip = CrearZip(("informe.pdf", 2048), ("carpeta/foto.png", 1024));

        var (extraido, almacen) = ExtraerEn(zip, PresupuestoHolgado);

        extraido.Should().HaveCount(2);
        extraido[0].NombreArchivo.Should().Be("informe.pdf");
        (await almacen.LeerAsync(extraido[0].Archivo)).Should().HaveCount(2048);
        // La ruta interna del .zip se descarta: solo se conserva el nombre.
        extraido[1].NombreArchivo.Should().Be("foto.png");
        (await almacen.LeerAsync(extraido[1].Archivo)).Should().HaveCount(1024);
        almacen.Reservado.Should().Be(3072, "lo extraído descuenta del tope de la pantalla");
        Directory.GetFiles(almacen.Directorio).Should().HaveCount(2);
    }

    /// <summary>
    /// Falsifica el tamaño descomprimido que declara un .zip, en la cabecera
    /// local y en el directorio central, para que anuncie mucho menos de lo
    /// que su flujo deflate produce en realidad. Es lo que hace una bomba de
    /// verdad, y es la razón de que los límites no puedan apoyarse en
    /// <c>ZipArchiveEntry.Length</c>.
    /// </summary>
    private static byte[] FalsificarTamanoDeclarado(byte[] zip, uint tamanoMentira)
    {
        var copia = (byte[])zip.Clone();
        // Cabecera local (PK): tamaño sin comprimir en el byte 22.
        // Directorio central (PK): el mismo campo en el byte 24.
        foreach (var (firma, desplazamiento) in new (byte[] Firma, int Desplazamiento)[]
                 {
                     ([0x50, 0x4B, 0x03, 0x04], 22),
                     ([0x50, 0x4B, 0x01, 0x02], 24),
                 })
        {
            for (var i = 0; i + firma.Length <= copia.Length; i++)
            {
                if (!copia.AsSpan(i, firma.Length).SequenceEqual(firma)) continue;
                BitConverter.GetBytes(tamanoMentira).CopyTo(copia.AsSpan(i + desplazamiento));
            }
        }

        return copia;
    }

    [Fact]
    public void Un_zip_que_miente_sobre_su_tamano_no_produce_mas_bytes_de_los_declarados()
    {
        // Caso de caracterización, no de defensa propia: fija una garantía del
        // framework de la que depende el diseño de ExtractorZip.
        //
        // La intuición dice que una bomba puede declarar 1 KB y verter
        // gigabytes, y que por tanto el tamaño de la cabecera no vale nada.
        // Medido, es falso para System.IO.Compression: ZipArchiveEntry.Open()
        // acota la lectura al tamaño sin comprimir declarado, así que un .zip
        // con 40 MB reales declarados como 1 KB entrega 1024 bytes y se acabó.
        //
        // De ahí que el rechazo por cabecera sea una cota fiable y no una mera
        // optimización. Si una versión futura de .NET dejara de truncar, este
        // caso se pondría rojo y avisaría de que el rechazo temprano dejó de
        // ser suficiente por sí solo — que es justo para lo que está aquí.
        const int bytesReales = 40 * 1024 * 1024;
        var mentiroso = FalsificarTamanoDeclarado(
            CrearZip(("mentiroso.pdf", bytesReales)), tamanoMentira: 1024);

        var (extraido, _) = ExtraerEn(mentiroso, PresupuestoHolgado, maximoPorEntrada: 2 * 1024 * 1024);

        extraido[0].Archivo.TamanoBytes.Should().Be(1024);
        extraido[0].Archivo.TamanoBytes.Should().BeLessThan(bytesReales);
    }

    [Fact]
    public void Un_lote_que_agota_su_presupuesto_justo_se_acepta()
    {
        // Frontera: gastar exactamente el presupuesto no es pasarse. Sin este
        // caso, un off-by-one rechazaría subidas legítimas y nadie lo notaría.
        var zip = CrearZip(("justo.pdf", 4096));

        var (extraido, almacen) = ExtraerEn(zip, presupuesto: 4096);

        extraido.Should().HaveCount(1);
        extraido[0].Archivo.TamanoBytes.Should().Be(4096);
        almacen.Reservado.Should().Be(4096);
    }
}
