using CaeManager.Infrastructure.Configuracion;
using CaeManager.Infrastructure.Coordinacion;
using CaeManager.Infrastructure.DataProtection;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.Integraciones;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CaeManager.Web.Tests.GatesDeConfiguracion;

/// <summary>
/// Un gate de configuración cerrado por configuración a medias no dejaba rastro:
/// la aplicación arrancaba sana y la pieza simplemente no existía. Estas pruebas
/// fijan (1) que el gate sigue decidiendo EXACTAMENTE lo mismo que antes, (2) que
/// el motivo del aviso sale de la misma lista de requisitos que el veredicto, y
/// (3) que el aviso nombra opciones y nunca valores.
/// </summary>
public class GatesDeConfiguracionAMediasTests
{
    private const string Secreto = "VALOR-SECRETO-QUE-NO-DEBE-SALIR";

    /// <summary>
    /// Una sección bajo prueba: cómo construirla dado qué claves están informadas,
    /// el veredicto que tenía el gate ANTES de este cambio (copiado literalmente
    /// de cada <c>EstaConfigurado</c> anterior, no derivado del código nuevo) y
    /// los nombres de las claves.
    /// </summary>
    public sealed record Caso(
        string Nombre,
        string[] Claves,
        bool TieneInterruptor,
        Func<bool, bool[], IOpcionesConGate> Construir,
        Func<bool, bool[], bool> VeredictoAnterior)
    {
        public override string ToString() => Nombre;
    }

    private static string? Valor(bool informada) => informada ? Secreto : null;

    public static IEnumerable<object[]> Casos() =>
    [
        [new Caso("DataProtection:Kms", ["KeyId", "AccessKeyId", "SecretAccessKey", "Region"], true,
            (activo, k) => new DataProtectionKmsOptions
            {
                Activo = activo, KeyId = Valor(k[0]), AccessKeyId = Valor(k[1]), SecretAccessKey = Valor(k[2]), Region = Valor(k[3]),
            },
            (activo, k) => activo && k[0] && k[1] && k[2] && k[3])],
        [new Caso("DataProtection:S3", ["AccessKeyId", "SecretAccessKey", "BucketName", "Region"], true,
            (activo, k) => new DataProtectionS3Options
            {
                Activo = activo, AccessKeyId = Valor(k[0]), SecretAccessKey = Valor(k[1]), BucketName = Valor(k[2]), Region = Valor(k[3]),
            },
            (activo, k) => activo && k[0] && k[1] && k[2] && k[3])],
        [new Caso("SignalR:Redis", ["CadenaConexion"], true,
            (activo, k) => new SignalRRedisOptions { Activo = activo, CadenaConexion = Valor(k[0]) },
            (activo, k) => activo && k[0])],
        [new Caso("Integraciones:WhatsApp", ["AppSecret", "VerifyToken"], false,
            (_, k) => new WhatsAppCloudApiOptions { AppSecret = Valor(k[0]), VerifyToken = Valor(k[1]) },
            (_, k) => k[0] && k[1])],
        [new Caso("AzureAd", ["TenantId", "ClientId", "ClientSecret"], false,
            (_, k) => new AzureAdOptions { TenantId = Valor(k[0]), ClientId = Valor(k[1]), ClientSecret = Valor(k[2]) },
            (_, k) => k[0] && k[1] && k[2])],
    ];

    private static IEnumerable<bool[]> Combinaciones(int n) =>
        Enumerable.Range(0, 1 << n).Select(mascara => Enumerable.Range(0, n).Select(i => (mascara & (1 << i)) != 0).ToArray());

    [Theory]
    [MemberData(nameof(Casos))]
    public void El_gate_decide_exactamente_lo_que_decidia_antes_en_toda_combinacion(Caso caso)
    {
        var comprobadas = 0;
        foreach (var activo in new[] { false, true })
        foreach (var claves in Combinaciones(caso.Claves.Length))
        {
            caso.Construir(activo, claves).EstaConfigurado.Should().Be(
                caso.VeredictoAnterior(activo, claves),
                $"{caso.Nombre} con Activo={activo} y claves [{string.Join(",", claves)}]");
            comprobadas++;
        }

        comprobadas.Should().Be(2 * (1 << caso.Claves.Length), "la rejilla entera, no una muestra");
    }

