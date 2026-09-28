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
/// La fecha que un formulario propone como «hoy» es el día de negocio de
/// <c>DiaDeNegocio</c> (Europe/Madrid, decisión 2026-09-28), el mismo con el que la
/// regla «no puede ser futura» la juzga después. Entre las 00:00 y las 02:00 de Madrid
/// (horario de verano) el día UTC todavía es el anterior; un formulario que propusiera
/// el día UTC ofrecería ayer como «hoy» (<c>Bandeja</c> al actualizar un documento
/// desde un adjunto, <c>DrawerBlindaje42</c> al registrar solicitud o respuesta).
///
/// El reloj falso fija el instante 2026-09-27 22:30 UTC: en Madrid ya es 28 de
/// septiembre, 00:30; en UTC sigue siendo el 27. Su <c>LocalTimeZone</c> es UTC, como
/// la de los contenedores: el día no puede salir de la zona del servidor.
/// </summary>
public class FechaDeHoyEnFormulariosTests : BunitContext
{
    private static readonly DateTime MediaHoraTrasLaMedianocheDeMadrid = new(2026, 9, 27, 22, 30, 0, DateTimeKind.Utc);

    private sealed class RelojManual(DateTime ahoraUtc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(ahoraUtc, TimeSpan.Zero);

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
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

        TimeZoneInfo.ConvertTime(reloj.GetUtcNow(), TimeZoneInfo.FindSystemTimeZoneById("Europe/Madrid")).Day.Should().Be(28);
        reloj.GetUtcNow().Day.Should().Be(27);
        DateOnly.FromDateTime(reloj.GetLocalNow().DateTime).Should().Be(new DateOnly(2026, 9, 27),
            "la zona local del reloj es UTC, como en producción: si el día saliera de ella, este test lo vería");
    }

    [Fact]
    public void Pasada_la_medianoche_de_Madrid_el_formulario_propone_el_dia_de_Madrid()
    {
        FechaDeHoy.ParaCampoFecha(new RelojManual(MediaHoraTrasLaMedianocheDeMadrid))
            .Should().Be("2026-09-28", "en Madrid ya es 28; el 27 del día UTC sería ayer");
    }

    [Fact]
    public void El_dia_cambia_a_la_medianoche_de_Madrid_no_a_la_medianoche_UTC()
    {
        // Verano (UTC+2): la medianoche de Madrid es a las 22:00 UTC.
        FechaDeHoy.ParaCampoFecha(new RelojManual(new DateTime(2026, 9, 27, 21, 59, 59, DateTimeKind.Utc)))
            .Should().Be("2026-09-27");
        FechaDeHoy.ParaCampoFecha(new RelojManual(new DateTime(2026, 9, 27, 22, 0, 0, DateTimeKind.Utc)))
            .Should().Be("2026-09-28");
        // Invierno (UTC+1): a las 23:00 UTC.
        FechaDeHoy.ParaCampoFecha(new RelojManual(new DateTime(2026, 1, 14, 22, 59, 59, DateTimeKind.Utc)))
            .Should().Be("2026-01-14");
        FechaDeHoy.ParaCampoFecha(new RelojManual(new DateTime(2026, 1, 14, 23, 30, 0, DateTimeKind.Utc)))
            .Should().Be("2026-01-15");
    }

    [Fact]
    public void El_drawer_del_blindaje_42_propone_como_fecha_de_solicitud_el_dia_de_Madrid_pasada_su_medianoche()
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
            .Should().Contain("2026-09-28").And.NotContain("2026-09-27");
    }
}
