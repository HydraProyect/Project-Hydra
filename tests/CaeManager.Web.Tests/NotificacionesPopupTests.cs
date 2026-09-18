using Bunit;
using CaeManager.Application.Notificaciones.Commands.MarcarNotificacionLeida;
using CaeManager.Application.Notificaciones.Queries.ObtenerNotificacionesPendientes;
using CaeManager.Web.Components.Layout;
using CaeManager.Web.Features.Notificaciones;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace CaeManager.Web.Tests;

/// <summary>
/// REC-166: <c>NotificacionesPopup</c> es uno de los cinco componentes de
/// layout que reciben la carrera "el circuito se desconecta con una consulta
/// en vuelo" (ver <see cref="CaeManager.Web.Components.Layout.ExcepcionDeCircuitoDesconectado"/>).
/// PR #517 ya lo cubrió para <see cref="ObjectDisposedException"/> y
/// <see cref="ArgumentOutOfRangeException"/>; esta suite cubre la variante
/// que se le escapó (medida en vivo el 2026-09-16, run 35158419682) y el
/// contrario obligatorio: un fallo real de PostgreSQL no debe desaparecer.
///
/// <para>
/// <b>Lo que SÍ observa:</b> si <c>OnInitializedAsync</c> deja escapar la
/// excepción (que en un circuito real tumba el componente entero — "Unhandled
/// exception in circuit") y si queda registrada.
/// </para>
/// <para>
/// <b>Lo que NO observa:</b> que un circuito real de Blazor Server se
/// comporte igual — bUnit no emula SignalR ni el ciclo de vida de un
/// circuito, solo el árbol de render en proceso.
/// </para>
/// </summary>
public class NotificacionesPopupTests : BunitContext
{
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

    private sealed class LoggerQueGuarda : ILogger<ExcepcionDeCircuitoDesconectado>
    {
        public List<(LogLevel Nivel, string Mensaje)> Entradas { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entradas.Add((logLevel, formatter(state, exception)));
    }

    private LoggerQueGuarda Preparar(Exception excepcionDeMediator)
    {
        var logger = new LoggerQueGuarda();
        Services.AddScoped<IMediator>(_ => new MediatorQueFalla(excepcionDeMediator));
        Services.AddSingleton<ILogger<ExcepcionDeCircuitoDesconectado>>(logger);
        return logger;
    }

    [Fact]
    public void Desconexion_de_circuito_via_NpgsqlException_cruda_no_escala_y_queda_registrada()
    {
        // Firma real medida en CI (REC-166, run 35158419682, 2026-09-16):
        // sin InnerException, tal cual Npgsql.Util.Statics.ThrowIfMsgWrongType
        // la construye.
        var excepcion = new NpgsqlException("Received backend message BindComplete while expecting ParseCompleteMessage. Please file a bug.");
        var logger = Preparar(excepcion);

        var render = () => Render<NotificacionesPopup>();

        render.Should().NotThrow("la carrera de desconexión no debe tumbar el circuito");
        logger.Entradas.Should().ContainSingle(e => e.Nivel == LogLevel.Warning && e.Mensaje.Contains("NpgsqlException"));
    }

    [Fact]
    public void Fallo_real_de_PostgreSQL_con_el_circuito_vivo_sigue_saliendo()
    {
        // El contrario obligatorio: un PostgresException es un error que el
        // servidor SÍ llegó a reportar (aquí, una violación de constraint) —
        // tragarlo sería peor que el ruido de la carrera.
        var excepcion = new PostgresException("duplicate key value violates unique constraint", "ERROR", "ERROR", "23505");
        Preparar(excepcion);

        var render = () => Render<NotificacionesPopup>();

        render.Should().Throw<PostgresException>("un error real de PostgreSQL no se traga");
    }

    [Fact]
    public void Fallo_real_de_red_transitorio_con_el_circuito_vivo_sigue_saliendo()
    {
        // Segundo contrario: la base inalcanzable (IsTransient == true, por
        // llevar un IOException dentro) tampoco es esta carrera.
        var excepcion = new NpgsqlException("Exception while reading from stream", new IOException("Connection reset by peer"));
        Preparar(excepcion);

        var render = () => Render<NotificacionesPopup>();

        render.Should().Throw<NpgsqlException>("una base inalcanzable no es la carrera de desconexión de circuito")
            .Which.InnerException.Should().BeOfType<IOException>();
    }
}
