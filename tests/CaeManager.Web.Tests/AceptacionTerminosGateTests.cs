using Bunit;
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
    public AceptacionTerminosGateTests()
    {
        Services.AddLocalization();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    /// <summary>El usuario tiene que aceptar; aceptar falla con una excepción inesperada.</summary>
    private sealed class MediatorConGatePendiente : IMediator
    {
        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            request switch
            {
                ObtenerEstadoAceptacionTerminosQuery => Task.FromResult((TResponse)(object)true),
                AceptarTerminosCommand => throw new InvalidOperationException("fallo simulado"),
                _ => throw new NotSupportedException(request.GetType().Name),
            };

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

    [Fact]
    public void El_gate_muestra_la_formula_de_consentimiento_de_los_recursos()
    {
        Services.AddScoped<IMediator, MediatorConGatePendiente>();

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
    public void Si_aceptar_falla_muestra_el_error_de_los_recursos()
    {
        Services.AddScoped<IMediator, MediatorConGatePendiente>();

        var gate = Render<AceptacionTerminosGate>();
        gate.Find("label.campo-checkbox input[type=checkbox]").Change(true);
        gate.FindAll("button").Single(b => b.TextContent.Trim() == "Aceptar y continuar").Click();

        gate.Find(".alerta-formulario").TextContent.Should().Be("No pudimos registrar tu aceptación. Intenta nuevamente.");
    }
}
