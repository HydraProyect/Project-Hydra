using System.Diagnostics;
using CaeManager.Application.Common;
using CaeManager.Application.HerramientasPdf;
using CaeManager.Domain.Common;
using CaeManager.Infrastructure.HerramientasPdf;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CaeManager.Web.Tests.HerramientasPdf;

/// <summary>
/// El cliente del proceso aparte con un doble del lanzador: qué lanza (entorno vacío y límite de memoria), qué le escribe,
/// cómo traduce cada desenlace (tiempo agotado, muerte, respuesta que no cuadra) a los códigos de <see cref="ErroresUnirPdf"/>
/// y cómo gestiona el turno. Que el proceso real se mata y que el límite de memoria corta lo prueba
/// <see cref="ProcesoAisladoRealTests"/>.
/// </summary>
public sealed class UnidorPdfEnProcesoAparteTests
{
    private static readonly byte[] PdfA = "%PDF-1.4 A"u8.ToArray();
    private static readonly byte[] PdfB = "%PDF-1.4 B"u8.ToArray();

    private readonly OpcionesProcesoHerramientasPdf _opciones = new()
    {
        MemoriaMaximaBytes = 300L * 1024 * 1024,
        TiempoMaximoInspeccionar = TimeSpan.FromSeconds(7),
        TiempoMaximoUnir = TimeSpan.FromSeconds(11),
        EsperaMaximaTurno = TimeSpan.FromMilliseconds(100),
        Anfitrion = "anfitrion-dotnet",
        RutaEnsamblado = "aplicacion.dll",
    };

    private readonly LimitadorProcesosAislados _limitador = new(1);

    private UnidorPdfEnProcesoAparte Unidor(LanzadorDoble lanzador) =>
        new(_opciones, _limitador, lanzador, NullLogger<UnidorPdfEnProcesoAparte>.Instance);

    private static SalidaProcesoAislado Terminado(int codigo, byte[] salida) =>
        new(DesenlaceProcesoAislado.Terminado, codigo, salida, 1234);

    private static byte[] Inspeccion(Result<InfoPdfParaUnir> resultado)
    {
        using var salida = new MemoryStream();
        ProtocoloProcesoHerramientasPdf.EscribirInspeccion(salida, resultado);
        return salida.ToArray();
    }

    private static byte[] Union(Result<PdfUnido> resultado)
    {
        using var salida = new MemoryStream();
        ProtocoloProcesoHerramientasPdf.EscribirUnion(salida, resultado);
        return salida.ToArray();
    }

    [Fact]
    public async Task Inspeccionar_manda_el_PDF_al_proceso_aparte_con_su_tiempo_y_devuelve_lo_que_responde()
    {
        var lanzador = new LanzadorDoble(_ => Terminado(0, Inspeccion(Result.Exito(new InfoPdfParaUnir(3, true, 4096)))));

        var resultado = await Unidor(lanzador).InspeccionarAsync(PdfA, CancellationToken.None);

        resultado.Valor.Should().Be(new InfoPdfParaUnir(3, true, 4096));
        var peticion = ProtocoloProcesoHerramientasPdf.LeerPeticion(new MemoryStream(lanzador.Entrada!))!;
        peticion.Operacion.Should().Be(ProtocoloProcesoHerramientasPdf.OperacionInspeccionar);
        peticion.Pdfs.Should().ContainSingle().Which.Should().Equal(PdfA);
        lanzador.Tiempo.Should().Be(_opciones.TiempoMaximoInspeccionar);
        lanzador.MaximoSalida.Should().Be(ProtocoloProcesoHerramientasPdf.MaximoBytesRespuestaInspeccion);
    }

