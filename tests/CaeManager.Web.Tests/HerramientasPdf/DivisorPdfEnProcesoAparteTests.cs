using CaeManager.Application.Common;
using CaeManager.Application.HerramientasPdf;
using CaeManager.Domain.Common;
using CaeManager.Infrastructure.DependencyInjection;
using CaeManager.Infrastructure.HerramientasPdf;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using LanzadorDoble = CaeManager.Web.Tests.HerramientasPdf.UnidorPdfEnProcesoAparteTests.LanzadorDoble;

namespace CaeManager.Web.Tests.HerramientasPdf;

/// <summary>
/// El cliente de Dividir del proceso aparte con un doble del lanzador: qué pide al hijo al abrir y al extraer un tramo (con
/// qué tiempo y qué tope de respuesta), qué decide sin lanzar nada, cómo traduce cada desenlace del hijo (respuesta, fallo,
/// muerte, tiempo agotado) a los códigos de <see cref="ErroresDivisorPdf"/>, y que Unir y Dividir comparten el mismo turno:
/// un hijo de Dividir que no consta terminado desactiva también Unir. Que el proceso real se mata y que su límite de memoria
/// corta lo prueba <see cref="ProcesoAisladoRealTests"/>.
/// </summary>
public sealed class DivisorPdfEnProcesoAparteTests
{
    private static readonly byte[] Original = "%PDF-1.7 original de prueba"u8.ToArray();
    private static readonly byte[] TramoPdf = "%PDF-1.7 tramo"u8.ToArray();

    private readonly OpcionesProcesoHerramientasPdf _opciones = new()
    {
        TiempoMaximoAbrirParaDividir = TimeSpan.FromSeconds(13),
        TiempoMaximoExtraerTramo = TimeSpan.FromSeconds(17),
        EsperaMaximaTurno = TimeSpan.FromMilliseconds(100),
        Anfitrion = "anfitrion-dotnet",
        RutaEnsamblado = "aplicacion.dll",
    };

    private readonly LimitadorProcesosAislados _limitador = new(1);

    private DivisorPdfEnProcesoAparte Divisor(ILanzadorProcesoAislado lanzador) =>
        new(_opciones, _limitador, lanzador, NullLogger<DivisorPdfEnProcesoAparte>.Instance);

    private static SalidaProcesoAislado Terminado(int codigo, byte[] salida) =>
        new(DesenlaceProcesoAislado.Terminado, codigo, salida, 1234);

    private static byte[] Apertura(Result<(int NumeroPaginas, bool EstaFirmado)> resultado)
    {
        using var salida = new MemoryStream();
        ProtocoloProcesoHerramientasPdf.EscribirApertura(salida, resultado);
        return salida.ToArray();
    }

    private static byte[] Tramo(Result<byte[]> resultado)
    {
        using var salida = new MemoryStream();
        ProtocoloProcesoHerramientasPdf.EscribirTramo(salida, resultado);
        return salida.ToArray();
    }

    private static ProtocoloProcesoHerramientasPdf.Peticion Peticion(LanzadorDoble lanzador) =>
        ProtocoloProcesoHerramientasPdf.LeerPeticion(new MemoryStream(lanzador.Entrada!))!;

    /// <summary>Responde a la apertura con 4 páginas sin firma y a cada tramo con <see cref="TramoPdf"/>.</summary>
    private static SalidaProcesoAislado Responder(byte[] entrada) =>
        ProtocoloProcesoHerramientasPdf.LeerPeticion(new MemoryStream(entrada))!.Operacion == ProtocoloProcesoHerramientasPdf.OperacionAbrirParaDividir
            ? Terminado(ProcesoHerramientasPdf.CodigoRespuestaEscrita, Apertura(Result.Exito((4, false))))
            : Terminado(ProcesoHerramientasPdf.CodigoRespuestaEscrita, Tramo(Result.Exito(TramoPdf)));

