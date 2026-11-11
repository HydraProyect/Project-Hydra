using System.Diagnostics;
using System.Text;
using CaeManager.Application.HerramientasPdf;
using CaeManager.Infrastructure.HerramientasPdf;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CaeManager.Web.Tests.HerramientasPdf;

/// <summary>
/// El proceso aparte de verdad: <c>dotnet CaeManager.Web.dll --proceso-herramientas-pdf</c> desde la salida de este proyecto
/// de tests, con un PDF que agota su memoria o su tiempo. El proceso de los tests hace de proceso web: si el aislamiento no
/// funcionara, el PDF agotaría su memoria y no podría seguir con la comprobación siguiente. Corre igual en Windows y en Linux
/// (CI); el tiempo y la muerte del árbol se prueban además con un proceso que solo espera.
/// </summary>
public sealed class ProcesoAisladoRealTests
{
    private readonly List<int> _arrancados = [];
    private SalidaProcesoAislado? _ultima;

    private UnidorPdfEnProcesoAparte Unidor(OpcionesProcesoHerramientasPdf opciones) =>
        new(opciones, new LimitadorProcesosAislados(1), new Grabador(Lanzador(), salida => _ultima = salida),
            NullLogger<UnidorPdfEnProcesoAparte>.Instance);

    private LanzadorProcesoAislado Lanzador() =>
        new(NullLogger<LanzadorProcesoAislado>.Instance) { AlArrancar = id => _arrancados.Add(id) };

    private static OpcionesProcesoHerramientasPdf Opciones(long memoria = 512L * 1024 * 1024, double segundos = 60) => new()
    {
        MemoriaMaximaBytes = memoria,
        TiempoMaximoInspeccionar = TimeSpan.FromSeconds(segundos),
        TiempoMaximoUnir = TimeSpan.FromSeconds(segundos),
        RutaEnsamblado = Path.Combine(AppContext.BaseDirectory, "CaeManager.Web.dll"),
    };

    /// <summary>
    /// Una página y un único objeto con un array de millones de enteros: unos pocos MB en el archivo, cientos de MB de
    /// objetos al abrirlo con PDFsharp. Un solo objeto y sin flujos comprimidos, así que el acotador no lo ve.
    /// </summary>
    private static byte[] PdfQueAgotaLaMemoria(int enteros = 5_000_000)
    {
        var array = new StringBuilder(enteros * 2 + 2).Append('[');
        for (var i = 0; i < enteros; i++)
            array.Append("0 ");
        array.Append(']');

        return UnionSobrePdfRealTests.PdfACano(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Grande 4 0 R >>",
            array.ToString());
    }

    [Fact]
    public async Task Une_dos_PDF_reales_en_el_proceso_aparte_y_el_proceso_ya_no_existe()
    {
        var unidor = Unidor(Opciones());

        var info = await unidor.InspeccionarAsync(UnionSobrePdfRealTests.Pdf("Uno", "Dos"), CancellationToken.None);
        var unido = await unidor.UnirAsync([UnionSobrePdfRealTests.Pdf("Uno"), UnionSobrePdfRealTests.Pdf("Dos", "Tres")], CancellationToken.None);

        info.Valor.NumeroPaginas.Should().Be(2, info.EsExitoso ? "" : info.Error.Codigo);
        unido.Valor.NumeroPaginas.Should().Be(3, unido.EsExitoso ? "" : unido.Error.Codigo);
        _arrancados.Should().HaveCount(2).And.OnlyContain(id => YaNoExiste(id));
    }

    [Fact]
    public async Task Un_PDF_que_agota_la_memoria_del_proceso_aparte_sale_como_demasiado_complejo_y_la_aplicacion_sigue()
    {
        var bomba = PdfQueAgotaLaMemoria();
        AcotadorEstructuraPdf.Medir(bomba).Should().NotBeNull("el acotador no mide arrays: esta bomba lo pasa y solo la para el límite duro");
        var unidor = Unidor(Opciones(memoria: 64L * 1024 * 1024));

        var resultado = await unidor.InspeccionarAsync(bomba, CancellationToken.None);

        resultado.Error.Codigo.Should().Be(ErroresUnirPdf.DemasiadoComplejo);
        _ultima!.Desenlace.Should().Be(DesenlaceProcesoAislado.Terminado);
        _ultima.CodigoSalida.Should().Be(ProcesoHerramientasPdf.CodigoSinMemoria, "el proceso aparte se quedó sin memoria en su límite");
        YaNoExiste(_ultima.IdProceso).Should().BeTrue();

        // El proceso web sigue: el siguiente PDF se inspecciona con normalidad en otro proceso aparte.
        (await unidor.InspeccionarAsync(UnionSobrePdfRealTests.Pdf("Normal"), CancellationToken.None)).Valor.NumeroPaginas.Should().Be(1);
    }