    [Fact]
    public async Task Unir_manda_los_PDF_en_orden_y_devuelve_el_unido()
    {
        var lanzador = new LanzadorDoble(_ => Terminado(0, Union(Result.Exito(new PdfUnido("%PDF-1.4 unido"u8.ToArray(), 5)))));

        var resultado = await Unidor(lanzador).UnirAsync([PdfB, PdfA], CancellationToken.None);

        resultado.Valor.NumeroPaginas.Should().Be(5);
        resultado.Valor.Contenido.Should().Equal("%PDF-1.4 unido"u8.ToArray());
        var peticion = ProtocoloProcesoHerramientasPdf.LeerPeticion(new MemoryStream(lanzador.Entrada!))!;
        peticion.Operacion.Should().Be(ProtocoloProcesoHerramientasPdf.OperacionUnir);
        peticion.Pdfs.Should().HaveCount(2);
        peticion.Pdfs[0].Should().Equal(PdfB);
        peticion.Pdfs[1].Should().Equal(PdfA);
        lanzador.Tiempo.Should().Be(_opciones.TiempoMaximoUnir);
        lanzador.MaximoSalida.Should().Be(ProtocoloProcesoHerramientasPdf.MaximoBytesRespuestaUnion);
    }

    [Fact]
    public void Lanza_la_aplicacion_en_modo_proceso_aparte_con_el_limite_de_memoria_y_sin_heredar_el_entorno()
    {
        var inicio = UnidorPdfEnProcesoAparte.Inicio(_opciones);

        inicio.FileName.Should().Be("anfitrion-dotnet");
        inicio.ArgumentList.Should().Equal("aplicacion.dll", ProcesoHerramientasPdf.Argumento);
        inicio.Environment["DOTNET_GCHeapHardLimit"].Should().Be("0x12C00000", "300 MB en hexadecimal, como lo lee el runtime");
        inicio.Environment["DOTNET_gcServer"].Should().Be("0");
        inicio.Environment.Keys.Should().BeSubsetOf(
            ["PATH", "LANG", "LC_ALL", "SystemRoot", "windir", "DOTNET_GCHeapHardLimit", "DOTNET_gcServer", "DOTNET_gcConcurrent"],
            "el proceso aparte no recibe cadenas de conexión ni claves del entorno de la aplicación");
    }

    [Fact]
    public void El_anfitrion_por_defecto_es_el_dotnet_del_runtime_que_corre()
    {
        File.Exists(OpcionesProcesoHerramientasPdf.AnfitrionDotnet()).Should().BeTrue();
    }

    private static readonly Dictionary<string, (SalidaProcesoAislado Salida, string Codigo)> CasosDeDesenlace = new()
    {
        ["tiempo agotado"] = (new(DesenlaceProcesoAislado.TiempoAgotado, null, ReadOnlyMemory<byte>.Empty, 1), ErroresUnirPdf.DemasiadoComplejo),
        ["salida de más"] = (new(DesenlaceProcesoAislado.SalidaDemasiadoGrande, null, ReadOnlyMemory<byte>.Empty, 1), ErroresUnirPdf.DemasiadoComplejo),
        ["sin memoria"] = (Terminado(ProcesoHerramientasPdf.CodigoSinMemoria, []), ErroresUnirPdf.DemasiadoComplejo),
        ["muerto por señal"] = (Terminado(137, []), ErroresUnirPdf.DemasiadoComplejo),
        ["petición inválida"] = (Terminado(ProcesoHerramientasPdf.CodigoPeticionInvalida, []), ErroresUnirPdf.Ilegible),
        ["respuesta vacía"] = (Terminado(0, []), ErroresUnirPdf.Ilegible),
        ["respuesta basura"] = (Terminado(0, "no es una respuesta"u8.ToArray()), ErroresUnirPdf.Ilegible),
        ["código fuera de la lista"] = (Terminado(0, [1, 99]), ErroresUnirPdf.Ilegible),
        ["páginas fuera de tope"] = (Terminado(0, Inspeccion(Result.Exito(new InfoPdfParaUnir(LimitesUnionPdf.MaximoPaginasTotales + 1, false)))), ErroresUnirPdf.Ilegible),
        ["estructura fuera de tope"] = (Terminado(0, Inspeccion(Result.Exito(new InfoPdfParaUnir(1, false, LimitesUnionPdf.MaximoBytesEstructuraPorPdf + 1)))), ErroresUnirPdf.Ilegible),
        ["fallo del motor"] = (Terminado(0, Inspeccion(Result.Fallo<InfoPdfParaUnir>(Error.Crear(ErroresUnirPdf.ConContrasena, "x")))), ErroresUnirPdf.ConContrasena),
    };

    public static TheoryData<string> Desenlaces()
    {
        var casos = new TheoryData<string>();
        foreach (var caso in CasosDeDesenlace.Keys)
            casos.Add(caso);
        return casos;
    }

