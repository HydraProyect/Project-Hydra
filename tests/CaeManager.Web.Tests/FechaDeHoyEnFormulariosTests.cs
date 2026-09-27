using Bunit;
using CaeManager.Application.Blindaje42.Queries.ObtenerHistorialCertificacionesTgss;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Blindaje42.Components;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// La fecha que un formulario propone como «hoy» tiene que ser la misma con la que la
/// regla «no puede ser futura» la juzga después: el día UTC. Entre las 00:00 y las
/// 02:00 de Madrid (horario de verano) los dos días difieren, y un formulario que
/// proponía el día local hacía fallar el guardado sin que el usuario tocara la fecha
/// (<c>Bandeja</c> al actualizar un documento desde un adjunto, <c>DrawerBlindaje42</c>
/// al registrar solicitud o respuesta).
///
/// El reloj falso fija el instante 2026-09-27 22:30 UTC con zona local Europe/Madrid:
/// en Madrid ya es 28 de septiembre, 00:30; en UTC sigue siendo el 27.
/// </summary>
public class FechaDeHoyEnFormulariosTests : BunitContext
{
    private static readonly DateTime MediaHoraTrasLaMedianocheDeMadrid = new(2026, 9, 27, 22, 30, 0, DateTimeKind.Utc);

    private sealed class RelojManual(DateTime ahoraUtc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(ahoraUtc, TimeSpan.Zero);

        public override TimeZoneInfo LocalTimeZone { get; } = TimeZoneInfo.FindSystemTimeZoneById("Europe/Madrid");
    }

    private sealed class MediatorSinHistorial : IMediator
    {
        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            Task.FromResult((TResponse)(request switch
            {
                ObtenerHistorialCertificacionesTgssQuery => (object)new List<SolicitudCertificacionTgssDto>(),
                _ => throw new NotSupportedException($"Petición no prevista en este test: {request.GetType().Name}.")
            }));

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

    [Fact]
    public void El_reloj_falso_esta_de_verdad_en_la_franja_donde_Madrid_y_UTC_estan_en_dias_distintos()
    {
        // Control del instrumento: si la zona no fuera Madrid, o el instante cayera fuera
        // de la franja, los casos de abajo pasarían sin observar nada.
        var reloj = new RelojManual(MediaHoraTrasLaMedianocheDeMadrid);

        DateOnly.FromDateTime(reloj.GetLocalNow().DateTime).Should().Be(new DateOnly(2026, 9, 28));
        DateOnly.FromDateTime(reloj.GetUtcNow().UtcDateTime).Should().Be(new DateOnly(2026, 9, 27));
    }

    [Fact]
    public void Pasada_la_medianoche_de_Madrid_el_formulario_propone_el_dia_UTC_con_el_que_se_juzga_si_es_futura()
    {
        FechaDeHoy.ParaCampoFecha(new RelojManual(MediaHoraTrasLaMedianocheDeMadrid))
            .Should().Be("2026-09-27", "el 28 todavía es un día futuro para la regla, que compara con el día UTC");
    }

    [Fact]
    public void A_la_medianoche_UTC_el_formulario_ya_propone_el_dia_siguiente()
    {
        FechaDeHoy.ParaCampoFecha(new RelojManual(new DateTime(2026, 9, 27, 23, 59, 59, DateTimeKind.Utc)))
            .Should().Be("2026-09-27");
        FechaDeHoy.ParaCampoFecha(new RelojManual(new DateTime(2026, 9, 28, 0, 0, 0, DateTimeKind.Utc)))
            .Should().Be("2026-09-28");
    }

    [Fact]
    public void El_drawer_del_blindaje_42_propone_como_fecha_de_solicitud_el_dia_UTC_pasada_la_medianoche_de_Madrid()
    {
        this.ConRolDeEscritura(Roles.GestorCae);
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddScoped<ToastService>();
        Services.AddSingleton<IMediator>(new MediatorSinHistorial());
        Services.AddSingleton<TimeProvider>(new RelojManual(MediaHoraTrasLaMedianocheDeMadrid));

        var cut = Render<DrawerBlindaje42>(p => p
            .Add(x => x.Visible, true)
            .Add(x => x.ClienteId, Guid.NewGuid())
            .Add(x => x.EmpresaId, Guid.NewGuid()));

        cut.FindAll("input[type=date]").Select(i => i.GetAttribute("value"))
            .Should().Contain("2026-09-27").And.NotContain("2026-09-28");
    }
}
