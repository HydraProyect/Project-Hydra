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

    private DivisorPdfEnProcesoAparte Divisor(OpcionesProcesoHerramientasPdf opciones) =>
        new(opciones, new LimitadorProcesosAislados(1), new Grabador(Lanzador(), salida => _ultima = salida),
            NullLogger<DivisorPdfEnProcesoAparte>.Instance);

    private LanzadorProcesoAislado Lanzador() =>
        new(NullLogger<LanzadorProcesoAislado>.Instance) { AlArrancar = id => _arrancados.Add(id) };

    private static OpcionesProcesoHerramientasPdf Opciones(long memoria = 512L * 1024 * 1024, double segundos = 60) => new()
    {
        MemoriaMaximaBytes = memoria,
        TiempoMaximoInspeccionar = TimeSpan.FromSeconds(segundos),
        TiempoMaximoUnir = TimeSpan.FromSeconds(segundos),
        TiempoMaximoAbrirParaDividir = TimeSpan.FromSeconds(segundos),
        TiempoMaximoExtraerTramo = TimeSpan.FromSeconds(segundos),
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
    public async Task Divide_un_PDF_real_en_el_proceso_aparte_y_el_proceso_ya_no_existe()
    {
        var divisor = Divisor(Opciones());

        using var abierto = (await divisor.AbrirAsync(UnionSobrePdfRealTests.Pdf("Uno", "Dos", "Tres"), CancellationToken.None)).Valor;
        var tramo = await abierto.ExtraerPaginasAsync(2, 3, LimitesDivisionPdf.TamanoMaximoTramo(1_000_000), CancellationToken.None);
        var fuera = await abierto.ExtraerPaginasAsync(3, 4, long.MaxValue, CancellationToken.None);

        abierto.NumeroPaginas.Should().Be(3);
        tramo.EsExitoso.Should().BeTrue(tramo.EsExitoso ? "" : tramo.Error.Codigo);
        using (var leido = PdfSharp.Pdf.IO.PdfReader.Open(new MemoryStream(tramo.Valor), PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import))
            leido.PageCount.Should().Be(2);
        fuera.Error.Codigo.Should().Be(ErroresDivisorPdf.RangoInvalido, "un rango fuera del PDF no llega a lanzar el proceso");
        _arrancados.Should().HaveCount(2).And.OnlyContain(id => YaNoExiste(id));
    }

    [Fact]
    public async Task Un_PDF_que_agota_la_memoria_del_proceso_aparte_al_dividir_sale_como_demasiado_complejo_y_la_aplicacion_sigue()
    {
        // La garantía de Dividir: el acotador deja pasar esta bomba, y PDFsharp la abre en el proceso aparte, no aquí.
        var bomba = PdfQueAgotaLaMemoria();
        AcotadorEstructuraPdf.Medir(bomba).Should().NotBeNull("el acotador no mide arrays: esta bomba lo pasa y solo la para el límite duro");
        var divisor = Divisor(Opciones(memoria: 64L * 1024 * 1024));

        var resultado = await divisor.AbrirAsync(bomba, CancellationToken.None);

        resultado.Error.Codigo.Should().Be(ErroresDivisorPdf.DemasiadoComplejo);
        _ultima!.Desenlace.Should().Be(DesenlaceProcesoAislado.Terminado);
        _ultima.CodigoSalida.Should().Be(ProcesoHerramientasPdf.CodigoSinMemoria, "el proceso aparte se quedó sin memoria en su límite");
        YaNoExiste(_ultima.IdProceso).Should().BeTrue();

        // El proceso web sigue: el siguiente PDF se abre y se divide con normalidad en otro proceso aparte.
        using var normal = (await divisor.AbrirAsync(UnionSobrePdfRealTests.Pdf("Normal", "Otra"), CancellationToken.None)).Valor;
        (await normal.ExtraerPaginasAsync(1, 1, LimitesDivisionPdf.TamanoMaximoTramo(1_000_000), CancellationToken.None)).EsExitoso.Should().BeTrue();
    }

    [Fact]
    public async Task Un_PDF_que_pasa_del_tiempo_al_dividir_se_mata_y_sale_como_demasiado_complejo()
    {
        var divisor = Divisor(Opciones(segundos: 0.2));

        var resultado = await divisor.AbrirAsync(PdfQueAgotaLaMemoria(), CancellationToken.None);

        resultado.Error.Codigo.Should().Be(ErroresDivisorPdf.DemasiadoComplejo);
        _ultima!.Desenlace.Should().Be(DesenlaceProcesoAislado.TiempoAgotado);
        YaNoExiste(_ultima.IdProceso).Should().BeTrue("se mató al agotar el tiempo");
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
    public async Task Si_no_se_puede_matar_al_hijo_el_plazo_no_da_un_desenlace_sino_la_excepcion_con_su_PID()
    {
        // El sistema no deja matarlo: Kill lanza siempre. El hijo real sigue vivo hasta que el test lo mata al final.
        var lanzador = LanzadorQueNoPuedeMatar(_ => throw new System.ComponentModel.Win32Exception("sin permiso"));
        try
        {
            var ejecutar = () => lanzador.EjecutarAsync(ProcesoQueEspera(), (_, _) => Task.CompletedTask,
                TimeSpan.FromMilliseconds(300), 1_000_000, CancellationToken.None);

            (await ejecutar.Should().ThrowAsync<ProcesoAisladoSinTerminarException>("un tiempo agotado soltaría el turno"))
                .Which.IdProceso.Should().Be(_arrancados.Single());
            YaNoExiste(_arrancados[0]).Should().BeFalse("el hijo sigue vivo: por eso no se puede soltar su turno");
        }
        finally
        {
            MatarDeVerdad();
        }
    }

    [Fact]
    public async Task Si_el_hijo_no_sale_tras_matarlo_y_el_llamante_cancela_gana_la_excepcion_y_no_la_cancelacion()
    {
        // Kill no lanza pero no surte efecto: el hijo no sale en el margen.
        var lanzador = LanzadorQueNoPuedeMatar(_ => { });
        using var cancelacion = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        try
        {
            var ejecutar = () => lanzador.EjecutarAsync(ProcesoQueEspera(), (_, _) => Task.CompletedTask,
                TimeSpan.FromMinutes(5), 1_000_000, cancelacion.Token);

            await ejecutar.Should().ThrowAsync<ProcesoAisladoSinTerminarException>("la cancelación soltaría el turno");
        }
        finally
        {
            MatarDeVerdad();
        }
    }

    [Fact]
    public async Task Si_el_hijo_muere_pero_una_escritura_sigue_pendiente_no_consta_terminado_porque_retiene_la_entrada()
    {
        // Una escritura que no atiende la cancelación ni ve la tubería rota: sigue referenciando la entrada tras matar al hijo.
        var pendiente = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lanzador = new LanzadorProcesoAislado(NullLogger<LanzadorProcesoAislado>.Instance)
        {
            AlArrancar = id => _arrancados.Add(id),
            EsperaDeMuerte = TimeSpan.FromMilliseconds(300),
        };
        var seguimiento = new SeguimientoProcesoAislado();
        try
        {
            var ejecutar = () => lanzador.EjecutarAsync(ProcesoQueEspera(), (_, _) => pendiente.Task,
                TimeSpan.FromMilliseconds(300), 1_000_000, CancellationToken.None, seguimiento);

            await ejecutar.Should().ThrowAsync<ProcesoAisladoSinTerminarException>("la escritura abandonada sigue reteniendo los PDF");
            YaNoExiste(_arrancados.Single()).Should().BeTrue("el hijo sí murió: lo que no consta es que nada retenga la entrada");
            seguimiento.ConstaTerminado.Should().BeFalse();
            seguimiento.IdProceso.Should().Be(_arrancados.Single(), "el PID se publica al arrancar");
        }
        finally
        {
            pendiente.TrySetResult();
            MatarDeVerdad();
        }
    }

    [Fact]
    public async Task Con_el_hijo_terminado_y_sus_tuberias_cerradas_el_seguimiento_lo_confirma()
    {
        var seguimiento = new SeguimientoProcesoAislado();

        var salida = await Lanzador().EjecutarAsync(ProcesoQueEspera(), (_, _) => Task.CompletedTask,
            TimeSpan.FromMilliseconds(300), 1_000_000, CancellationToken.None, seguimiento);

        salida.Desenlace.Should().Be(DesenlaceProcesoAislado.TiempoAgotado);
        seguimiento.ConstaTerminado.Should().BeTrue();
        seguimiento.IdProceso.Should().Be(salida.IdProceso);
    }

    [Fact]
    public async Task Si_el_ejecutable_no_existe_no_hay_hijo_y_el_seguimiento_lo_confirma()
    {
        var seguimiento = new SeguimientoProcesoAislado();

        var ejecutar = () => Lanzador().EjecutarAsync(new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "no-existe-" + Guid.NewGuid().ToString("N"))),
            (_, _) => Task.CompletedTask, TimeSpan.FromSeconds(5), 1_000_000, CancellationToken.None, seguimiento);

        await ejecutar.Should().ThrowAsync<System.ComponentModel.Win32Exception>();
        seguimiento.ConstaTerminado.Should().BeTrue("sin hijo no queda nada vivo que retenga el turno");
        seguimiento.IdProceso.Should().BeNull();
    }

    private LanzadorProcesoAislado LanzadorQueNoPuedeMatar(Action<Process> matar) =>
        new(NullLogger<LanzadorProcesoAislado>.Instance)
        {
            AlArrancar = id => _arrancados.Add(id),
            Matar = matar,
            EsperaDeMuerte = TimeSpan.FromMilliseconds(300),
        };

    private void MatarDeVerdad()
    {
        foreach (var id in _arrancados)
        {
            try
            {
                using var proceso = Process.GetProcessById(id);
                proceso.Kill(entireProcessTree: true);
                proceso.WaitForExit(TimeSpan.FromSeconds(10));
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                // Ya no existe.
            }
        }
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
    /// Si el contenedor llega a su techo de memoria, el OOM killer del kernel debe elegir al proceso aparte y no al proceso
    /// web: el hijo sube su propio <c>oom_score_adj</c> a 1000 al arrancar. Se lee de <c>/proc</c> mientras el hijo espera la
    /// petición, antes de escribírsela. procfs y el OOM killer solo existen en Linux, así que en Windows no hay nada que
    /// comprobar; la suite corre en Linux en CI, que es donde discrimina.
    /// </summary>
    [Fact]
    public async Task En_Linux_el_proceso_aparte_se_ofrece_como_primera_victima_del_OOM_killer()
    {
        if (!OperatingSystem.IsLinux())
            return;

        string? valor = null;
        var unidor = new UnidorPdfEnProcesoAparte(Opciones(), new LimitadorProcesosAislados(1),
            new AntesDeEscribir(Lanzador(), async () => valor = await LeerOomScoreAdjAsync(_arrancados[^1])),
            NullLogger<UnidorPdfEnProcesoAparte>.Instance);

        var info = await unidor.InspeccionarAsync(UnionSobrePdfRealTests.Pdf("Uno"), CancellationToken.None);

        info.EsExitoso.Should().BeTrue(info.EsExitoso ? "" : info.Error.Codigo);
        valor.Should().Be("1000");
    }

    /// <summary>Espera a que el hijo haya escrito su <c>oom_score_adj</c> (arranca el anfitrión dotnet) y lo devuelve.</summary>
    private static async Task<string> LeerOomScoreAdjAsync(int id)
    {
        var ruta = $"/proc/{id}/oom_score_adj";
        var limite = Stopwatch.StartNew();
        string valor;
        do
        {
            valor = (await File.ReadAllTextAsync(ruta)).Trim();
            if (valor == "1000")
                break;
            await Task.Delay(50);
        }
        while (limite.Elapsed < TimeSpan.FromSeconds(20));

        return valor;
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
            TimeSpan tiempoMaximo, int maximoBytesSalida, CancellationToken cancellationToken, SeguimientoProcesoAislado? seguimiento = null)
        {
            var salida = await real.EjecutarAsync(inicio, escribirEntrada, tiempoMaximo, maximoBytesSalida, cancellationToken, seguimiento);
            anotar(salida);
            return salida;
        }
    }

    /// <summary>El lanzador real, con una espera antes de escribir la petición al hijo ya arrancado.</summary>
    private sealed class AntesDeEscribir(ILanzadorProcesoAislado real, Func<Task> antes) : ILanzadorProcesoAislado
    {
        public Task<SalidaProcesoAislado> EjecutarAsync(ProcessStartInfo inicio, Func<Stream, CancellationToken, Task> escribirEntrada,
            TimeSpan tiempoMaximo, int maximoBytesSalida, CancellationToken cancellationToken, SeguimientoProcesoAislado? seguimiento = null) =>
            real.EjecutarAsync(inicio, async (entrada, ct) =>
            {
                await antes();
                await escribirEntrada(entrada, ct);
            }, tiempoMaximo, maximoBytesSalida, cancellationToken, seguimiento);
    }
}
