using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CaeManager.Application.Tests.Common;

public class LoggingBehaviorTests
{
    private record FalsoCommand(string Dni) : ICommand;
    private record FalsaQuery : IRequest<string>;

    private static readonly Guid TenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid UsuarioId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public async Task Registra_el_command_su_duracion_y_su_resultado()
    {
        var fabrica = new FabricaLoggerFalsa();
        var behavior = Construir<FalsoCommand, Result>(fabrica);

        await behavior.Handle(new FalsoCommand("12345678Z"), _ => Task.FromResult(Result.Exito()), CancellationToken.None);

        var evento = fabrica.Logger.Eventos.Should().ContainSingle().Subject;
        evento.Nivel.Should().Be(LogLevel.Information);
        evento.Mensaje.Should().Contain("FalsoCommand").And.Contain("DuracionMs");
    }

    [Fact]
    public async Task Un_result_fallido_se_registra_como_aviso_con_su_codigo()
    {
        var fabrica = new FabricaLoggerFalsa();
        var behavior = Construir<FalsoCommand, Result>(fabrica);
        var error = Error.Crear("Autorizacion.SoloLectura", "Tu rol no permite escribir.");

        await behavior.Handle(
            new FalsoCommand("12345678Z"), _ => Task.FromResult(Result.Fallo(error)), CancellationToken.None);

        var evento = fabrica.Logger.Eventos.Should().ContainSingle().Subject;
        evento.Nivel.Should().Be(LogLevel.Warning);
        evento.Propiedades["CodigoError"].Should().Be("Autorizacion.SoloLectura");
    }

    [Fact]
    public async Task Nunca_registra_el_contenido_del_request()
    {
        // El DNI del record no puede acabar en ningún log: sería un
        // tratamiento de datos personales fuera del inventario de
        // Project-Hydra-Negocio/tecnico/RGPD-TRATAMIENTO-DATOS.md, con su retención y sus derechos.
        var fabrica = new FabricaLoggerFalsa();
        var behavior = Construir<FalsoCommand, Result>(fabrica);

        await behavior.Handle(new FalsoCommand("12345678Z"), _ => Task.FromResult(Result.Exito()), CancellationToken.None);

        fabrica.Logger.Eventos.Should().OnlyContain(e => !e.Mensaje.Contains("12345678Z"));
    }

    [Fact]
    public async Task Correlaciona_cada_evento_con_tenant_y_usuario()
    {
        var fabrica = new FabricaLoggerFalsa();
        var behavior = Construir<FalsoCommand, Result>(fabrica);

        await behavior.Handle(new FalsoCommand("12345678Z"), _ => Task.FromResult(Result.Exito()), CancellationToken.None);

        var ambito = fabrica.Logger.Ambitos.Should().ContainSingle().Subject;
        ambito["TenantId"].Should().Be(TenantId);
        ambito["UsuarioId"].Should().Be(UsuarioId);
        // Sin Delegated Workspace en juego (origen == actual) no se añade
        // ruido: la propiedad solo aparece cuando difieren.
        ambito.Should().NotContainKey("TenantOrigenId");
    }

    [Fact]
    public async Task Marca_el_Delegated_Workspace_cuando_el_tenant_de_origen_difiere()
    {
        var fabrica = new FabricaLoggerFalsa();
        var tenantOrigen = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var behavior = new LoggingBehavior<FalsoCommand, Result>(
            fabrica, new CurrentUserServiceFalso(UsuarioId, "GestorCae", tenantOrigen), new TenantActualFalso(TenantId),
            new VentanaSaludOperativa(new AlertaOperativaFalsa()));

        await behavior.Handle(new FalsoCommand("12345678Z"), _ => Task.FromResult(Result.Exito()), CancellationToken.None);

        fabrica.Logger.Ambitos.Should().ContainSingle()
            .Which.Should().Contain(new KeyValuePair<string, object?>("TenantOrigenId", tenantOrigen));
    }

    [Fact]
    public async Task Una_query_rapida_no_ensucia_el_log_a_nivel_Information()
    {
        var fabrica = new FabricaLoggerFalsa();
        var behavior = new LoggingBehavior<FalsaQuery, string>(
            fabrica, new CurrentUserServiceFalso(UsuarioId, "Consulta", TenantId), new TenantActualFalso(TenantId),
            new VentanaSaludOperativa(new AlertaOperativaFalsa()));

        await behavior.Handle(new FalsaQuery(), _ => Task.FromResult("ok"), CancellationToken.None);

        fabrica.Logger.Eventos.Should().ContainSingle().Which.Nivel.Should().Be(LogLevel.Debug);
    }

