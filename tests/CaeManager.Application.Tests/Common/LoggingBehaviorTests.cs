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
        // RGPD-TRATAMIENTO-DATOS.md, con su retención y sus derechos.
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
            fabrica, new CurrentUserServiceFalso(UsuarioId, "GestorCae", tenantOrigen), new TenantActualFalso(TenantId));

        await behavior.Handle(new FalsoCommand("12345678Z"), _ => Task.FromResult(Result.Exito()), CancellationToken.None);

        fabrica.Logger.Ambitos.Should().ContainSingle()
            .Which.Should().Contain(new KeyValuePair<string, object?>("TenantOrigenId", tenantOrigen));
    }

    [Fact]
    public async Task Una_query_rapida_no_ensucia_el_log_a_nivel_Information()
    {
        var fabrica = new FabricaLoggerFalsa();
        var behavior = new LoggingBehavior<FalsaQuery, string>(
            fabrica, new CurrentUserServiceFalso(UsuarioId, "Consulta", TenantId), new TenantActualFalso(TenantId));

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

    private static LoggingBehavior<TRequest, TResponse> Construir<TRequest, TResponse>(FabricaLoggerFalsa fabrica)
        where TRequest : notnull
        => new(fabrica, new CurrentUserServiceFalso(UsuarioId, "GestorCae", TenantId), new TenantActualFalso(TenantId));

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
