using Microsoft.Extensions.DependencyInjection;
using CaeManager.Application.Common;
using CaeManager.Application.Centros.Commands.CrearCentro;
using CaeManager.Application.Tenants.Queries.ObtenerClientesAutorizados;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Workspace;
using FluentValidation;
using MediatR;
using Bunit;
using CaeManager.Application.Centros.Queries.ObtenerCentros;
using CaeManager.Web.Features.Centros.Pages;
using FluentAssertions;

namespace CaeManager.Web.Tests;

public partial class CentrosListaGen2Tests
{
    private void RegistrarFrescura(MediatorPorTipo mediador)
    {
        Services.AddScoped<IMediator>(_ => mediador);
        Services.AddScoped<ITenantActual>(_ => new SeleccionEmpresaGestionadaDePrueba());
        Services.AddScoped<ToastService>();
        Services.AddScoped<ContextWorkspaceService>();
        Services.AddScoped<ICurrentUserService, UsuarioActualFalso>();
        Services.AddScoped<IValidator<CrearCentroCommand>>(_ => new InlineValidator<CrearCentroCommand>());
    }

    [Fact]
    public async Task Frescura_no_pide_datos_sin_Empresa_elegida_ni_mientras_se_resuelve()
    {
        var circuito = await CircuitoFrescuraDePrueba.RegistrarAsync(this);
        var mediador = new MediatorPorTipo
        {
            Centros = [Centro("Centro conservado")],
            Autorizados = [
                new(Guid.NewGuid(), "Organización de origen", EsOrigen: true),
                new(Guid.NewGuid(), "Empresa gestionada", EsOrigen: false, EsGestionadoPorOperacion: true)]
        };
        var puerta = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        mediador.RetenerAutorizados = puerta.Task;
        RegistrarFrescura(mediador);
        var cut = Render<Centros>();
        cut.WaitForAssertion(() => mediador.Enviadas.OfType<ObtenerClientesAutorizadosQuery>().Should().ContainSingle());
        try
        {
            circuito.Publicar(2);
            await Task.Delay(CircuitoFrescuraDePrueba.EsperaNegativa);
            circuito.Cache.AvisosRecibidos.Should().Be(1);
            mediador.Enviadas.OfType<ObtenerCentrosQuery>().Should().BeEmpty("la Empresa todavía se está resolviendo");
            cut.FindAll(".lista-filas-acordeon").Should().BeEmpty();
        }
        finally
        {
            puerta.TrySetResult();
        }
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Selecciona una empresa de tu cartera"));
        circuito.Publicar(3);
        await Task.Delay(CircuitoFrescuraDePrueba.EsperaNegativa);
        circuito.Cache.AvisosRecibidos.Should().Be(2);
        mediador.Enviadas.OfType<ObtenerCentrosQuery>().Should().BeEmpty("no hay Empresa elegida");
        cut.FindAll(".lista-filas-acordeon").Should().BeEmpty();
    }

    [Fact]
    public async Task Frescura_conserva_la_lista_buena_si_falla_la_relectura_del_aviso()
    {
        var circuito = await CircuitoFrescuraDePrueba.RegistrarAsync(this);
        var mediador = new MediatorPorTipo { Centros = [Centro("Centro conservado")] };
        RegistrarFrescura(mediador);
        var cut = Render<Centros>();
        cut.WaitForAssertion(() => cut.FindAll(".enlace-nombre-fila").Should().ContainSingle());
        var nombres = cut.FindAll(".enlace-nombre-fila").Select(e => e.TextContent).ToArray();
        var lecturas = mediador.Enviadas.OfType<ObtenerCentrosQuery>().Count();
        var fallo = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        var iniciada = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        mediador.Retener = p =>
        {
            if (p is not ObtenerCentrosQuery) return null;
            iniciada.TrySetResult();
            return fallo.Task;
        };
        circuito.Publicar(2);
        await iniciada.Task.WaitAsync(CircuitoFrescuraDePrueba.EsperaPositiva);
        mediador.Enviadas.OfType<ObtenerCentrosQuery>().Should().HaveCount(lecturas + 1);
        fallo.SetException(new InvalidOperationException("Fallo de relectura provocado por el test."));
        await Task.Delay(CircuitoFrescuraDePrueba.EsperaNegativa);
        circuito.Cache.AvisosRecibidos.Should().Be(1);
        cut.FindAll(".enlace-nombre-fila").Select(e => e.TextContent).Should().Equal(nombres);
        cut.Markup.Should().NotContain("No pudimos cargar");
        cut.FindComponents<EstadoVacio>().Should().BeEmpty();
        cut.Markup.Should().NotContain("aria-busy=\"true\"");
    }
}