    [Fact]
    public async Task Una_excepcion_se_registra_como_error_y_se_deja_subir()
    {
        var fabrica = new FabricaLoggerFalsa();
        var behavior = Construir<FalsoCommand, Result>(fabrica);

        var acto = async () => await behavior.Handle(
            new FalsoCommand("12345678Z"),
            _ => throw new InvalidOperationException("boom"),
            CancellationToken.None);

        await acto.Should().ThrowAsync<InvalidOperationException>();
        var evento = fabrica.Logger.Eventos.Should().ContainSingle().Subject;
        evento.Nivel.Should().Be(LogLevel.Error);
        evento.Excepcion.Should().BeOfType<InvalidOperationException>();
    }

    [Fact]
    public async Task Pedir_credenciales_sin_2FA_se_registra_como_aviso_con_codigo_y_se_deja_subir()
    {
        // P1-I1: es una denegación esperada, no un fallo del sistema; como
        // error llenaría Sentry cada vez que alguien sin 2FA pulsa «Copiar».
        var fabrica = new FabricaLoggerFalsa();
        var behavior = Construir<FalsaQuery, string>(fabrica);

        var acto = async () => await behavior.Handle(
            new FalsaQuery(),
            _ => throw new SegundoFactorRequeridoParaCredencialesException(),
            CancellationToken.None);

        await acto.Should().ThrowAsync<SegundoFactorRequeridoParaCredencialesException>();
        var evento = fabrica.Logger.Eventos.Should().ContainSingle().Subject;
        evento.Nivel.Should().Be(LogLevel.Warning);
        evento.Excepcion.Should().BeNull();
        evento.Mensaje.Should().Contain("Credenciales.SegundoFactorRequerido");
    }

    [Fact]
    public async Task Un_archivo_rechazado_se_registra_como_aviso_sin_pila_ni_mensaje()
    {
        // ASVS V12.1: el rechazo de un archivo subido es una denegación de entrada; un usuario que sube archivos
        // malos no debe inundar el log ni Sentry con trazas, ni dejar en el log texto que fabricó el archivo.
        var fabrica = new FabricaLoggerFalsa();
        var behavior = Construir<FalsaQuery, string>(fabrica);

        var acto = async () => await behavior.Handle(
            new FalsaQuery(),
            _ => throw new ArchivoRechazadoException("contenido-del-archivo"),
            CancellationToken.None);

        await acto.Should().ThrowAsync<ArchivoRechazadoException>();
        var evento = fabrica.Logger.Eventos.Should().ContainSingle().Subject;
        evento.Nivel.Should().Be(LogLevel.Warning);
        evento.Excepcion.Should().BeNull();
        evento.Mensaje.Should().Contain("Archivo.Rechazado").And.NotContain("contenido-del-archivo");
    }

    [Fact]
    public async Task Una_cancelacion_no_se_registra_como_error()
    {
        // En Blazor Server se cancelan requests continuamente (navegación,
        // debounce reemplazado); tratarlo como error llenaría Sentry de ruido.
        var fabrica = new FabricaLoggerFalsa();
        var behavior = Construir<FalsoCommand, Result>(fabrica);

        var acto = async () => await behavior.Handle(
            new FalsoCommand("12345678Z"),
            _ => throw new OperationCanceledException(),
            CancellationToken.None);

        await acto.Should().ThrowAsync<OperationCanceledException>();
        fabrica.Logger.Eventos.Should().ContainSingle().Which.Nivel.Should().Be(LogLevel.Debug);
    }

    // MJ-06: el histograma de latencia lleva la etiqueta «Resultado» para poder calcular SLI de disponibilidad
    // (exito / fallo de negocio / excepcion) sin números de SLO. Request propio para no mezclar con otros tests.
    private record RequestMedido : ICommand;

    private static async Task<List<(double Valor, string? Resultado)>> MedirAsync(Func<LoggingBehavior<RequestMedido, Result>, Task> acto)
    {
        var medidas = new List<(double, string?)>();
        using var oyente = new System.Diagnostics.Metrics.MeterListener();
        oyente.InstrumentPublished = (instrumento, o) =>
        {
            // Por nombre y no por Observabilidad.DuracionComandoMs: el callback se dispara durante su inicializador estático.
            if (instrumento.Name == "caemanager.comando.duracion_ms") o.EnableMeasurementEvents(instrumento);
        };
        oyente.SetMeasurementEventCallback<double>((_, valor, etiquetas, _) =>
        {
            string? request = null, resultado = null;
            foreach (var etiqueta in etiquetas)
            {
                if (etiqueta.Key == "Request") request = etiqueta.Value as string;
                if (etiqueta.Key == "Resultado") resultado = etiqueta.Value as string;
            }
            if (request == nameof(RequestMedido)) lock (medidas) medidas.Add((valor, resultado));
        });
        oyente.Start();

        try { await acto(Construir<RequestMedido, Result>(new FabricaLoggerFalsa())); }
        catch (Exception) { /* la propagación ya la fijan otros tests; aquí solo se mide */ }
        return medidas;
    }

