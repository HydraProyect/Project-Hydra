using AngleSharp.Dom;
using Bunit;
using Bunit.TestDoubles;
using System.Globalization;
using CaeManager.Application.Common;
using CaeManager.Application.Cumplimiento.Commands.AceptarTerminos;
using CaeManager.Application.Cumplimiento.Queries.ObtenerEstadoAceptacionTerminos;
using CaeManager.Web.Features.Cumplimiento;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// Los textos del gate de aceptación de términos salen de
/// <c>TextosCumplimiento</c>. Son texto legal: la fórmula de consentimiento se
/// compara entera, con la marca interpolada. Las claves no coinciden con su
/// texto a propósito: si el localizador no encontrara el recurso devolvería el
/// nombre de la clave («CasillaAceptacion»), y estos tests lo verían.
/// </summary>
public class AceptacionTerminosGateTests : BunitContext
{
    private readonly BunitPersistentComponentState _estadoPersistente;

    public AceptacionTerminosGateTests()
    {
        _estadoPersistente = this.AddBunitPersistentComponentState();
        Services.AddLocalization();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    /// <summary>El usuario tiene que aceptar; aceptar falla con una excepción inesperada.</summary>
    private sealed class MediatorConGatePendiente : IMediator
    {
        public int Consultas { get; private set; }

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            if (request is ObtenerEstadoAceptacionTerminosQuery) Consultas++;
            return request switch
            {
                ObtenerEstadoAceptacionTerminosQuery => Task.FromResult((TResponse)(object)true),
                AceptarTerminosCommand => throw new InvalidOperationException("fallo simulado"),
                _ => throw new NotSupportedException(request.GetType().Name),
            };
        }

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest =>
            throw new NotSupportedException();

        public Task<object?> Send(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification => Task.CompletedTask;
    }

    private sealed class UsuarioFalso : ICurrentUserService
    {
        public Guid Id { get; set; } = Guid.Parse("dddddddd-0000-0000-0000-000000000001");
        public Task<Guid?> ObtenerUsuarioActualIdAsync() => Task.FromResult<Guid?>(Id);
        public Task<string?> ObtenerRolOrigenAsync() => ObtenerRolEfectivoAsync();
        public Task<string?> ObtenerRolEfectivoAsync() => Task.FromResult<string?>("Administrador");
        public Task<Guid?> ObtenerTenantOrigenIdAsync() => Task.FromResult<Guid?>(Guid.Parse("dddddddd-0000-0000-0000-0000000000aa"));
        public Task<bool> TieneDobleFactorActivoAsync() => Task.FromResult(true);
    }

    private readonly UsuarioFalso _usuario = new();

    private MediatorConGatePendiente Preparar()
    {
        var mediador = new MediatorConGatePendiente();
        Services.AddSingleton<IMediator>(mediador);
        Services.AddSingleton<ICurrentUserService>(_usuario);
        return mediador;
    }

    [Fact]
    public void El_prerenderizado_pinta_el_modal_y_el_circuito_lo_reutiliza_sin_consultar_otra_vez()
    {
        var estado = _estadoPersistente;
        var mediador = Preparar();
        var prerender = Render<AceptacionTerminosGate>();
        prerender.Find(".texto-consentimiento");
        estado.TriggerOnPersisting();
        prerender.Dispose();

        var circuito = Render<AceptacionTerminosGate>();

        mediador.Consultas.Should().Be(1, "el circuito reutiliza lo que leyó el prerenderizado del mismo usuario");
        circuito.Find(".texto-consentimiento");
    }

    [Theory]
    [InlineData("vigente")]
    [InlineData("caducado")]
    [InlineData("otro-usuario")]
    [InlineData("otra-version")]
    public void Solo_se_reutiliza_el_estado_vigente_del_mismo_usuario_y_version(string escenario)
    {
        var mediador = Preparar();
        _estadoPersistente.Persist("gate-terminos-estado-v1", new AceptacionTerminosGate.EstadoGate(
            escenario == "otro-usuario" ? Guid.NewGuid() : _usuario.Id,
            escenario == "otra-version" ? "2000-01-01" : CaeManager.Domain.Cumplimiento.VersionTerminos.Actual,
            DateTimeOffset.UtcNow - (escenario == "caducado" ? TimeSpan.FromMinutes(1) : TimeSpan.Zero),
            DebeAceptar: false));

        var gate = Render<AceptacionTerminosGate>();

        if (escenario == "vigente")
        {
            mediador.Consultas.Should().Be(0, "el circuito reutiliza el «no hace falta aceptar» del prerenderizado");
            gate.FindAll(".texto-consentimiento").Should().BeEmpty();
        }
        else
        {
            mediador.Consultas.Should().Be(1);
            gate.Find(".texto-consentimiento");
        }
    }

    [Fact]
    public void El_gate_muestra_la_formula_de_consentimiento_de_los_recursos()
    {
        Preparar();

        var gate = Render<AceptacionTerminosGate>();

        gate.Find(".texto-consentimiento").TextContent.Should().Be(
            $"Antes de continuar, confirma que has leído y aceptas los Términos y Condiciones de uso y la Política de Privacidad de {Marca.Nombre} — incluida la responsabilidad de tu organización sobre la gestión y el tratamiento de los datos de sus clientes y trabajadores que introduzcas en la plataforma.");
        gate.Find("a[href='/legal/terminos']").TextContent.Should().Be("Leer Términos y Condiciones completos ↗");
        gate.Find("a[href='/legal/privacidad']").TextContent.Should().Be("Leer Política de Privacidad completa ↗");
        gate.Find("label.campo-checkbox").TextContent.Trim().Should().Be(
            "He leído y acepto los Términos y Condiciones y la Política de Privacidad.");
        gate.Find("h2").TextContent.Should().Be("Términos y Condiciones");
        gate.FindAll("button").Select(b => b.TextContent.Trim()).Should().Contain("Aceptar y continuar");
    }

    [Fact]
    public void El_gate_marcado_sigue_bloqueado_ante_los_intentos_de_cierre()
    {
        Preparar();
        var gate = Render<AceptacionTerminosGate>();
        gate.Find("label.campo-checkbox input").Change(true);
        gate.FindAll(".modal-cerrar").Should().BeEmpty();
        gate.Find(".modal-superposicion").Click();
        gate.Find("[role=dialog]").KeyDown("Escape");
        gate.FindAll("[role=dialog]").Should().ContainSingle();
        gate.Find("label.campo-checkbox input").HasAttribute("checked").Should().BeTrue();
    }

    [Fact]
    public void Si_aceptar_falla_muestra_el_error_de_los_recursos()
    {
        Preparar();

        var gate = Render<AceptacionTerminosGate>();
        IElement BotonAceptar() => gate.FindAll("button").Single(b => b.TextContent.Trim() == "Aceptar y continuar");
        BotonAceptar().HasAttribute("disabled").Should().BeTrue("sin marcar la casilla no se puede aceptar");

        gate.Find("label.campo-checkbox input[type=checkbox]").Change(true);
        BotonAceptar().HasAttribute("disabled").Should().BeFalse();
        BotonAceptar().Click();

        gate.Find(".alerta-formulario").TextContent.Should().Be("No pudimos registrar tu aceptación. Intenta nuevamente.");
    }

    [Fact]
    public void En_ca_ES_la_formula_de_consentimiento_esta_fijada()
    {
        // Fija el texto legal que ve un usuario en catalán, leído de
        // TextosCumplimiento.ca-ES.resx: editar solo ese recurso pone este
        // test en rojo. Hoy coincide con el español por decisión (ca-ES
        // idéntico al empezar). Cuando se traduzca, cambiar este texto es la
        // decisión legal a revisar, no un ajuste del test.
        var (previa, previaUi) = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
        CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("ca-ES");
        try
        {
            Preparar();

            var gate = Render<AceptacionTerminosGate>();

            gate.Find(".texto-consentimiento").TextContent.Should().Be(
                $"Antes de continuar, confirma que has leído y aceptas los Términos y Condiciones de uso y la Política de Privacidad de {Marca.Nombre} — incluida la responsabilidad de tu organización sobre la gestión y el tratamiento de los datos de sus clientes y trabajadores que introduzcas en la plataforma.");
            gate.Find("label.campo-checkbox").TextContent.Trim().Should().Be(
                "He leído y acepto los Términos y Condiciones y la Política de Privacidad.");
        }
        finally
        {
            (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture) = (previa, previaUi);
        }
    }
}