    [Fact]
    public async Task Abrir_manda_el_original_al_proceso_aparte_con_su_tiempo_y_devuelve_paginas_y_firma()
    {
        var lanzador = new LanzadorDoble(_ => Terminado(ProcesoHerramientasPdf.CodigoRespuestaEscrita, Apertura(Result.Exito((7, true)))));

        var abierto = await Divisor(lanzador).AbrirAsync(Original, CancellationToken.None);

        abierto.Valor.NumeroPaginas.Should().Be(7);
        abierto.Valor.EstaFirmado.Should().BeTrue();
        var peticion = Peticion(lanzador);
        peticion.Operacion.Should().Be(ProtocoloProcesoHerramientasPdf.OperacionAbrirParaDividir);
        peticion.Pdfs.Should().ContainSingle().Which.Should().Equal(Original);
        lanzador.Tiempo.Should().Be(_opciones.TiempoMaximoAbrirParaDividir);
        lanzador.MaximoSalida.Should().Be(ProtocoloProcesoHerramientasPdf.MaximoBytesRespuestaApertura);
        _limitador.Libres.Should().Be(1);
    }

    [Fact]
    public async Task Extraer_un_tramo_lanza_otro_proceso_con_el_original_el_rango_la_cota_y_su_tiempo()
    {
        var lanzador = new LanzadorDoble(Responder);
        using var pdf = (await Divisor(lanzador).AbrirAsync(Original, CancellationToken.None)).Valor;

        var tramo = await pdf.ExtraerPaginasAsync(2, 3, tamanoMaximoBytes: 5000, CancellationToken.None);

        tramo.Valor.Should().Equal(TramoPdf);
        lanzador.Llamadas.Should().Be(2, "abrir y extraer van cada uno a su proceso");
        var peticion = Peticion(lanzador);
        peticion.Operacion.Should().Be(ProtocoloProcesoHerramientasPdf.OperacionExtraerTramo);
        peticion.Pdfs.Should().ContainSingle().Which.Should().Equal(Original, "cada extracción vuelve a abrir el original");
        peticion.Tramo.Should().Be(new ProtocoloProcesoHerramientasPdf.ParametrosTramo(2, 3, 5000));
        lanzador.Tiempo.Should().Be(_opciones.TiempoMaximoExtraerTramo);
        lanzador.MaximoSalida.Should().Be(ProtocoloProcesoHerramientasPdf.MaximoBytesRespuestaTramo(5000));
        _limitador.Libres.Should().Be(1);
    }

    [Fact]
    public async Task Una_cota_por_encima_de_la_del_original_mas_grande_se_recorta_a_esa()
    {
        var lanzador = new LanzadorDoble(Responder);
        using var pdf = (await Divisor(lanzador).AbrirAsync(Original, CancellationToken.None)).Valor;

        await pdf.ExtraerPaginasAsync(1, 1, long.MaxValue, CancellationToken.None);

        Peticion(lanzador).Tramo!.Cota.Should().Be(ProtocoloProcesoHerramientasPdf.MaximaCotaTramo);
        lanzador.MaximoSalida.Should().Be(ProtocoloProcesoHerramientasPdf.MaximoBytesRespuestaTramo(ProtocoloProcesoHerramientasPdf.MaximaCotaTramo));
    }

    [Fact]
    public async Task Lo_que_se_decide_sin_abrir_no_lanza_ningun_proceso()
    {
        var lanzador = new LanzadorDoble(Responder);
        var divisor = Divisor(lanzador);

        (await divisor.AbrirAsync([], CancellationToken.None)).Error.Codigo.Should().Be(ErroresDivisorPdf.Ilegible);
        (await divisor.AbrirAsync(new byte[LimitesDivisionPdf.TamanoMaximoOriginalBytes + 1], CancellationToken.None))
            .Error.Codigo.Should().Be(ErroresDivisorPdf.DemasiadoGrande);
        lanzador.Llamadas.Should().Be(0);

        using var pdf = (await divisor.AbrirAsync(Original, CancellationToken.None)).Valor;
        (await pdf.ExtraerPaginasAsync(0, 1, 5000, CancellationToken.None)).Error.Codigo.Should().Be(ErroresDivisorPdf.RangoInvalido);
        (await pdf.ExtraerPaginasAsync(3, 2, 5000, CancellationToken.None)).Error.Codigo.Should().Be(ErroresDivisorPdf.RangoInvalido);
        (await pdf.ExtraerPaginasAsync(4, 5, 5000, CancellationToken.None)).Error.Codigo.Should().Be(ErroresDivisorPdf.RangoInvalido,
            "el PDF abierto tiene 4 páginas");
        lanzador.Llamadas.Should().Be(1, "solo la apertura llegó al proceso aparte");
    }

