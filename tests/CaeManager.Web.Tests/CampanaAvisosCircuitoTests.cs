using Bunit;
using CaeManager.Application.Common;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Layout;
using CaeManager.Web.Features.Notificaciones;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace CaeManager.Web.Tests;

/// <summary>
/// REC-166 para <c>CampanaAvisos</c>, que desde que se retiró <c>NotificacionesPopup</c> es el único
/// componente de avisos que lee en el montaje: la carrera "el circuito se desconecta con una consulta
/// en vuelo" (ver <see cref="ExcepcionDeCircuitoDesconectado"/>) debe quedar en el log como aviso y
/// nada más, y un fallo real de PostgreSQL no debe desaparecer (llega a Sentry y el islote muestra su
/// aviso compacto).
///
/// <para>
/// <b>Lo que NO observa:</b> un circuito real de Blazor Server — bUnit no emula SignalR.
/// </para>
/// </summary>
public class CampanaAvisosCircuitoTests : BunitContext
{
    private readonly AlertaOperativaQueCuenta _alertas = new();
    private readonly LoggerQueGuarda _logger = new();

    public CampanaAvisosCircuitoTests()
    {
        Services.AddLocalization();
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        Services.AddSingleton<IAlertaOperativa>(_alertas);
        Services.AddSingleton<ToastService>();
        Services.AddSingleton<ILogger<ExcepcionDeCircuitoDesconectado>>(_logger);
    }

    private sealed class AlertaOperativaQueCuenta : IAlertaOperativa
    {
        public List<Exception> Capturadas { get; } = [];
        public void Emitir(string mensaje, NivelAlertaOperativa nivel) { }
        public void CapturarExcepcion(Exception excepcion) => Capturadas.Add(excepcion);
        public void DejarMigaDePan(string mensaje) { }
        public IDisposable IniciarAmbitoDeCaptura() => new Nada();
        private sealed class Nada : IDisposable { public void Dispose() { } }
    }

    private sealed class LoggerQueGuarda : ILogger<ExcepcionDeCircuitoDesconectado>
    {
        public List<(LogLevel Nivel, string Mensaje)> Entradas { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entradas.Add((logLevel, formatter(state, exception)));
    }

    private sealed class MediatorQueFalla(Exception excepcion) : IMediator
    {
        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            throw excepcion;

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest =>
            Task.CompletedTask;

        public Task<object?> Send(object request, CancellationToken cancellationToken = default) =>
            Task.FromResult<object?>(null);

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification => Task.CompletedTask;
    }

    private void Preparar(Exception excepcionDeMediator)
    {
        Services.AddScoped<IMediator>(_ => new MediatorQueFalla(excepcionDeMediator));
        // Después de registrar: SetRendererInfo ya resuelve servicios. El aviso del límite lo lee.
        SetRendererInfo(new Microsoft.AspNetCore.Components.RendererInfo("Server", isInteractive: true));
    }

    [Fact]
    public void Desconexion_de_circuito_via_NpgsqlException_cruda_no_escala_y_queda_registrada()
    {
        // Firma real medida en CI (REC-166, run 35158419682, 2026-09-16), sin InnerException.
        Preparar(new NpgsqlException("Received backend message BindComplete while expecting ParseCompleteMessage. Please file a bug."));

        var render = () => Render<CampanaAvisos>();

        render.Should().NotThrow("la carrera de desconexión no debe tumbar el circuito");
        _logger.Entradas.Should().ContainSingle(e => e.Nivel == LogLevel.Warning && e.Mensaje.Contains("NpgsqlException"));
        _alertas.Capturadas.Should().BeEmpty("la carrera no es un fallo que alertar: nadie espera ya la campana");
    }

    [Fact]
    public void Fallo_real_de_PostgreSQL_con_el_circuito_vivo_sigue_saliendo()
    {
        var excepcion = new PostgresException("duplicate key value violates unique constraint", "ERROR", "ERROR", "23505");
        Preparar(excepcion);

        var cut = Render<CampanaAvisos>();

        _alertas.Capturadas.Should().ContainSingle().Which.Should().BeSameAs(excepcion,
            "un error real de PostgreSQL no se traga: llega a Sentry como error");
        _logger.Entradas.Should().BeEmpty("no se confunde con la carrera de desconexión");
        cut.Find("[data-limite-errores-compacto]");
    }

    [Fact]
    public void Fallo_real_de_red_transitorio_con_el_circuito_vivo_sigue_saliendo()
    {
        var excepcion = new NpgsqlException("Exception while reading from stream", new IOException("Connection reset by peer"));
        Preparar(excepcion);

        var cut = Render<CampanaAvisos>();

        _alertas.Capturadas.Should().ContainSingle().Which.Should().BeSameAs(excepcion);
        _logger.Entradas.Should().BeEmpty("una base inalcanzable no es la carrera de desconexión de circuito");
        cut.Find("[data-limite-errores-compacto]");
    }
}