    [Theory]
    [MemberData(nameof(Desenlaces))]
    public async Task Cada_desenlace_del_proceso_aparte_sale_como_un_codigo_de_entrada(string caso)
    {
        var (salida, codigo) = CasosDeDesenlace[caso];
        var resultado = await Unidor(new LanzadorDoble(_ => salida)).InspeccionarAsync(PdfA, CancellationToken.None);

        resultado.EsFallido.Should().BeTrue(caso);
        resultado.Error.Codigo.Should().Be(codigo, caso);
        _limitador.Libres.Should().Be(1, "con el hijo terminado el turno se suelta siempre");
        _limitador.Degradado.Should().BeFalse(caso);
    }

    [Fact]
    public async Task Una_union_cuya_longitud_no_cuadra_con_lo_recibido_es_ilegible()
    {
        var union = Union(Result.Exito(new PdfUnido("%PDF-1.4 unido"u8.ToArray(), 2)));

        var resultado = await Unidor(new LanzadorDoble(_ => Terminado(0, union[..^1]))).UnirAsync([PdfA, PdfB], CancellationToken.None);

        resultado.Error.Codigo.Should().Be(ErroresUnirPdf.Ilegible);
    }

    [Fact]
    public async Task Una_union_cuyo_contenido_no_empieza_como_un_PDF_es_ilegible()
    {
        // Longitud y páginas cuadran; lo único que falla es la cabecera: «%PDF» sin el guion no es la de un PDF.
        var union = Union(Result.Exito(new PdfUnido("%PDF unido"u8.ToArray(), 2)));

        var resultado = await Unidor(new LanzadorDoble(_ => Terminado(0, union))).UnirAsync([PdfA, PdfB], CancellationToken.None);

        resultado.Error.Codigo.Should().Be(ErroresUnirPdf.Ilegible);
    }

    [Fact]
    public async Task Con_el_hijo_terminado_el_turno_se_suelta_y_la_herramienta_sigue_activa()
    {
        var lanzador = new LanzadorDoble(_ => Terminado(0, Union(Result.Exito(new PdfUnido("%PDF-1.4 unido"u8.ToArray(), 2)))));

        await Unidor(lanzador).UnirAsync([PdfA, PdfB], CancellationToken.None);
        var segunda = await Unidor(lanzador).UnirAsync([PdfA, PdfB], CancellationToken.None);

        segunda.EsExitoso.Should().BeTrue();
        lanzador.Llamadas.Should().Be(2);
        _limitador.Libres.Should().Be(1);
        _limitador.Degradado.Should().BeFalse();
    }

    [Fact]
    public async Task Si_no_se_confirma_que_el_hijo_termino_el_turno_no_se_suelta_y_la_herramienta_queda_desactivada()
    {
        // Falló Kill, o el hijo no salió en el margen tras matarlo: el lanzador no puede confirmar su muerte.
        var registro = new RegistroDoble();
        var lanzador = new LanzadorDoble(_ => throw new ProcesoAisladoSinTerminarException(4321));
        var unidor = new UnidorPdfEnProcesoAparte(_opciones, _limitador, lanzador, registro);

        var unir = () => unidor.UnirAsync([PdfA, PdfB], CancellationToken.None);

        await unir.Should().ThrowAsync<EntradaPdfRetenidaException>("quien reservó memoria para los PDF tampoco la devuelve");
        _limitador.Libres.Should().Be(0, "el hijo quizá vivo conserva su hueco: otro proceso no puede ocuparlo");
        _limitador.Degradado.Should().BeTrue();
        (await _limitador.EntrarAsync(TimeSpan.Zero, CancellationToken.None)).Should().BeNull("ningún otro proceso recibe turno");
        registro.Entradas.Should().ContainSingle(e => e.Nivel == LogLevel.Critical)
            .Which.Mensaje.Should().Contain("4321").And.NotContain("%PDF", "sin datos de los Documentos");
    }