    [Fact]
    public async Task Un_fallo_que_el_hijo_responde_llega_con_su_codigo()
    {
        var lanzador = new LanzadorDoble(_ => Terminado(ProcesoHerramientasPdf.CodigoRespuestaEscrita,
            Apertura(Result.Fallo<(int, bool)>(Error.Crear(ErroresDivisorPdf.ConContrasena, "x")))));

        var abierto = await Divisor(lanzador).AbrirAsync(Original, CancellationToken.None);

        abierto.Error.Codigo.Should().Be(ErroresDivisorPdf.ConContrasena);
    }

    private static readonly Dictionary<string, (SalidaProcesoAislado Salida, string Codigo)> CasosDeDesenlace = new()
    {
        ["tiempo agotado"] = (new(DesenlaceProcesoAislado.TiempoAgotado, null, ReadOnlyMemory<byte>.Empty, 1), ErroresDivisorPdf.DemasiadoComplejo),
        ["salida de más"] = (new(DesenlaceProcesoAislado.SalidaDemasiadoGrande, null, ReadOnlyMemory<byte>.Empty, 1), ErroresDivisorPdf.DemasiadoComplejo),
        ["sin memoria"] = (Terminado(ProcesoHerramientasPdf.CodigoSinMemoria, []), ErroresDivisorPdf.DemasiadoComplejo),
        ["muerto por señal"] = (Terminado(137, []), ErroresDivisorPdf.DemasiadoComplejo),
        ["petición inválida"] = (Terminado(ProcesoHerramientasPdf.CodigoPeticionInvalida, []), ErroresDivisorPdf.Ilegible),
        ["respuesta basura"] = (Terminado(ProcesoHerramientasPdf.CodigoRespuestaEscrita, "no es una respuesta"u8.ToArray()), ErroresDivisorPdf.Ilegible),
    };

    public static TheoryData<string, bool> Desenlaces()
    {
        var casos = new TheoryData<string, bool>();
        foreach (var caso in CasosDeDesenlace.Keys)
        {
            casos.Add(caso, false);
            casos.Add(caso, true);
        }

        return casos;
    }

    [Theory]
    [MemberData(nameof(Desenlaces))]
    public async Task Cada_desenlace_del_hijo_al_abrir_y_al_extraer_sale_como_un_codigo_y_suelta_el_turno(string caso, bool alExtraer)
    {
        var (salida, codigo) = CasosDeDesenlace[caso];
        // Al extraer, la apertura responde bien y el desenlace es el del segundo proceso.
        var lanzador = new LanzadorDoble(entrada => alExtraer ? TramoODesenlace(entrada, salida) : salida);
        var divisor = Divisor(lanzador);

        var resultado = alExtraer
            ? await ExtraerAsync(divisor)
            : (await divisor.AbrirAsync(Original, CancellationToken.None)).Error;

        resultado.Codigo.Should().Be(codigo, caso);
        _limitador.Libres.Should().Be(1, "con el hijo terminado el turno se suelta siempre");
        _limitador.Degradado.Should().BeFalse(caso);
    }

    private static SalidaProcesoAislado TramoODesenlace(byte[] entrada, SalidaProcesoAislado desenlace) =>
        ProtocoloProcesoHerramientasPdf.LeerPeticion(new MemoryStream(entrada))!.Operacion == ProtocoloProcesoHerramientasPdf.OperacionAbrirParaDividir
            ? Responder(entrada)
            : desenlace;

    private static async Task<Error> ExtraerAsync(DivisorPdfEnProcesoAparte divisor)
    {
        using var pdf = (await divisor.AbrirAsync(Original, CancellationToken.None)).Valor;
        return (await pdf.ExtraerPaginasAsync(1, 2, 5000, CancellationToken.None)).Error;
    }

    [Fact]
    public async Task Un_tramo_que_pasa_de_su_cota_es_ilegible_aunque_el_hijo_lo_mande()
    {
        var lanzador = new LanzadorDoble(Responder);
        using var pdf = (await Divisor(lanzador).AbrirAsync(Original, CancellationToken.None)).Valor;

        var tramo = await pdf.ExtraerPaginasAsync(1, 1, tamanoMaximoBytes: TramoPdf.Length - 1, CancellationToken.None);

        tramo.Error.Codigo.Should().Be(ErroresDivisorPdf.Ilegible);
    }

