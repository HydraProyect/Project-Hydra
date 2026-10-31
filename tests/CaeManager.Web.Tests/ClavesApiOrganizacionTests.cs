using Bunit;
using CaeManager.Application.ApiKeys.Commands.RevocarClaveApiDeMiOrganizacion;
using CaeManager.Application.ApiKeys.Queries.ObtenerClavesApi;
using CaeManager.Application.ApiKeys.Queries.ObtenerClavesApiDeMiOrganizacion;
using CaeManager.Domain.Common;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.ApiKeys.Pages;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

public class ClavesApiOrganizacionTests : BunitContext
{
    public ClavesApiOrganizacionTests() => JSInterop.Mode = JSRuntimeMode.Loose;

    private sealed class Mediador : IMediator
    {
        public List<object> Enviadas { get; } = [];
        public IReadOnlyList<ClaveApiDto> Claves { get; set; } = [];
        public string? FallaRevocarCon { get; set; }
        public Task<T> Send<T>(IRequest<T> request, CancellationToken cancellationToken = default)
        {
            Enviadas.Add(request);
            object respuesta = request switch
            {
                ObtenerClavesApiDeMiOrganizacionQuery => Claves,
                RevocarClaveApiDeMiOrganizacionCommand when FallaRevocarCon is { } motivo => Result.Fallo(Error.Crear("ClaveApi.Rechazada", motivo)),
                RevocarClaveApiDeMiOrganizacionCommand => Result.Exito(),
                _ => throw new NotSupportedException(),
            };
            return Task.FromResult((T)respuesta);
        }
        public Task Send<T>(T request, CancellationToken cancellationToken = default) where T : IRequest => Task.CompletedTask;
        public Task<object?> Send(object request, CancellationToken cancellationToken = default) => Task.FromResult<object?>(null);
        public IAsyncEnumerable<T> CreateStream<T>(IStreamRequest<T> request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task Publish<T>(T notification, CancellationToken cancellationToken = default) where T : INotification => Task.CompletedTask;
    }

    private static ClaveApiDto Clave(string nombre, bool estaActiva = true, bool cortada = false, int caducaEnDias = 30) =>
        new(Guid.NewGuid(), nombre, "tlv_xxxxxx", DateTime.UtcNow.AddDays(-1), null, estaActiva, DateTime.UtcNow.AddDays(caducaEnDias), cortada);

    private (IRenderedComponent<ClavesApiOrganizacion> Cut, Mediador Mediador) Renderizar(params ClaveApiDto[] claves)
    {
        var mediator = new Mediador { Claves = claves };
        Services.AddScoped<IMediator>(_ => mediator);
        Services.AddScoped<ToastService>();
        Services.AddLocalization();
        return (Render<ClavesApiOrganizacion>(), mediator);
    }

    [Fact]
    public void Muestra_el_estado_real_y_solo_ofrece_revocar_lo_que_no_esta_revocado()
    {
        var (cut, _) = Renderizar(
            Clave("ERP activa"), Clave("ERP cortada", cortada: true), Clave("ERP caducada", caducaEnDias: -1), Clave("ERP revocada", estaActiva: false));

        string Fila(string nombre) => cut.FindAll("tbody tr").Single(f => f.TextContent.Contains(nombre)).TextContent;

        cut.FindAll("tbody tr").Should().HaveCount(4, "control positivo: el instrumento observa las cuatro claves");
        Fila("ERP activa").Should().Contain("Activa").And.Contain("Revocar");
        Fila("ERP cortada").Should().Contain("Sin efecto").And.Contain("Revocar", "una clave cortada puede volver a valer y hay que poder retirarla");
        Fila("ERP caducada").Should().Contain("Caducada");
        Fila("ERP revocada").Should().Contain("Revocada").And.NotContain("Revocar");
    }

    [Fact]
    public void Sin_claves_muestra_el_estado_vacio()
    {
        var (cut, mediator) = Renderizar();

        mediator.Enviadas.Should().ContainSingle().Which.Should().BeOfType<ObtenerClavesApiDeMiOrganizacionQuery>();
        cut.FindAll("tbody tr").Should().BeEmpty();
        cut.FindComponents<EstadoVacio>().Should().ContainSingle();
    }

    [Fact]
    public async Task Revocar_pide_confirmacion_envia_el_comando_y_recarga()
    {
        var clave = Clave("ERP activa");
        var (cut, mediator) = Renderizar(clave);

        await cut.FindAll("tbody button").Single().ClickAsync(new MouseEventArgs());
        mediator.Enviadas.OfType<RevocarClaveApiDeMiOrganizacionCommand>().Should().BeEmpty("sin confirmar no se revoca");
        await cut.FindComponent<DialogoConfirmacion>().FindAll("button").Single(b => b.TextContent.Trim() == "Revocar").ClickAsync(new MouseEventArgs());

        mediator.Enviadas.OfType<RevocarClaveApiDeMiOrganizacionCommand>().Should().ContainSingle().Which.ClaveApiId.Should().Be(clave.Id);
        mediator.Enviadas.OfType<ObtenerClavesApiDeMiOrganizacionQuery>().Should().HaveCount(2);
    }
}