    [Fact]
    public async Task Un_PDF_que_pasa_del_tiempo_se_mata_y_sale_como_demasiado_complejo()
    {
        var unidor = Unidor(Opciones(segundos: 0.2));
        var reloj = Stopwatch.StartNew();

        var resultado = await unidor.InspeccionarAsync(PdfQueAgotaLaMemoria(), CancellationToken.None);

        resultado.Error.Codigo.Should().Be(ErroresUnirPdf.DemasiadoComplejo);
        _ultima!.Desenlace.Should().Be(DesenlaceProcesoAislado.TiempoAgotado);
        YaNoExiste(_ultima.IdProceso).Should().BeTrue("se mató al agotar el tiempo");
        reloj.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(8));
    }

    [Fact]
    public async Task Al_agotar_el_tiempo_el_lanzador_mata_el_proceso_sin_esperar_a_que_acabe()
    {
        var reloj = Stopwatch.StartNew();

        var salida = await Lanzador().EjecutarAsync(ProcesoQueEspera(), (_, _) => Task.CompletedTask,
            TimeSpan.FromMilliseconds(300), 1_000_000, CancellationToken.None);

        salida.Desenlace.Should().Be(DesenlaceProcesoAislado.TiempoAgotado);
        reloj.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(8), "el proceso espera 60 s: solo vuelve antes si se mata");
        YaNoExiste(salida.IdProceso).Should().BeTrue();
    }

    [Fact]
    public async Task Si_el_llamante_cancela_el_lanzador_mata_el_proceso_y_propaga_la_cancelacion()
    {
        using var cancelacion = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var reloj = Stopwatch.StartNew();

        var ejecutar = () => Lanzador().EjecutarAsync(ProcesoQueEspera(), (_, _) => Task.CompletedTask,
            TimeSpan.FromMinutes(5), 1_000_000, cancelacion.Token);

        await ejecutar.Should().ThrowAsync<OperationCanceledException>();
        reloj.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(8));
        _arrancados.Should().ContainSingle();
        YaNoExiste(_arrancados[0]).Should().BeTrue();
    }

    [Fact]
    public async Task Un_proceso_que_escribe_de_mas_se_mata_sin_leer_el_resto()
    {
        var reloj = Stopwatch.StartNew();

        var salida = await Lanzador().EjecutarAsync(ProcesoQueEscribeSinParar(), (_, _) => Task.CompletedTask,
            TimeSpan.FromMinutes(5), 4096, CancellationToken.None);

        salida.Desenlace.Should().Be(DesenlaceProcesoAislado.SalidaDemasiadoGrande);
        reloj.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(8), "escribe sin parar: solo vuelve antes si se mata");
        YaNoExiste(salida.IdProceso).Should().BeTrue();
    }

    [Fact]
    public async Task Quinientas_paginas_legitimas_se_unen_dentro_del_limite_por_defecto()
    {
        var unidor = Unidor(new OpcionesProcesoHerramientasPdf { RutaEnsamblado = Path.Combine(AppContext.BaseDirectory, "CaeManager.Web.dll") });
        var doscientas = UnionSobrePdfRealTests.Pdf(Enumerable.Range(1, 200).Select(i => $"Pagina {i}").ToArray());
        var trescientas = UnionSobrePdfRealTests.Pdf(Enumerable.Range(1, 300).Select(i => $"Hoja {i}").ToArray());

        var unido = await unidor.UnirAsync([doscientas, trescientas], CancellationToken.None);

        unido.Valor.NumeroPaginas.Should().Be(LimitesUnionPdf.MaximoPaginasTotales, unido.EsExitoso ? "" : unido.Error.Codigo);
    }

    /// <summary>
    /// Un proceso que no escribe nada y tarda 60 s en acabar porque espera a un nieto, en Windows y en Linux: comprueba que
    /// el plazo no depende de que el hijo escriba y que se mata el árbol (el nieto hereda la salida estándar; si siguiera
    /// vivo, la lectura no terminaría).
    /// </summary>
    private static ProcessStartInfo ProcesoQueEspera() => OperatingSystem.IsWindows()
        ? new ProcessStartInfo("cmd") { ArgumentList = { "/c", "ping -n 60 127.0.0.1 >NUL" } }
        : new ProcessStartInfo("sh") { ArgumentList = { "-c", "sleep 60; true" } };

    private static ProcessStartInfo ProcesoQueEscribeSinParar() => OperatingSystem.IsWindows()
        ? new ProcessStartInfo("cmd") { ArgumentList = { "/c", "for /l %i in (0,0,1) do @echo xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx" } }
        : new ProcessStartInfo("yes");

    private static bool YaNoExiste(int id)
    {
        try
        {
            using var proceso = Process.GetProcessById(id);
            return proceso.HasExited;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    /// <summary>El lanzador real, anotando la última salida para comprobar el desenlace.</summary>
    private sealed class Grabador(ILanzadorProcesoAislado real, Action<SalidaProcesoAislado> anotar) : ILanzadorProcesoAislado
    {
        public async Task<SalidaProcesoAislado> EjecutarAsync(ProcessStartInfo inicio, Func<Stream, CancellationToken, Task> escribirEntrada,
            TimeSpan tiempoMaximo, int maximoBytesSalida, CancellationToken cancellationToken)
        {
            var salida = await real.EjecutarAsync(inicio, escribirEntrada, tiempoMaximo, maximoBytesSalida, cancellationToken);
            anotar(salida);
            return salida;
        }
    }
}
