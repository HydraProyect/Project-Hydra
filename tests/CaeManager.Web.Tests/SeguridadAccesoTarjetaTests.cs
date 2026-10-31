using Bunit;
using CaeManager.Application.Configuracion.Commands.ActualizarSeguridadAcceso;
using CaeManager.Application.Configuracion.Queries;
using CaeManager.Domain.Common;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Configuracion.Components;
using FluentAssertions;
using FluentValidation;
using FluentValidation.Results;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>Tarjeta «Seguridad de acceso» de <c>/configuracion</c>: la pantalla no autoriza, solo ofrece.</summary>
public class SeguridadAccesoTarjetaTests : BunitContext
{
    public SeguridadAccesoTarjetaTests() => Services.AddLocalization();

    private MediatorFalso Montar(SeguridadAccesoDto? ajuste, Exception? alLeer = null)
    {
        var mediator = new MediatorFalso { Ajuste = ajuste, AlLeer = alLeer };
        Services.AddScoped<IMediator>(_ => mediator);
        Services.AddScoped<ToastService>();
        return mediator;
    }

    [Fact]
    public void Quien_no_puede_editar_no_ve_la_tarjeta()
    {
        Montar(new SeguridadAccesoDto(30, false, PuedeEditar: false));

        Render<SeguridadAccesoTarjeta>().FindAll("[data-testid=seguridad-acceso]").Should().BeEmpty();
    }

    [Fact]
    public void Si_la_lectura_falla_la_tarjeta_no_se_pinta_ni_rompe_la_pantalla()
    {
        Montar(null, new InvalidOperationException());

        Render<SeguridadAccesoTarjeta>().FindAll("[data-testid=seguridad-acceso]").Should().BeEmpty();
    }

    /// <summary>
    /// M-06: sin aviso, la tarjeta ausente por un fallo de lectura era indistinguible de «no tienes
    /// permiso». El usuario debe enterarse de que no se pudo cargar.
    /// </summary>
    [Fact]
    public void Si_la_lectura_falla_avisa_al_usuario_de_que_no_se_pudo_cargar()
    {
        Montar(null, new InvalidOperationException());

        Render<SeguridadAccesoTarjeta>();

        Services.GetRequiredService<ToastService>().Mensajes.Should().ContainSingle()
            .Which.Tono.Should().Be(TonoToast.Error);
    }

    [Fact]
    public void Quien_no_puede_editar_no_recibe_ningun_aviso()
    {
        Montar(new SeguridadAccesoDto(30, false, PuedeEditar: false));

        Render<SeguridadAccesoTarjeta>();

        Services.GetRequiredService<ToastService>().Mensajes.Should().BeEmpty();
    }

    [Fact]
    public void Muestra_el_plazo_y_el_interruptor_con_sus_atributos_de_accesibilidad()
    {
        Montar(new SeguridadAccesoDto(45, false, PuedeEditar: true));

        var tarjeta = Render<SeguridadAccesoTarjeta>();

        tarjeta.Find("#seg-minutos").GetAttribute("value").Should().Be("45");
        var interruptor = tarjeta.Find("[role=switch]");
        interruptor.GetAttribute("aria-checked").Should().Be("false");
        interruptor.GetAttribute("aria-labelledby").Should().Be("seg-dos-pasos");
        tarjeta.FindAll("#seg-dos-pasos-aviso").Should().BeEmpty();
    }

    [Fact]
    public void Al_activar_la_exigencia_avisa_de_las_cuentas_sin_verificacion()
    {
        Montar(new SeguridadAccesoDto(30, false, PuedeEditar: true));
        var tarjeta = Render<SeguridadAccesoTarjeta>();

        tarjeta.Find("[role=switch]").Click();

        tarjeta.Find("[role=switch]").GetAttribute("aria-checked").Should().Be("true");
        tarjeta.Find("#seg-dos-pasos-aviso").GetAttribute("role").Should().Be("status");
    }

    [Fact]
    public void Guardar_envia_el_plazo_y_la_exigencia_escritos()
    {
        var mediator = Montar(new SeguridadAccesoDto(30, false, PuedeEditar: true));
        var tarjeta = Render<SeguridadAccesoTarjeta>();

        tarjeta.Find("#seg-minutos").Change("45");
        tarjeta.Find("[role=switch]").Click();
        tarjeta.Find(".seguridad-acciones button").Click();

        mediator.Comandos.Should().ContainSingle().Which.Should().Be(new ActualizarSeguridadAccesoCommand(45, true));
    }

    [Fact]
    public void Un_valor_vacio_no_se_toma_por_el_anterior()
    {
        var mediator = Montar(new SeguridadAccesoDto(30, false, PuedeEditar: true));
        var tarjeta = Render<SeguridadAccesoTarjeta>();

        tarjeta.Find("#seg-minutos").Change("");
        tarjeta.Find(".seguridad-acciones button").Click();

        mediator.Comandos.Should().ContainSingle().Which.Should().Be(new ActualizarSeguridadAccesoCommand(0, false));
    }

    [Fact]
    public void Un_error_de_validacion_marca_el_campo()
    {
        var mediator = Montar(new SeguridadAccesoDto(30, false, PuedeEditar: true));
        mediator.AlGuardar = _ => throw new ValidationException(
            [new ValidationFailure("MinutosInactividadSesion", "Introduce un valor entre 5 y 480 minutos.")]);
        var tarjeta = Render<SeguridadAccesoTarjeta>();

        tarjeta.Find(".seguridad-acciones button").Click();

        tarjeta.Find("#seg-minutos").GetAttribute("aria-invalid").Should().Be("true");
        tarjeta.Find("#seg-minutos").GetAttribute("aria-describedby").Should().Be("seg-minutos-error seg-minutos-ayuda");
        tarjeta.Find("#seg-minutos-error").TextContent.Should().Contain("Introduce un valor entre 5 y 480 minutos.");
    }

    [Fact]
    public void Un_rechazo_del_comando_se_muestra_como_aviso()
    {
        var mediator = Montar(new SeguridadAccesoDto(30, false, PuedeEditar: true));
        mediator.AlGuardar = _ => Result.Fallo(Error.Crear("X", "Solo el Administrador."));
        var tarjeta = Render<SeguridadAccesoTarjeta>();

        tarjeta.Find(".seguridad-acciones button").Click();

        tarjeta.Find("[role=alert]").TextContent.Should().Contain("Solo el Administrador.");
    }

    private sealed class MediatorFalso : IMediator
    {
        public SeguridadAccesoDto? Ajuste { get; init; }
        public Exception? AlLeer { get; init; }
        public Func<ActualizarSeguridadAccesoCommand, Result> AlGuardar { get; set; } = _ => Result.Exito();
        public List<ActualizarSeguridadAccesoCommand> Comandos { get; } = [];

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            switch (request)
            {
                case ObtenerSeguridadAccesoQuery:
                    if (AlLeer is not null) throw AlLeer;
                    return Task.FromResult((TResponse)(object)Ajuste!);
                case ActualizarSeguridadAccesoCommand comando:
                    Comandos.Add(comando);
                    return Task.FromResult((TResponse)(object)AlGuardar(comando));
                default:
                    throw new NotSupportedException(request.GetType().Name);
            }
        }

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest =>
            throw new NotSupportedException();
        public Task<object?> Send(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification => Task.CompletedTask;
    }
}
