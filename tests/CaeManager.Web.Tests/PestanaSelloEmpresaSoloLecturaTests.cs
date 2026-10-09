using Bunit;
using CaeManager.Application.Documentos.Queries.ObtenerSelloEmpresa;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Empresas.Components;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CaeManager.Web.Tests;

/// <summary>
/// La pestaña «Sello» de Empresa (ficha 360 y panel): guardar el sello es
/// <c>GuardarSelloEmpresaCommand</c>, que <c>AutorizacionEscrituraBehavior</c> deniega al rol Consulta. A quien
/// solo lee se le enseña el sello guardado, no la zona de subida. Cada caso lleva su control positivo con un
/// rol de escritura: sin él, una zona que no se pintara a nadie daría verde.
/// </summary>
public class PestanaSelloEmpresaSoloLecturaTests : BunitContext
{
    private sealed class MediatorDelSello(SelloEmpresaDto? sello) : IMediator
    {
        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            request is ObtenerSelloEmpresaQuery
                ? Task.FromResult((TResponse)(object?)sello!)
                : throw new NotSupportedException($"Petición no esperada en el test: {request.GetType().Name}");

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

    private IRenderedComponent<PestanaSelloEmpresa> Renderizar(string rol, SelloEmpresaDto? sello)
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        this.ConRolDeEscritura(rol);
        Services.AddScoped<IMediator>(_ => new MediatorDelSello(sello));
        Services.AddScoped<ToastService>();
        return Render<PestanaSelloEmpresa>(p => p.Add(x => x.EntidadId, Guid.NewGuid()));
    }

    [Theory]
    [InlineData(Roles.Consulta, false)]
    [InlineData(Roles.GestorCae, true)]
    public void La_zona_de_subida_del_sello_solo_se_pinta_a_un_rol_con_escritura(string rol, bool sePinta)
    {
        var cut = Renderizar(rol, new SelloEmpresaDto("sellos/ibertec.png", new DateTime(2026, 9, 30, 10, 0, 0, DateTimeKind.Utc)));

        // Barrera: la pestaña cargó y enseña el sello guardado; si no, la ausencia de abajo sería verde vacío.
        cut.Find("img.firma-guardada-vista-previa").GetAttribute("alt").Should().Be("Sello de la empresa");

        cut.FindAll(".zona-soltar-archivo").Should().HaveCount(sePinta ? 1 : 0);
        cut.FindAll("input[type=file]").Should().HaveCount(sePinta ? 1 : 0);
    }

    [Theory]
    [InlineData(Roles.Consulta, false)]
    [InlineData(Roles.GestorCae, true)]
    public void Sin_sello_guardado_Consulta_ve_el_vacio_pero_no_la_zona_de_subida(string rol, bool sePinta)
    {
        var cut = Renderizar(rol, sello: null);

        cut.Markup.Should().Contain("Sin sello guardado");
        cut.FindAll("input[type=file]").Should().HaveCount(sePinta ? 1 : 0);
    }
}
