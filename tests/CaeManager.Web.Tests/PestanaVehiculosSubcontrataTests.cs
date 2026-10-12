using Bunit;
using CaeManager.Application.Common;
using CaeManager.Application.Vehiculos.Queries.ObtenerVehiculos;
using CaeManager.Domain.Documentos;
using CaeManager.Web.Components.Workspace;
using CaeManager.Web.Features.Subcontratas.Components;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

public class PestanaVehiculosSubcontrataTests : BunitContext
{
    private sealed class Contenedor : ComponentBase
    {
        [Parameter] public bool Mostrar { get; set; }
        [Parameter] public Guid Id { get; set; }
        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            if (!Mostrar) return;
            builder.OpenComponent<PestanaVehiculosSubcontrata>(0);
            builder.AddAttribute(1, nameof(PestanaVehiculosSubcontrata.EmpresaContraparteId), Id);
            builder.CloseComponent();
        }
    }

    private sealed class Mediador : IMediator
    {
        public List<ObtenerVehiculosQuery> Consultas { get; } = [];
        public Func<ObtenerVehiculosQuery, CancellationToken, Task<ResultadoPaginado<VehiculoListaDto>>> Responder { get; set; } =
            (q, _) => Task.FromResult(new ResultadoPaginado<VehiculoListaDto>([], 0, q.Pagina, q.TamanoPagina));
        public async Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            var query = (ObtenerVehiculosQuery)(object)request;
            Consultas.Add(query);
            return (TResponse)(object)await Responder(query, cancellationToken);
        }
        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest => throw new NotSupportedException();
        public Task<object?> Send(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default) where TNotification : INotification => Task.CompletedTask;
    }

    private void Registrar(Mediador mediador)
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
        Services.AddScoped<IMediator>(_ => mediador);
        Services.AddScoped<ContextWorkspaceService>();
        Services.AddSingleton(TimeProvider.System);
    }

    private static VehiculoListaDto Vehiculo(string placa = "1234 ABC", EstadoDocumento? estado = EstadoDocumento.Vencido) =>
        new(Guid.NewGuid(), "Nombre interno", "Furgón largo", placa, "Montajes Ebro", estado);

    [Fact]
    public async Task Usa_la_Empresa_contraparte_de_la_ficha_ordena_peor_primero_y_pinta_matricula_modelo_estado_y_destinos()
    {
        var id = Guid.NewGuid();
        var vehiculo = Vehiculo();
        var mediador = new Mediador { Responder = (q, _) => Task.FromResult(new ResultadoPaginado<VehiculoListaDto>([vehiculo], 1, q.Pagina, q.TamanoPagina)) };
        Registrar(mediador);
        var cut = Render<PestanaVehiculosSubcontrata>(p => p.Add(x => x.EmpresaContraparteId, id));

        var consulta = mediador.Consultas.Should().ContainSingle().Which;
        consulta.SubcontrataId.Should().Be(id);
        consulta.EmpresaId.Should().BeNull();
        consulta.OrdenarPor.Should().Be(nameof(VehiculoListaDto.EstadoDocumental));
        consulta.Descendente.Should().BeFalse("el rango de estado de Vehículos es ascendente del peor al mejor");
        cut.Find(".fila-relacion-nombre").TextContent.Should().Be("1234 ABC");
        cut.Find(".fila-relacion-nombre").GetAttribute("href").Should().Be($"/vehiculos/{vehiculo.Id}");
        cut.Find(".fila-relacion-detalle").TextContent.Should().Be("Furgón largo");
        cut.Find(".fila-relacion-estado").TextContent.Trim().Should().Be("Vencido");
        cut.Find(".resumen-vehiculos-subcontrata").TextContent.Should().Be("Mostrando 1 de 1 · del peor estado al mejor");
        await cut.Find(".boton-360").ClickAsync(new MouseEventArgs());
        Services.GetRequiredService<ContextWorkspaceService>().FrameActual.Should()
            .Be(new WorkspaceFrame(EntidadWorkspace.Vehiculo, vehiculo.Id, vehiculo.NumeroPlaca, "informacion"));
    }

    [Fact]
    public void Un_estado_no_cargado_no_se_rotula_como_Vigente()
    {
        var mediador = new Mediador { Responder = (q, _) => Task.FromResult(new ResultadoPaginado<VehiculoListaDto>([Vehiculo(estado: null)], 1, q.Pagina, q.TamanoPagina)) };
        Registrar(mediador);
        var cut = Render<PestanaVehiculosSubcontrata>(p => p.Add(x => x.EmpresaContraparteId, Guid.NewGuid()));
        cut.Find(".fila-relacion-estado").TextContent.Trim().Should().Be("Estado desconocido");
        cut.Find(".fila-relacion").HasAttribute("data-tono").Should().BeFalse();
    }

    [Fact]
    public async Task El_total_no_se_confunde_con_la_pagina_y_Siguiente_pide_la_segunda_pagina()
    {
        var mediador = new Mediador { Responder = (q, _) => Task.FromResult(new ResultadoPaginado<VehiculoListaDto>([Vehiculo()], 21, q.Pagina, q.TamanoPagina)) };
        Registrar(mediador);
        var cut = Render<PestanaVehiculosSubcontrata>(p => p.Add(x => x.EmpresaContraparteId, Guid.NewGuid()));
        cut.Find(".resumen-vehiculos-subcontrata").TextContent.Should().Contain("Mostrando 1 de 21");
        await cut.FindAll(".paginador-simple button").Single(x => x.TextContent.Contains("Siguiente")).ClickAsync(new MouseEventArgs());
        mediador.Consultas.Select(q => q.Pagina).Should().Equal(1, 2);
        mediador.Consultas.Should().OnlyContain(q => q.TamanoPagina == 20);
    }

    [Fact]
    public async Task El_error_ofrece_reintento_y_el_vacio_no_es_un_error()
    {
        var fallar = true;
        var mediador = new Mediador
        {
            Responder = (q, _) => fallar
            ? throw new InvalidOperationException("Fallo de consulta")
            : Task.FromResult(new ResultadoPaginado<VehiculoListaDto>([], 0, q.Pagina, q.TamanoPagina))
        };
        Registrar(mediador);
        var cut = Render<PestanaVehiculosSubcontrata>(p => p.Add(x => x.EmpresaContraparteId, Guid.NewGuid()));
        cut.Markup.Should().Contain("No pudimos cargar los vehículos");
        fallar = false;
        await cut.Find("button").ClickAsync(new MouseEventArgs());
        cut.Markup.Should().Contain("Sin vehículos registrados").And.NotContain("No pudimos cargar los vehículos");
    }

    [Fact]
    public void Una_respuesta_del_anterior_no_pinta_vehiculos_en_la_nueva_Empresa_contraparte()
    {
        var anterior = Guid.NewGuid();
        var actual = Guid.NewGuid();
        var pendiente = new TaskCompletionSource<ResultadoPaginado<VehiculoListaDto>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var mediador = new Mediador
        {
            Responder = (q, _) => q.SubcontrataId == anterior ? pendiente.Task
            : Task.FromResult(new ResultadoPaginado<VehiculoListaDto>([Vehiculo("9999 NUE")], 1, q.Pagina, q.TamanoPagina))
        };
        Registrar(mediador);
        var cut = Render<PestanaVehiculosSubcontrata>(p => p.Add(x => x.EmpresaContraparteId, anterior));
        cut.Render(p => p.Add(x => x.EmpresaContraparteId, actual));
        var renderizados = cut.RenderCount;
        pendiente.SetResult(new ResultadoPaginado<VehiculoListaDto>([Vehiculo("0000 ANT")], 1, 1, 20));
        cut.WaitForState(() => cut.RenderCount > renderizados);
        cut.WaitForAssertion(() => cut.Find(".fila-relacion-nombre").TextContent.Should().Be("9999 NUE"));
        cut.Markup.Should().NotContain("0000 ANT");
    }

    [Fact]
    public void Retirar_la_pestana_cancela_su_lectura_en_vuelo()
    {
        CancellationToken capturado = default;
        var pendiente = new TaskCompletionSource<ResultadoPaginado<VehiculoListaDto>>();
        var mediador = new Mediador
        {
            Responder = (q, token) =>
        {
            capturado = token;
            return pendiente.Task;
        }
        };
        Registrar(mediador);
        var cut = Render<Contenedor>(p => p.Add(x => x.Id, Guid.NewGuid()).Add(x => x.Mostrar, true));
        cut.Render(p => p.Add(x => x.Mostrar, false));
        capturado.IsCancellationRequested.Should().BeTrue();
        pendiente.SetCanceled();
    }
}
