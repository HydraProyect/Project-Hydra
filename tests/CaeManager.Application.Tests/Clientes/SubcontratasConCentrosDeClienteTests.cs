using CaeManager.Application.Asignaciones.Queries.ObtenerAsignacionesDocumentacionPorCentro;
using CaeManager.Application.Subcontratas.Queries.ObtenerCentrosConActividadDeSubcontrata;
using CaeManager.Application.Centros;
using CaeManager.Application.Clientes.Queries.ObtenerCentrosDeCliente;
using CaeManager.Application.Clientes.Queries.ObtenerSubcontratasDeCliente;
using CaeManager.Application.Clientes.Queries.ObtenerSubcontratasConCentrosDeCliente;
using CaeManager.Application.Subcontratas.Queries.ObtenerCentrosConCumplimientoDeSubcontrata;
using CaeManager.Domain.Documentos;
using FluentAssertions;
using MediatR;

namespace CaeManager.Application.Tests.Clientes;

public class SubcontratasConCentrosDeClienteTests
{
    private readonly Guid _cliente = Guid.NewGuid();
    private readonly Guid _empresa = Guid.NewGuid();
    private readonly Guid _centro = Guid.NewGuid();
    private Mediador Datos()
    {
        var m = new Mediador();
        m.Propios.Add(new(_centro, "Nombre compartido", "Empresa propia"));
        m.Subcontratas.Add(new(_empresa, "Empresa contraparte"));
        m.Centros.Add(new(new(_centro, "Nombre compartido", "Cliente", 2), new(1, 2), EstadoDocumento.Faltante));
        m.Centros.Add(new(new(Guid.NewGuid(), "Nombre compartido", "Cliente", 3), new(3, 3), EstadoDocumento.Vigente));
        return m;
    }
    private Task<IReadOnlyList<SubcontrataConCentrosDeClienteDto>> Leer(Mediador m, AlcanceDatosServiceFalso? alcance = null) =>
        new ObtenerSubcontratasConCentrosDeClienteQueryHandler(m, alcance ?? new()).Handle(new(_cliente), CancellationToken.None);

    [Fact]
    public async Task Solo_Centros_del_Cliente_empresarial_por_Id_aunque_los_nombres_coincidan()
    {
        var m = Datos();
        var resultado = await Leer(m);
        resultado.Should().ContainSingle().Which.Subcontrata.Id.Should().Be(_empresa);
        resultado.Single().Centros.Should().ContainSingle().Which.Centro.Id.Should().Be(_centro);
        resultado.Single().Centros.Single().Cumplimiento.Should().Be(new CaeManager.Domain.Documentos.FraccionCumplimiento(1, 2));
        m.Enviadas.OfType<ObtenerCentrosDeClienteQuery>().Should().ContainSingle().Which.ClienteId.Should().Be(_cliente);
        m.Enviadas.OfType<ObtenerSubcontratasDeClienteQuery>().Should().ContainSingle().Which.ClienteId.Should().Be(_cliente);
        m.Enviadas.OfType<ObtenerCentrosConActividadDeSubcontrataQuery>().Should().ContainSingle().Which.SubcontrataId.Should().Be(_empresa);
        var documental = m.Enviadas.OfType<ObtenerAsignacionesDocumentacionPorCentroQuery>().Should().ContainSingle().Which;
        documental.CentroId.Should().Be(_centro);
        documental.EmpresaEmpleadoraSubcontratistaId.Should().Be(_empresa);
        m.Enviadas.OfType<ObtenerCentrosConCumplimientoDeSubcontrataQuery>().Should().BeEmpty();
    }
    [Fact]
    public async Task Sin_visibilidad_del_Cliente_empresarial_no_consulta()
    {
        var m = Datos();
        (await Leer(m, new(tieneAccesoTotal: false, clienteIdsVisibles: []))).Should().BeEmpty();
        m.Enviadas.Should().BeEmpty();
    }
    [Fact]
    public async Task Centro_propio_fuera_de_visibilidad_no_se_revela_ni_se_pide_cumplimiento()
    {
        var m = Datos();
        var resultado = await Leer(m, new(tieneAccesoTotal: false, clienteIdsVisibles: [_cliente], subcontrataIdsVisibles: [_empresa], centroIdsVisibles: []));
        resultado.Should().ContainSingle().Which.Centros.Should().BeEmpty();
        m.Enviadas.OfType<ObtenerAsignacionesDocumentacionPorCentroQuery>().Should().BeEmpty();
        m.Enviadas.OfType<ObtenerCentrosConCumplimientoDeSubcontrataQuery>().Should().BeEmpty();
    }
    [Fact]
    public async Task Empresa_con_Relacion_empresarial_fuera_de_visibilidad_no_se_revela()
    {
        var m = Datos();
        (await Leer(m, new(tieneAccesoTotal: false, clienteIdsVisibles: [_cliente], subcontrataIdsVisibles: [], centroIdsVisibles: [_centro]))).Should().BeEmpty();
        m.Enviadas.OfType<ObtenerCentrosConCumplimientoDeSubcontrataQuery>().Should().BeEmpty();
    }
    [Fact]
    public async Task Universo_de_Centros_completo_supera_la_pagina_de_doscientos()
    {
        var m = Datos();
        for (var i = 0; i < 201; i++)
        {
            var id = Guid.NewGuid();
            m.Propios.Add(new(id, $"Centro {i}", "Empresa propia"));
            m.Centros.Add(new(new(id, $"Centro {i}", "Cliente", 1), new(0, 0), null));
        }
        (await Leer(m)).Single().Centros.Should().HaveCount(202);
    }
    private sealed class Mediador : IMediator
    {
        public List<CentroDeClienteDto> Propios { get; } = [];
        public List<SubcontrataDeClienteDto> Subcontratas { get; } = [];
        public List<CentroCumplimientoSubcontrataDto> Centros { get; } = [];
        public List<object> Enviadas { get; } = [];
        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken ct = default)
        {
            Enviadas.Add(request);
            object respuesta = request switch
            {
                ObtenerCentrosDeClienteQuery => Propios,
                ObtenerSubcontratasDeClienteQuery => Subcontratas,
                ObtenerCentrosConActividadDeSubcontrataQuery => Centros.Select(c => c.Centro).ToList(),
                ObtenerAsignacionesDocumentacionPorCentroQuery q => Documentos(q.CentroId),
                _ => throw new NotSupportedException()
            };
            return Task.FromResult((TResponse)respuesta);
        }
        private IReadOnlyList<TrabajadorAsignacionDocumentacionDto> Documentos(Guid id)
        {
            var centro = Centros.Single(c => c.Centro.Id == id);
            return [new(Guid.NewGuid(), Guid.NewGuid(), "Trabajador", new DateOnly(2026, 1, 1),
                centro.PeorEstado ?? EstadoDocumento.Vigente, [], centro.Cumplimiento)];
        }
        public Task Send<TRequest>(TRequest request, CancellationToken ct = default) where TRequest : IRequest => Task.CompletedTask;
        public Task<object?> Send(object request, CancellationToken ct = default) => throw new NotSupportedException();
        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken ct = default) => throw new NotSupportedException();
        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task Publish(object notification, CancellationToken ct = default) => Task.CompletedTask;
        public Task Publish<TNotification>(TNotification notification, CancellationToken ct = default) where TNotification : INotification => Task.CompletedTask;
    }
}
