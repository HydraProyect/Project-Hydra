using Bunit;
using CaeManager.Application.Common;
using CaeManager.Application.Contactos.Queries.ObtenerAgendaContactos;
using CaeManager.Application.Empresas.Queries.ObtenerClientesDeEmpresa;
using CaeManager.Application.Empresas.Queries.ObtenerCumplimientoEmpresa;
using CaeManager.Application.Empresas.Queries.ObtenerEmpresaPorId;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadores;
using CaeManager.Web.Features.Empresas.Components;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// La celda «Clientes» del drawer de vista previa de Empresa cuenta los Clientes
/// empresariales con alcance de gestión (ObtenerClientesDeEmpresaQuery, REC-153),
/// nunca EmpresaDetalleDto.ClienteIds, que no está acotado y revelaría el tamaño
/// de una cartera que el actor no puede ver.
/// </summary>
public class EmpresaPreviewDrawerTests : BunitContext
{
    public EmpresaPreviewDrawerTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        this.ConRolDeEscritura();
    }

    private sealed class MediadorFalso : IMediator
    {
        public Dictionary<Guid, EmpresaDetalleDto> Detalles { get; } = [];
        public Dictionary<Guid, IReadOnlyList<ClienteDeEmpresaDto>> Clientes { get; } = [];

        public Task<T> Send<T>(IRequest<T> request, CancellationToken cancellationToken = default)
        {
            object? valor = request switch
            {
                ObtenerEmpresaPorIdQuery q => Detalles.GetValueOrDefault(q.Id),
                ObtenerCumplimientoEmpresaQuery => (int?)80,
                ObtenerClientesDeEmpresaQuery q => Clientes.GetValueOrDefault(q.EmpresaId) ?? [],
                ObtenerTrabajadoresQuery => new ResultadoPaginado<TrabajadorListaDto>([], 0, 1, 1),
                ObtenerAgendaContactosQuery => (IReadOnlyList<ContactoAgendaDto>)[],
                _ => throw new NotSupportedException(request.GetType().Name)
            };
            return Task.FromResult((T)valor!);
        }
        public Task Send<T>(T request, CancellationToken ct = default) where T : IRequest => Task.CompletedTask;
        public Task<object?> Send(object request, CancellationToken ct = default) => Task.FromResult<object?>(null);
        public IAsyncEnumerable<T> CreateStream<T>(IStreamRequest<T> r, CancellationToken ct = default) => throw new NotSupportedException();
        public IAsyncEnumerable<object?> CreateStream(object r, CancellationToken ct = default) => throw new NotSupportedException();
        public Task Publish(object n, CancellationToken ct = default) => Task.CompletedTask;
        public Task Publish<T>(T n, CancellationToken ct = default) where T : INotification => Task.CompletedTask;
    }

    private string CeldaClientes(MediadorFalso m, Guid id)
    {
        Services.AddScoped<IMediator>(_ => m);
        var cut = Render<EmpresaPreviewDrawer>(p => p.Add(x => x.EmpresaId, id).Add(x => x.Visible, true));
        return cut.WaitForElements(".celda-info-preview-empresa")
            .Where(c => c.QuerySelector("span")!.TextContent.Trim() == "Clientes")
            .Should().ContainSingle().Subject
            .QuerySelector("strong")!.TextContent.Trim();
    }

    [Fact]
    public void La_celda_de_clientes_cuenta_la_consulta_acotada_y_no_ClienteIds()
    {
        var id = Guid.NewGuid(); var m = new MediadorFalso();
        m.Detalles[id] = new(id, "Montajes Ebro S.L.", "B-50", DateTime.UtcNow, [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()], Guid.NewGuid());
        m.Clientes[id] = [new(Guid.NewGuid(), "Refrielectric S.A.", "A-01")];

        CeldaClientes(m, id).Should().Be("1", "el actor solo gestiona uno de los tres Clientes empresariales de la Empresa");
    }

    [Fact]
    public void Una_lista_vacia_por_falta_de_alcance_no_se_presenta_como_cero()
    {
        var id = Guid.NewGuid(); var m = new MediadorFalso();
        m.Detalles[id] = new(id, "Montajes Ebro S.L.", "B-50", DateTime.UtcNow, [Guid.NewGuid(), Guid.NewGuid()], Guid.NewGuid());

        CeldaClientes(m, id).Should().Be("—", "sin alcance de gestión la consulta devuelve vacío a propósito, y «0» sería falso");
    }
}
