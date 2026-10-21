using Bunit;
using CaeManager.Application.Common;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadores;
using CaeManager.Application.Clientes.Queries.ObtenerClientes;
using CaeManager.Application.Documentos.Queries.ObtenerDocumentos;
using CaeManager.Application.Gestiones.Queries.ObtenerGestiones;
using CaeManager.Application.Incidencias.Queries.ObtenerIncidencias;
using CaeManager.Application.Vehiculos.Queries.ObtenerVehiculos;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Workspace;
using Microsoft.AspNetCore.Components;

namespace CaeManager.Web.Tests;

internal static class ComprobacionPieDuranteCarga
{
    public static void SinFranjaVacia<T>(IRenderedComponent<T> cut) where T : IComponent
    {
        cut.FindAll(".tarjeta-listado").Should().ContainSingle();
        cut.FindAll(".tarjeta-listado-pie").Should().BeEmpty("el total aún no se conoce y no debe quedar una franja vacía");
        cut.FindAll(".paginador-texto").Should().BeEmpty();
    }
}

public partial class TrabajadoresListaGen2Tests
{
    [Fact]
    public async Task La_primera_carga_no_deja_una_franja_de_pie_vacia()
    {
        var respuesta = new TaskCompletionSource<object>();
        var mediador = new MediatorFalso { Retener = q => q is ObtenerTrabajadoresQuery ? respuesta.Task : null };
        Registrar(mediador, "trabajadores");
        var cut = Render<CaeManager.Web.Features.Trabajadores.Pages.Trabajadores>();
        try
        {
            // Como Empresas: mientras llega la primera pagina solo hay el esqueleto, sin tarjeta ni pie.
            cut.FindAll(".esqueleto-lista").Should().ContainSingle();
            cut.FindAll(".tarjeta-listado, .tarjeta-listado-pie, .paginador-texto").Should().BeEmpty("el total aun no se conoce y no debe quedar una franja vacia");
        }
        finally
        {
            await cut.InvokeAsync(() => respuesta.SetResult(new ResultadoPaginado<TrabajadorListaDto>([], 0, 1, 20)));
        }
        ComprobacionPieListado.Vacio(cut);
    }
}

public partial class ClientesListaGen2Tests
{
    [Fact]
    public async Task La_primera_carga_no_deja_una_franja_de_pie_vacia()
    {
        var respuesta = new TaskCompletionSource<object>();
        var mediador = new MediatorFalso { Retener = q => q is ObtenerClientesQuery ? respuesta.Task : null };
        Registrar(mediador);
        Services.GetRequiredService<NavigationManager>().NavigateTo("clientes");
        var cut = Render<CaeManager.Web.Features.Clientes.Pages.Clientes>();
        try
        {
            // Como Empresas y Trabajadores: mientras llega la primera página solo hay el esqueleto, sin tarjeta ni pie.
            cut.FindAll(".esqueleto-lista").Should().ContainSingle();
            cut.FindAll(".tarjeta-listado, .tarjeta-listado-pie, .paginador-texto").Should().BeEmpty("el total aún no se conoce y no debe quedar una franja vacía");
        }
        finally
        {
            await cut.InvokeAsync(() => respuesta.SetResult(new ResultadoPaginado<ClienteListaDto>([], 0, 1, 20)));
        }
        ComprobacionPieListado.Vacio(cut);
    }
}

public partial class GestionesListaGen2Tests
{
    [Fact]
    public async Task La_primera_carga_no_deja_una_franja_de_pie_vacia()
    {
        var respuesta = new TaskCompletionSource<object>();
        var mediador = new MediatorFalso { Retener = q => q is ObtenerGestionesQuery ? respuesta.Task : null };
        Services.AddScoped<IMediator>(_ => mediador);
        Services.AddScoped<ToastService>();
        Services.AddScoped<ContextWorkspaceService>();
        var cut = Render<CaeManager.Web.Features.Gestiones.Pages.Gestiones>();
        try
        {
            ComprobacionPieDuranteCarga.SinFranjaVacia(cut);
        }
        finally
        {
            await cut.InvokeAsync(() => respuesta.SetResult(new ResultadoPaginado<GestionListaDto>([], 0, 1, 20)));
        }
        ComprobacionPieListado.Vacio(cut);
    }
}

public partial class IncidenciasGen2Tests
{
    [Fact]
    public async Task La_primera_carga_no_deja_una_franja_de_pie_vacia()
    {
        var mediador = new MediadorControlado { DiferirCarga = _ => true };
        var cut = Renderizar(mediador);
        cut.WaitForAssertion(() => mediador.CargasPendientes.Should().NotBeEmpty());
        ComprobacionPieDuranteCarga.SinFranjaVacia(cut);
        foreach (var carga in mediador.CargasPendientes.ToList())
            await cut.InvokeAsync(() => carga.Respuesta.TrySetResult(new ResultadoPaginado<IncidenciaListaDto>([], 0, 1, 20)));
        ComprobacionPieListado.Vacio(cut);
    }
}

public partial class DocumentosGen2Tests
{
    [Fact]
    public async Task La_primera_carga_no_deja_una_franja_de_pie_vacia()
    {
        var respuesta = new TaskCompletionSource<object?>();
        var mediador = new MediadorControlado { Interceptar = q => q is ObtenerDocumentosQuery ? respuesta.Task : null };
        var (cut, _) = Renderizar(mediador);
        try
        {
            ComprobacionPieDuranteCarga.SinFranjaVacia(cut);
        }
        finally
        {
            await cut.InvokeAsync(() => respuesta.SetResult(new ResultadoPaginado<DocumentoListaDto>([], 0, 1, 20)));
        }
        ComprobacionPieListado.Vacio(cut);
    }
}

public partial class VehiculosVacioPorFiltroTests
{
    [Fact]
    public async Task La_primera_carga_no_deja_una_franja_de_pie_vacia()
    {
        var respuesta = new TaskCompletionSource<object>();
        _listaRetenida = respuesta.Task;
        var cut = Renderizar();
        try
        {
            ComprobacionPieDuranteCarga.SinFranjaVacia(cut);
        }
        finally
        {
            await cut.InvokeAsync(() => respuesta.SetResult(new ResultadoPaginado<VehiculoListaDto>([], 0, 1, 20)));
        }
        ComprobacionPieListado.Vacio(cut);
    }
}

public partial class VisitasGen2Tests
{
    [Fact]
    public async Task La_primera_carga_no_deja_una_franja_de_pie_vacia()
    {
        var mediador = new MediatorVisitas { DiferirLista = true };
        var cut = Renderizar(mediador);
        try
        {
            ComprobacionPieDuranteCarga.SinFranjaVacia(cut);
        }
        finally
        {
            await cut.InvokeAsync(() => mediador.CargasPendientes.Single().Respuesta.SetResult(Pagina()));
        }
        ComprobacionPieListado.Vacio(cut);
    }
}