    [Fact]
    public async Task Desactivada_la_herramienta_una_union_posterior_pide_reintentar_en_el_acto_sin_lanzar_nada()
    {
        var lanzador = new LanzadorDoble(_ => throw new ProcesoAisladoSinTerminarException(4321));
        // Una espera de turno larga: si la unión posterior esperase turno en vez de responder en el acto, el test vencería.
        var opciones = _opciones with { EsperaMaximaTurno = TimeSpan.FromMinutes(5) };
        var unidor = new UnidorPdfEnProcesoAparte(opciones, _limitador, lanzador, NullLogger<UnidorPdfEnProcesoAparte>.Instance);
        await unidor.Invoking(u => u.UnirAsync([PdfA, PdfB], CancellationToken.None)).Should().ThrowAsync<EntradaPdfRetenidaException>();

        var unir = () => unidor.UnirAsync([PdfA, PdfB], CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        var inspeccionar = () => unidor.InspeccionarAsync(PdfA, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        await unir.Should().ThrowAsync<PresupuestoMemoriaAgotadoException>();
        await inspeccionar.Should().ThrowAsync<PresupuestoMemoriaAgotadoException>();
        lanzador.Llamadas.Should().Be(1, "solo la unión que degradó llegó a lanzar");
    }

    [Fact]
    public async Task Si_el_hijo_no_se_confirma_terminado_y_el_llamante_cancelo_gana_la_entrada_retenida_y_la_herramienta_queda_desactivada()
    {
        using var cancelacion = new CancellationTokenSource();
        var lanzador = new LanzadorDoble(_ =>
        {
            cancelacion.Cancel();
            throw new ProcesoAisladoSinTerminarException(4321);
        });

        var inspeccionar = () => Unidor(lanzador).InspeccionarAsync(PdfA, cancelacion.Token);

        await inspeccionar.Should().ThrowAsync<EntradaPdfRetenidaException>("una cancelación haría que el llamante devolviera la memoria de los PDF");
        _limitador.Libres.Should().Be(0);
        _limitador.Degradado.Should().BeTrue();
    }

    [Fact]
    public async Task Si_el_lanzador_lanza_cualquier_otra_excepcion_sin_confirmar_la_muerte_el_turno_se_retiene()
    {
        // Por ejemplo, un fallo del proveedor de registro mientras confirmaba: no es una ProcesoAisladoSinTerminarException.
        var lanzador = new LanzadorDoble(_ => throw new InvalidOperationException("el registro falló")) { SinConfirmar = true };

        var unir = () => Unidor(lanzador).UnirAsync([PdfA, PdfB], CancellationToken.None);

        await unir.Should().ThrowAsync<EntradaPdfRetenidaException>();
        _limitador.Libres.Should().Be(0, "sin confirmación positiva el turno no se suelta, sea cual sea la excepción");
        _limitador.Degradado.Should().BeTrue();
    }

    [Fact]
    public async Task Si_el_lanzador_confirma_la_muerte_y_luego_lanza_el_turno_se_suelta_y_la_excepcion_sigue()
    {
        var lanzador = new LanzadorDoble(_ => throw new InvalidOperationException("fallo con el hijo ya muerto"));

        var unir = () => Unidor(lanzador).UnirAsync([PdfA, PdfB], CancellationToken.None);

        await unir.Should().ThrowAsync<InvalidOperationException>();
        _limitador.Libres.Should().Be(1);
        _limitador.Degradado.Should().BeFalse();
    }

    [Fact]
    public async Task Un_registro_que_falla_al_desactivar_la_herramienta_no_suelta_el_turno()
    {
        var lanzador = new LanzadorDoble(_ => throw new ProcesoAisladoSinTerminarException(4321));
        var unidor = new UnidorPdfEnProcesoAparte(_opciones, _limitador, lanzador, new RegistroDoble { Fallar = true });

        var unir = () => unidor.UnirAsync([PdfA, PdfB], CancellationToken.None);

        await unir.Should().ThrowAsync<EntradaPdfRetenidaException>();
        _limitador.Libres.Should().Be(0);
        _limitador.Degradado.Should().BeTrue();
    }

    [Fact]
    public async Task Si_el_lanzador_se_bloquea_antes_de_devolver_su_tarea_la_espera_exterior_lo_acota_y_el_turno_se_retiene()
    {
        // Como un Process.Start que no vuelve: el bloqueo es síncrono, antes de que exista ninguna tarea que esperar.
        using var bloqueo = new ManualResetEventSlim();
        var registro = new RegistroDoble();
        var lanzador = new LanzadorDoble(_ => Terminado(0, [])) { Bloqueo = bloqueo };
        var opciones = _opciones with { TiempoMaximoInspeccionar = TimeSpan.FromMilliseconds(50), MargenConfirmacionTerminado = TimeSpan.FromMilliseconds(50) };
        var unidor = new UnidorPdfEnProcesoAparte(opciones, _limitador, lanzador, registro);
        try
        {
            // En otro hilo: si el unidor volviera a bloquearse en el tramo síncrono, el test vencería en vez de colgarse.
            var inspeccionar = () => Task.Run(() => unidor.InspeccionarAsync(PdfA, CancellationToken.None)).WaitAsync(TimeSpan.FromSeconds(10));

            await inspeccionar.Should().ThrowAsync<EntradaPdfRetenidaException>("un TimeoutException diría que la espera exterior no cubrió el bloqueo");
            _limitador.Libres.Should().Be(0);
            _limitador.Degradado.Should().BeTrue();
            registro.Entradas.Should().ContainSingle(e => e.Nivel == LogLevel.Critical).Which.Mensaje.Should().Contain("no consta que llegara a arrancar");
        }
        finally
        {
            bloqueo.Set();
        }
    }

    [Fact]
    public async Task Con_capacidad_dos_quien_ya_tenia_turno_cuando_se_degrado_no_lanza()
    {
        var limitador = new LimitadorProcesosAislados(2);
        var otro = await limitador.EntrarAsync(TimeSpan.Zero, CancellationToken.None);
        // La otra unión degrada la instancia justo después de que esta pasara la última comprobación del limitador.
        limitador.AlDarTurno = () => otro!.RetenerPorHijoSinConfirmar();
        var lanzador = new LanzadorDoble(_ => Terminado(0, []));
        var unidor = new UnidorPdfEnProcesoAparte(_opciones, limitador, lanzador, NullLogger<UnidorPdfEnProcesoAparte>.Instance);

        var inspeccionar = () => unidor.InspeccionarAsync(PdfA, CancellationToken.None);

        await inspeccionar.Should().ThrowAsync<PresupuestoMemoriaAgotadoException>();
        lanzador.Llamadas.Should().Be(0, "degradada la instancia no se lanza ningún proceso más");
        limitador.Libres.Should().Be(1, "el turno que no se usó vuelve; el retenido no");
    }

    [Fact]
    public async Task Si_el_lanzador_no_vuelve_ni_al_cancelar_el_turno_no_se_suelta_y_la_herramienta_queda_desactivada()
    {
        // No pudo matar al hijo y la lectura no atiende la cancelación: el lanzador no vuelve nunca, ni con el token.
        var registro = new RegistroDoble();
        var lanzador = new LanzadorDoble(_ => Terminado(0, [])) { NoVolver = true };
        var opciones = _opciones with { TiempoMaximoInspeccionar = TimeSpan.FromMilliseconds(50), MargenConfirmacionTerminado = TimeSpan.FromMilliseconds(50) };
        var unidor = new UnidorPdfEnProcesoAparte(opciones, _limitador, lanzador, registro);

        var inspeccionar = () => unidor.InspeccionarAsync(PdfA, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        await inspeccionar.Should().ThrowAsync<EntradaPdfRetenidaException>("la espera acotada no confirma que el hijo muriera");
        _limitador.Libres.Should().Be(0, "el único turno se queda con el hijo quizá vivo");
        _limitador.Degradado.Should().BeTrue();
        registro.Entradas.Should().ContainSingle(e => e.Nivel == LogLevel.Critical)
            .Which.Mensaje.Should().Contain(LanzadorDoble.IdHijo.ToString(), "el PID se publicó al arrancar, aunque el lanzador no volviera");
    }

    [Fact]
    public async Task Si_el_lanzador_no_vuelve_y_el_llamante_cancela_gana_la_entrada_retenida_y_la_herramienta_queda_desactivada()
    {
        var lanzador = new LanzadorDoble(_ => Terminado(0, [])) { NoVolver = true };
        var opciones = _opciones with { TiempoMaximoInspeccionar = TimeSpan.FromMilliseconds(50), MargenConfirmacionTerminado = TimeSpan.FromMilliseconds(50) };
        var unidor = new UnidorPdfEnProcesoAparte(opciones, _limitador, lanzador, NullLogger<UnidorPdfEnProcesoAparte>.Instance);
        using var cancelacion = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));

        var inspeccionar = () => unidor.InspeccionarAsync(PdfA, cancelacion.Token).WaitAsync(TimeSpan.FromSeconds(10));

        await inspeccionar.Should().ThrowAsync<EntradaPdfRetenidaException>();
        _limitador.Libres.Should().Be(0);
        _limitador.Degradado.Should().BeTrue();
    }

    [Fact]
    public async Task Si_el_llamante_cancela_la_cancelacion_sigue_y_el_turno_se_suelta()
    {
        var lanzador = new LanzadorDoble(_ => Terminado(0, [])) { Esperar = true };
        using var cancelacion = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        var inspeccionar = () => Unidor(lanzador).InspeccionarAsync(PdfA, cancelacion.Token);

        await inspeccionar.Should().ThrowAsync<OperationCanceledException>();
        _limitador.Libres.Should().Be(1);
        _limitador.Degradado.Should().BeFalse("el lanzador confirmó la muerte al cancelar");
    }

    [Fact]
    public async Task Sin_turno_a_tiempo_pide_reintentar_sin_lanzar_nada()
    {
        var lanzador = new LanzadorDoble(_ => Terminado(0, []));
        using var ocupado = await _limitador.EntrarAsync(TimeSpan.Zero, CancellationToken.None);

        var unir = () => Unidor(lanzador).UnirAsync([PdfA, PdfB], CancellationToken.None);

        await unir.Should().ThrowAsync<PresupuestoMemoriaAgotadoException>("es el transitorio que la pantalla traduce a «inténtalo de nuevo»");
        lanzador.Llamadas.Should().Be(0);
    }

    [Fact]
    public async Task La_espera_de_turno_se_corta_si_el_llamante_cancela()
    {
        var lanzador = new LanzadorDoble(_ => Terminado(0, []));
        var opciones = _opciones with { EsperaMaximaTurno = TimeSpan.FromMinutes(5) };
        using var ocupado = await _limitador.EntrarAsync(TimeSpan.Zero, CancellationToken.None);
        using var cancelacion = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        var inspeccionar = () => new UnidorPdfEnProcesoAparte(opciones, _limitador, lanzador, NullLogger<UnidorPdfEnProcesoAparte>.Instance)
            .InspeccionarAsync(PdfA, cancelacion.Token);

        await inspeccionar.Should().ThrowAsync<OperationCanceledException>();
        lanzador.Llamadas.Should().Be(0);
    }

    [Fact]
    public void El_proceso_aparte_responde_a_una_inspeccion_y_a_una_union_reales_y_rechaza_una_peticion_que_no_cuadra()
    {
        var uno = UnionSobrePdfRealTests.Pdf("Uno");
        var dos = UnionSobrePdfRealTests.Pdf("Dos", "Tres");

        var inspeccion = Ejecutar(Peticion(ProtocoloProcesoHerramientasPdf.OperacionInspeccionar, dos));
        inspeccion.Codigo.Should().Be(ProcesoHerramientasPdf.CodigoRespuestaEscrita);
        ProtocoloProcesoHerramientasPdf.LeerInspeccion(inspeccion.Salida)!.Valor.NumeroPaginas.Should().Be(2);

        var union = Ejecutar(Peticion(ProtocoloProcesoHerramientasPdf.OperacionUnir, uno, dos));
        ProtocoloProcesoHerramientasPdf.LeerUnion(union.Salida)!.Valor.NumeroPaginas.Should().Be(3);

        Ejecutar(Peticion(9, uno)).Codigo.Should().Be(ProcesoHerramientasPdf.CodigoPeticionInvalida, "operación desconocida");
        Ejecutar(Peticion(ProtocoloProcesoHerramientasPdf.OperacionInspeccionar, uno, dos)).Codigo
            .Should().Be(ProcesoHerramientasPdf.CodigoPeticionInvalida, "una inspección es de un solo PDF");
        Ejecutar([.. Peticion(ProtocoloProcesoHerramientasPdf.OperacionInspeccionar, uno), 0]).Codigo
            .Should().Be(ProcesoHerramientasPdf.CodigoPeticionInvalida, "nada detrás de la petición");
        Ejecutar(Peticion(ProtocoloProcesoHerramientasPdf.OperacionInspeccionar, uno)[..^1]).Codigo
            .Should().Be(ProcesoHerramientasPdf.CodigoPeticionInvalida, "petición cortada");
    }

    private static byte[] Peticion(byte operacion, params byte[][] pdfs)
    {
        using var destino = new MemoryStream();
        ProtocoloProcesoHerramientasPdf.EscribirPeticionAsync(destino, operacion, pdfs, CancellationToken.None).GetAwaiter().GetResult();
        return destino.ToArray();
    }

    private static (int Codigo, byte[] Salida) Ejecutar(byte[] peticion)
    {
        using var salida = new MemoryStream();
        var codigo = ProcesoHerramientasPdf.Ejecutar(new MemoryStream(peticion), salida);
        return (codigo, salida.ToArray());
    }

    /// <summary>Anota cada entrada del registro con su nivel y su mensaje ya formateado.</summary>
    private sealed class RegistroDoble : ILogger<UnidorPdfEnProcesoAparte>
    {
        public List<(LogLevel Nivel, string Mensaje)> Entradas { get; } = [];

        /// <summary>Un proveedor de registro que lanza en cada entrada.</summary>
        public bool Fallar { get; init; }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (Fallar)
                throw new InvalidOperationException("el proveedor de registro falla");
            Entradas.Add((logLevel, formatter(state, exception)));
        }
    }