    [Theory]
    [MemberData(nameof(Casos))]
    public void Un_valor_en_blanco_cuenta_como_no_informado(Caso caso)
    {
        var opciones = caso.Construir(true, caso.Claves.Select(_ => true).ToArray());
        opciones.EstaConfigurado.Should().BeTrue("control positivo: con todo informado el gate está abierto");

        GateDeConfiguracion.Evaluar(true, ("Clave", "   ")).Completo.Should().BeFalse();
        GateDeConfiguracion.Evaluar(true, ("Clave", "")).Completo.Should().BeFalse();
    }

    [Theory]
    [MemberData(nameof(Casos))]
    public void Sin_nada_informado_y_apagado_no_hay_aviso_porque_apagado_es_lo_normal(Caso caso)
    {
        caso.Construir(false, caso.Claves.Select(_ => false).ToArray()).ProblemasDeConfiguracion().Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(Casos))]
    public void Completo_no_avisa(Caso caso)
    {
        caso.Construir(true, caso.Claves.Select(_ => true).ToArray()).ProblemasDeConfiguracion().Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(Casos))]
    public void Con_una_clave_de_menos_avisa_de_esa_y_solo_de_esa(Caso caso)
    {
        if (caso.Claves.Length < 2)
            return; // con una sola clave "a medias" no existe: o está o no está; lo cubre el caso del interruptor

        for (var i = 0; i < caso.Claves.Length; i++)
        {
            var claves = caso.Claves.Select((_, j) => j != i).ToArray();
            var problemas = caso.Construir(true, claves).ProblemasDeConfiguracion();

            problemas.Should().ContainSingle($"solo falta {caso.Claves[i]}")
                .Which.Should().Be($"falta {caso.Claves[i]}");
        }
    }

    [Theory]
    [MemberData(nameof(Casos))]
    public void Con_solo_una_clave_informada_avisa_de_todas_las_demas(Caso caso)
    {
        if (caso.Claves.Length < 2)
            return;

        var claves = caso.Claves.Select((_, j) => j == 0).ToArray();
        var problemas = caso.Construir(true, claves).ProblemasDeConfiguracion();

        problemas.Should().BeEquivalentTo(caso.Claves.Skip(1).Select(c => $"falta {c}"));
    }

    [Theory]
    [MemberData(nameof(Casos))]
    public void Con_interruptor_encendido_y_nada_mas_informado_avisa_de_todo_lo_que_falta(Caso caso)
    {
        if (!caso.TieneInterruptor)
            return;

        var problemas = caso.Construir(true, caso.Claves.Select(_ => false).ToArray()).ProblemasDeConfiguracion();

        problemas.Should().BeEquivalentTo(caso.Claves.Select(c => $"falta {c}"),
            "activarlo y no informar nada es un descuido, no «apagado por defecto»");
    }

    [Theory]
    [MemberData(nameof(Casos))]
    public void Con_interruptor_apagado_no_avisa_aunque_falte_algo(Caso caso)
    {
        if (!caso.TieneInterruptor || caso.Claves.Length < 2)
            return;

        var claves = caso.Claves.Select((_, j) => j == 0).ToArray();
        caso.Construir(false, claves).ProblemasDeConfiguracion().Should().BeEmpty("apagarlo es una decisión");
    }

    [Theory]
    [MemberData(nameof(Casos))]
    public void Sin_interruptor_y_sin_nada_informado_no_avisa(Caso caso)
    {
        if (caso.TieneInterruptor)
            return;

        caso.Construir(true, caso.Claves.Select(_ => false).ToArray()).ProblemasDeConfiguracion().Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(Casos))]
    public void Los_problemas_nombran_opciones_y_nunca_valores(Caso caso)
    {
        var todos = Combinaciones(caso.Claves.Length)
            .SelectMany(claves => caso.Construir(true, claves).ProblemasDeConfiguracion())
            .ToList();

        todos.Should().NotBeEmpty("control positivo: la rejilla produce avisos, así que la comprobación de abajo mira algo");
        todos.Should().NotContain(p => p.Contains(Secreto), "el aviso nombra la opción, jamás lo que contiene");
    }

    // ── El aviso de arranque ──────────────────────────────────────────────────

    private static (ServiceProvider Proveedor, CapturaDeLog Captura) Componer(Action<IServiceCollection> registrar)
    {
        var captura = new CapturaDeLog();
        var servicios = new ServiceCollection();
        servicios.AddLogging(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(captura));
        registrar(servicios);
        return (servicios.BuildServiceProvider(), captura);
    }

    private static async Task Arrancar(ServiceProvider proveedor)
    {
        foreach (var servicio in proveedor.GetServices<IHostedService>())
            await servicio.StartAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Un_gate_a_medias_deja_un_warning_que_dice_seccion_que_falta_y_que_deja_de_existir()
    {
        var (proveedor, captura) = Componer(s => s.AvisarSiConfiguracionAMedias(
            DataProtectionS3Options.SeccionConfiguracion,
            new DataProtectionS3Options { Activo = true, AccessKeyId = Secreto, SecretAccessKey = Secreto, BucketName = "b" },
            "El llavero NO se ha movido a S3."));

        await Arrancar(proveedor);

        var aviso = captura.Entradas.Should().ContainSingle().Which;
        aviso.Nivel.Should().Be(LogLevel.Warning);
        aviso.Texto.Should().Contain("DataProtection:S3").And.Contain("falta Region").And.Contain("NO se ha movido a S3");
        aviso.Texto.Should().NotContain(Secreto).And.NotContain("falta AccessKeyId", "solo lo que falta de verdad");
    }

    [Fact]
    public async Task Varios_gates_a_medias_comparten_un_unico_servicio_y_cada_uno_avisa_una_vez()
    {
        var (proveedor, captura) = Componer(s =>
        {
            s.AvisarSiConfiguracionAMedias(SignalRRedisOptions.SeccionConfiguracion,
                new SignalRRedisOptions { Activo = true }, "consecuencia-redis");
            s.AvisarSiConfiguracionAMedias(WhatsAppCloudApiOptions.SeccionConfiguracion,
                new WhatsAppCloudApiOptions { AppSecret = Secreto }, "consecuencia-whatsapp");
        });

        proveedor.GetServices<IHostedService>().Should().ContainSingle(
            "el servicio de aviso es único aunque haya varios gates a medias");

        await Arrancar(proveedor);

        captura.Entradas.Should().HaveCount(2);
        captura.Entradas.Should().Contain(e => e.Texto.Contains("SignalR:Redis") && e.Texto.Contains("falta CadenaConexion") && e.Texto.Contains("consecuencia-redis"));
        captura.Entradas.Should().Contain(e => e.Texto.Contains("Integraciones:WhatsApp") && e.Texto.Contains("falta VerifyToken") && e.Texto.Contains("consecuencia-whatsapp"));
        captura.Entradas.Should().OnlyContain(e => !e.Texto.Contains(Secreto));
    }

    [Fact]
    public void Apagado_o_completo_no_registra_ningun_servicio_de_arranque()
    {
        var (proveedor, _) = Componer(s =>
        {
            s.AvisarSiConfiguracionAMedias(WhatsAppCloudApiOptions.SeccionConfiguracion, new WhatsAppCloudApiOptions(), "x");
            s.AvisarSiConfiguracionAMedias(WhatsAppCloudApiOptions.SeccionConfiguracion,
                new WhatsAppCloudApiOptions { AppSecret = "a", VerifyToken = "b" }, "x");
        });

        proveedor.GetServices<IHostedService>().Should().BeEmpty(
            "sin configuración a medias no hay nada que avisar ni servicio que arrancar");
    }

    [Fact]
    public async Task Un_fallo_al_avisar_no_tumba_el_arranque()
    {
        var captura = new CapturaDeLog { LanzarAlEscribirElPrimero = true };
        var servicios = new ServiceCollection();
        servicios.AddLogging(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(captura));
        servicios.AvisarSiConfiguracionAMedias(SignalRRedisOptions.SeccionConfiguracion, new SignalRRedisOptions { Activo = true }, "x");
        var proveedor = servicios.BuildServiceProvider();

        var arrancar = () => Arrancar(proveedor);

        await arrancar.Should().NotThrowAsync("un aviso de arranque no puede abortar el arranque");
    }

    // ── Captura de log ────────────────────────────────────────────────────────

    public sealed record Entrada(LogLevel Nivel, string Texto);

    public sealed class CapturaDeLog : ILoggerProvider
    {
        public List<Entrada> Entradas { get; } = [];
        public bool LanzarAlEscribirElPrimero { get; set; }
        private bool _yaLanzo;

        public ILogger CreateLogger(string categoria) => new Registro(this);

        public void Dispose() { }

        private sealed class Registro(CapturaDeLog captura) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (captura.LanzarAlEscribirElPrimero && !captura._yaLanzo)
                {
                    captura._yaLanzo = true;
                    throw new InvalidOperationException("fallo simulado del proveedor de log");
                }

                captura.Entradas.Add(new Entrada(logLevel, formatter(state, exception)));
            }
        }
    }
}