    [Fact]
    public async Task La_latencia_de_un_exito_lleva_la_etiqueta_exito()
    {
        var medidas = await MedirAsync(b => b.Handle(new RequestMedido(), _ => Task.FromResult(Result.Exito()), CancellationToken.None));

        medidas.Should().ContainSingle().Which.Resultado.Should().Be("exito");
    }

    [Fact]
    public async Task La_latencia_de_un_result_fallido_lleva_la_etiqueta_fallo()
    {
        var error = Error.Crear("Autorizacion.SoloLectura", "Tu rol no permite escribir.");

        var medidas = await MedirAsync(b => b.Handle(new RequestMedido(), _ => Task.FromResult(Result.Fallo(error)), CancellationToken.None));

        medidas.Should().ContainSingle().Which.Resultado.Should().Be("fallo");
    }

    [Fact]
    public async Task La_latencia_de_una_excepcion_se_mide_con_la_etiqueta_excepcion()
    {
        var medidas = await MedirAsync(b => b.Handle(new RequestMedido(), _ => throw new InvalidOperationException("x"), CancellationToken.None));

        medidas.Should().ContainSingle().Which.Resultado.Should().Be("excepcion");
    }

    [Fact]
    public async Task Pedir_credenciales_sin_2FA_se_mide_como_fallo_y_no_como_excepcion()
    {
        var medidas = await MedirAsync(b => b.Handle(
            new RequestMedido(), _ => throw new SegundoFactorRequeridoParaCredencialesException(), CancellationToken.None));

        medidas.Should().ContainSingle().Which.Resultado.Should().Be("fallo");
    }

    [Fact]
    public async Task Un_archivo_rechazado_se_mide_como_fallo_y_no_como_excepcion()
    {
        var medidas = await MedirAsync(b => b.Handle(
            new RequestMedido(), _ => throw new ArchivoRechazadoException("x"), CancellationToken.None));

        medidas.Should().ContainSingle().Which.Resultado.Should().Be("fallo");
    }

    [Fact]
    public async Task Una_cancelacion_no_entra_en_el_histograma_de_latencia()
    {
        var medidas = await MedirAsync(b => b.Handle(new RequestMedido(), _ => throw new OperationCanceledException(), CancellationToken.None));

        medidas.Should().BeEmpty("navegar o reemplazar un debounce no es ni éxito ni fallo del servicio");
    }

    private static LoggingBehavior<TRequest, TResponse> Construir<TRequest, TResponse>(FabricaLoggerFalsa fabrica)
        where TRequest : notnull
        => new(
            fabrica, new CurrentUserServiceFalso(UsuarioId, "GestorCae", TenantId), new TenantActualFalso(TenantId),
            new VentanaSaludOperativa(new AlertaOperativaFalsa()));

    private sealed class TenantActualFalso(Guid? tenantId) : ITenantActual
    {
        public Guid? TenantId => tenantId;
    }

    private sealed record EventoLog(
        LogLevel Nivel, string Mensaje, Exception? Excepcion, IReadOnlyDictionary<string, object?> Propiedades);

    private sealed class FabricaLoggerFalsa : ILoggerFactory
    {
        public LoggerFalso Logger { get; } = new();

        public ILogger CreateLogger(string categoryName) => Logger;
        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }
    }

    private sealed class LoggerFalso : ILogger
    {
        public List<EventoLog> Eventos { get; } = [];
        public List<Dictionary<string, object?>> Ambitos { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull
        {
            if (state is IEnumerable<KeyValuePair<string, object?>> pares)
                Ambitos.Add(new Dictionary<string, object?>(pares));

            return new AmbitoVacio();
        }

        // Debug incluido a propósito: los tests comprueban que una Query
        // rápida se queda en Debug, así que el logger falso no puede filtrar
        // por nivel.
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var propiedades = state is IEnumerable<KeyValuePair<string, object?>> pares
                ? pares.ToDictionary(p => p.Key, p => p.Value)
                : new Dictionary<string, object?>();

            // El mensaje sin formatear ({OriginalFormat}) es el que lleva los
            // nombres de propiedad; el formateado, los valores. Se concatenan
            // para que un solo assert cubra ambos.
            var mensajeFormateado = formatter(state, exception);
            var plantilla = propiedades.TryGetValue("{OriginalFormat}", out var original)
                ? original?.ToString() ?? string.Empty
                : string.Empty;

            Eventos.Add(new EventoLog(logLevel, $"{plantilla} :: {mensajeFormateado}", exception, propiedades));
        }

        private sealed class AmbitoVacio : IDisposable
        {
            public void Dispose() { }
        }
    }
}