    [Fact]
    public async Task Si_el_hijo_de_una_extraccion_no_consta_terminado_el_turno_se_retiene_y_la_herramienta_queda_desactivada()
    {
        var lanzador = new LanzadorDoble(entrada =>
            ProtocoloProcesoHerramientasPdf.LeerPeticion(new MemoryStream(entrada))!.Operacion == ProtocoloProcesoHerramientasPdf.OperacionAbrirParaDividir
                ? Responder(entrada)
                : throw new ProcesoAisladoSinTerminarException(4321));
        var divisor = Divisor(lanzador);
        var abierto = (await divisor.AbrirAsync(Original, CancellationToken.None)).Valor;

        var extraer = () => abierto.ExtraerPaginasAsync(1, 2, 5000, CancellationToken.None);

        await extraer.Should().ThrowAsync<EntradaPdfRetenidaException>("quien reservó memoria para el original tampoco la devuelve");
        _limitador.Libres.Should().Be(0);
        _limitador.Degradado.Should().BeTrue();
    }

    [Fact]
    public async Task Si_el_hijo_de_Dividir_no_consta_terminado_Unir_pide_reintentar_sin_lanzar_nada()
    {
        // El mismo limitador para las dos, como en la composición real: un solo hijo a la vez en la instancia.
        var deDividir = new LanzadorDoble(_ => throw new ProcesoAisladoSinTerminarException(4321));
        var deUnir = new LanzadorDoble(_ => Terminado(0, []));
        var unidor = new UnidorPdfEnProcesoAparte(_opciones, _limitador, deUnir, NullLogger<UnidorPdfEnProcesoAparte>.Instance);

        var abrir = () => Divisor(deDividir).AbrirAsync(Original, CancellationToken.None);
        await abrir.Should().ThrowAsync<EntradaPdfRetenidaException>();

        var unir = () => unidor.UnirAsync([Original, Original], CancellationToken.None);
        await unir.Should().ThrowAsync<PresupuestoMemoriaAgotadoException>("Unir queda desactivada con el hijo de Dividir quizá vivo");
        deUnir.Llamadas.Should().Be(0);
    }

    [Fact]
    public async Task Mientras_Unir_tiene_el_turno_Dividir_espera_y_pide_reintentar_sin_lanzar_nada()
    {
        var lanzador = new LanzadorDoble(Responder);
        using var deUnir = await _limitador.EntrarAsync(TimeSpan.Zero, CancellationToken.None);

        var abrir = () => Divisor(lanzador).AbrirAsync(Original, CancellationToken.None);

        await abrir.Should().ThrowAsync<PresupuestoMemoriaAgotadoException>();
        lanzador.Llamadas.Should().Be(0);
    }

    [Fact]
    public async Task En_la_composicion_real_Unir_y_Dividir_reciben_el_mismo_limitador()
    {
        // Los registros de verdad de AddInfrastructure, con un lanzador doble: si cada herramienta recibiera su propio
        // limitador, degradar el registrado no impediría que lanzaran.
        var reales = new ServiceCollection();
        reales.AddInfrastructure(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(), new EntornoFalso());
        Type[] tipos = [typeof(OpcionesProcesoHerramientasPdf), typeof(LimitadorProcesosAislados), typeof(IUnidorPdf), typeof(IDivisorPdf)];
        IServiceCollection servicios = new ServiceCollection();
        foreach (var registro in reales.Where(s => tipos.Contains(s.ServiceType)))
            servicios.Add(registro);
        var lanzador = new LanzadorDoble(_ => Terminado(0, []));
        servicios.AddSingleton<ILanzadorProcesoAislado>(lanzador);
        servicios.AddLogging();
        using var proveedor = servicios.BuildServiceProvider();

        var turno = await proveedor.GetRequiredService<LimitadorProcesosAislados>().EntrarAsync(TimeSpan.Zero, CancellationToken.None);
        turno!.RetenerPorHijoSinConfirmar();

        var abrir = () => proveedor.GetRequiredService<IDivisorPdf>().AbrirAsync(Original, CancellationToken.None);
        var inspeccionar = () => proveedor.GetRequiredService<IUnidorPdf>().InspeccionarAsync(Original, CancellationToken.None);
        await abrir.Should().ThrowAsync<PresupuestoMemoriaAgotadoException>();
        await inspeccionar.Should().ThrowAsync<PresupuestoMemoriaAgotadoException>();
        lanzador.Llamadas.Should().Be(0, "las dos herramientas ven el limitador registrado, ya degradado");
    }

    private sealed class EntornoFalso : Microsoft.Extensions.Hosting.IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "CaeManager";
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