    /// <summary>
    /// Anota lo que se le pide y responde lo que diga el test, como el lanzador real: publica el PID del hijo al arrancar y
    /// confirma su muerte en el seguimiento al volver, salvo con <see cref="ProcesoAisladoSinTerminarException"/> o con
    /// <see cref="SinConfirmar"/>. Con <see cref="Esperar"/>, no responde hasta que se cancele; con <see cref="NoVolver"/>, no
    /// responde nunca, ni al cancelar (un hijo que no se pudo matar); con <see cref="Bloqueo"/>, se bloquea antes de devolver
    /// su tarea, como un <c>Process.Start</c> que no vuelve.
    /// </summary>
    private sealed class LanzadorDoble(Func<byte[], SalidaProcesoAislado> responder) : ILanzadorProcesoAislado
    {
        public const int IdHijo = 4321;

        public bool Esperar { get; init; }
        public bool NoVolver { get; init; }
        public bool SinConfirmar { get; init; }
        public ManualResetEventSlim? Bloqueo { get; init; }
        public int Llamadas { get; private set; }
        public byte[]? Entrada { get; private set; }
        public TimeSpan Tiempo { get; private set; }
        public int MaximoSalida { get; private set; }

        public async Task<SalidaProcesoAislado> EjecutarAsync(ProcessStartInfo inicio, Func<Stream, CancellationToken, Task> escribirEntrada,
            TimeSpan tiempoMaximo, int maximoBytesSalida, CancellationToken cancellationToken, SeguimientoProcesoAislado? seguimiento = null)
        {
            Llamadas++;
            Tiempo = tiempoMaximo;
            MaximoSalida = maximoBytesSalida;
            // Antes del primer await: bloquea a quien llama, que aún no tiene ninguna tarea que esperar.
            Bloqueo?.Wait();
            seguimiento?.Arrancado(IdHijo);
            using var entrada = new MemoryStream();
            await escribirEntrada(entrada, cancellationToken);
            Entrada = entrada.ToArray();
            if (Esperar)
            {
                try
                {
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    seguimiento?.ConfirmarTerminado();
                    throw;
                }
            }

            if (NoVolver)
                await new TaskCompletionSource().Task;

            SalidaProcesoAislado salida;
            try
            {
                salida = responder(Entrada);
            }
            catch (Exception ex) when (ex is not ProcesoAisladoSinTerminarException && !SinConfirmar)
            {
                seguimiento?.ConfirmarTerminado();
                throw;
            }

            if (!SinConfirmar)
                seguimiento?.ConfirmarTerminado();
            return salida;
        }
    }
}
