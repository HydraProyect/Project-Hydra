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

    /// <summary>Las lecturas por Subcontrata o por Centro que el handler ya no hace (#2130).</summary>
    private static void SinLecturasPorSubcontrataNiPorCentro(Mediador m)
    {
        m.Enviadas.OfType<ObtenerCentrosConActividadDeSubcontrataQuery>().Should().BeEmpty();
        m.Enviadas.OfType<ObtenerCumplimientoDeAsignacionesPorCentrosQuery>().Should().BeEmpty();
        m.Enviadas.OfType<ObtenerAsignacionesDocumentacionPorCentroQuery>().Should().BeEmpty();
        m.Enviadas.OfType<ObtenerCentrosConCumplimientoDeSubcontrataQuery>().Should().BeEmpty();
    }

    [Fact]
    public async Task Solo_Centros_del_Cliente_empresarial_por_Id_aunque_los_nombres_coincidan()
    {
        var m = Datos();
        var resultado = await Leer(m);
        resultado.Should().ContainSingle().Which.Subcontrata.Id.Should().Be(_empresa);
        resultado.Single().Centros.Should().ContainSingle().Which.Centro.Id.Should().Be(_centro);
        resultado.Single().Centros.Single().Cumplimiento.Should().Be(new FraccionCumplimiento(1, 2));
        m.Enviadas.OfType<ObtenerCentrosDeClienteQuery>().Should().ContainSingle().Which.ClienteId.Should().Be(_cliente);
        m.Enviadas.OfType<ObtenerSubcontratasDeClienteQuery>().Should().ContainSingle().Which.ClienteId.Should().Be(_cliente);
        m.Enviadas.OfType<ObtenerCentrosConActividadDeSubcontratasQuery>().Should().ContainSingle()
            .Which.SubcontrataIds.Should().BeEquivalentTo([_empresa]);
        var documental = m.Enviadas.OfType<ObtenerCumplimientoDeAsignacionesPorCentrosYEmpresasQuery>().Should().ContainSingle().Which;
        documental.CentroIds.Should().BeEquivalentTo([_centro]);
        documental.EmpresaEmpleadoraSubcontratistaIds.Should().BeEquivalentTo([_empresa]);
        SinLecturasPorSubcontrataNiPorCentro(m);
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
        m.Enviadas.OfType<ObtenerCentrosConActividadDeSubcontratasQuery>().Should().BeEmpty();
        m.Enviadas.OfType<ObtenerCumplimientoDeAsignacionesPorCentrosYEmpresasQuery>().Should().BeEmpty();
        SinLecturasPorSubcontrataNiPorCentro(m);
    }
    [Fact]
    public async Task Empresa_con_Relacion_empresarial_fuera_de_visibilidad_no_se_revela()
    {
        var m = Datos();
        (await Leer(m, new(tieneAccesoTotal: false, clienteIdsVisibles: [_cliente], subcontrataIdsVisibles: [], centroIdsVisibles: [_centro]))).Should().BeEmpty();
        m.Enviadas.OfType<ObtenerCentrosConActividadDeSubcontratasQuery>().Should().BeEmpty();
        m.Enviadas.OfType<ObtenerCumplimientoDeAsignacionesPorCentrosYEmpresasQuery>().Should().BeEmpty();
        SinLecturasPorSubcontrataNiPorCentro(m);
    }
    [Fact]
    public async Task Solo_se_piden_las_Subcontratas_visibles()
    {
        var m = Datos();
        var oculta = Guid.NewGuid();
        m.Subcontratas.Add(new(oculta, "Contraparte fuera de cartera"));
        var resultado = await Leer(m, new(tieneAccesoTotal: false, clienteIdsVisibles: [_cliente], subcontrataIdsVisibles: [_empresa], centroIdsVisibles: [_centro]));
        resultado.Select(r => r.Subcontrata.Id).Should().Equal(_empresa);
        m.Enviadas.OfType<ObtenerCentrosConActividadDeSubcontratasQuery>().Single().SubcontrataIds.Should().BeEquivalentTo([_empresa]);
        m.Enviadas.OfType<ObtenerCumplimientoDeAsignacionesPorCentrosYEmpresasQuery>().Single().EmpresaEmpleadoraSubcontratistaIds.Should().BeEquivalentTo([_empresa]);
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
    /// <summary>
    /// El número de lecturas no crece con las Subcontratas ni con los Centros (#2130): siempre las mismas cuatro, una de
    /// cada, en vez de una lectura de actividad y una de cumplimiento por Subcontrata.
    /// </summary>
    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 3)]
    [InlineData(7, 5)]
    public async Task Las_lecturas_son_cuatro_sean_cuantas_sean_las_Subcontratas_y_los_Centros(int subcontratas, int centros)
    {
        var m = new Mediador();
        for (var c = 0; c < centros; c++)
        {
            var id = Guid.NewGuid();
            m.Propios.Add(new(id, $"Centro {c}", "Empresa propia"));
            m.Centros.Add(new(new(id, $"Centro {c}", "Cliente", 1), new(c, c + 1), EstadoDocumento.Faltante));
        }
        for (var s = 0; s < subcontratas; s++)
            m.Subcontratas.Add(new(Guid.NewGuid(), $"Contraparte {s}"));

        var resultado = await Leer(m);

        resultado.Should().HaveCount(subcontratas).And.OnlyContain(r => r.Centros.Count == centros);
        m.Enviadas.Should().HaveCount(4);
        m.Enviadas.OfType<ObtenerCentrosDeClienteQuery>().Should().ContainSingle();
        m.Enviadas.OfType<ObtenerSubcontratasDeClienteQuery>().Should().ContainSingle();
        m.Enviadas.OfType<ObtenerCentrosConActividadDeSubcontratasQuery>().Should().ContainSingle()
            .Which.SubcontrataIds.Should().HaveCount(subcontratas);
        var lote = m.Enviadas.OfType<ObtenerCumplimientoDeAsignacionesPorCentrosYEmpresasQuery>().Should().ContainSingle().Which;
        lote.CentroIds.Should().HaveCount(centros);
        lote.EmpresaEmpleadoraSubcontratistaIds.Should().BeEquivalentTo(m.Subcontratas.Select(s => s.Id));
        SinLecturasPorSubcontrataNiPorCentro(m);
    }
    [Fact]
    public async Task Varios_Centros_y_Subcontratas_dan_el_mismo_resultado_y_orden()
    {
        var m = Datos();
        var segundoCentro = Guid.NewGuid();
        var segundaEmpresa = Guid.NewGuid();
        m.Propios.Add(new(segundoCentro, "Otro centro", "Empresa propia"));
        m.Centros.Add(new(new(segundoCentro, "Otro centro", "Cliente", 1), new(2, 5), EstadoDocumento.Vencido));
        m.Subcontratas.Add(new(segundaEmpresa, "Segunda contraparte"));
        var resultado = await Leer(m);
        resultado.Select(r => r.Subcontrata.Id).Should().Equal(_empresa, segundaEmpresa);
        foreach (var r in resultado)
        {
            r.Centros.Select(c => c.Centro.Id).Should().Equal(_centro, segundoCentro);
            r.Centros.Select(c => c.Cumplimiento).Should().Equal(new FraccionCumplimiento(1, 2), new FraccionCumplimiento(2, 5));
            r.Centros.Select(c => c.PeorEstado).Should().Equal(EstadoDocumento.Faltante, EstadoDocumento.Vencido);
        }
        SinLecturasPorSubcontrataNiPorCentro(m);
    }
    /// <summary>
    /// Cada Subcontrata recibe el cumplimiento de sus propios Trabajadores en cada Centro, no el de otra: el lote se reparte
    /// por Empresa empleadora. El mediador simulado da valores distintos por par, así que reutilizar el de la primera daría rojo.
    /// </summary>
    [Fact]
    public async Task Cada_Subcontrata_recibe_su_propio_cumplimiento_en_cada_Centro()
    {
        var m = Datos();
        var segundaEmpresa = Guid.NewGuid();
        m.Subcontratas.Add(new(segundaEmpresa, "Segunda contraparte"));
        m.CumplimientoPor[(_empresa, _centro)] = [new(Guid.NewGuid(), EstadoDocumento.Faltante, new(1, 4))];
        m.CumplimientoPor[(segundaEmpresa, _centro)] = [
            new(Guid.NewGuid(), EstadoDocumento.Vencido, new(2, 3)),
            new(Guid.NewGuid(), EstadoDocumento.Faltante, new(0, 0))];

        var resultado = await Leer(m);

        resultado.Single(r => r.Subcontrata.Id == _empresa).Centros.Single()
            .Should().Match<CentroCumplimientoSubcontrataDto>(c => c.Cumplimiento == new FraccionCumplimiento(1, 4) && c.PeorEstado == EstadoDocumento.Faltante);
        // Un Trabajador sin requisitos no aporta peor estado.
        resultado.Single(r => r.Subcontrata.Id == segundaEmpresa).Centros.Single()
            .Should().Match<CentroCumplimientoSubcontrataDto>(c => c.Cumplimiento == new FraccionCumplimiento(2, 3) && c.PeorEstado == EstadoDocumento.Vencido);
    }
    [Fact]
    public async Task Subcontrata_sin_actividad_en_los_Centros_del_Cliente_sale_sin_Centros_y_no_entra_en_el_lote()
    {
        var m = Datos();
        var sinActividad = Guid.NewGuid();
        m.Subcontratas.Add(new(sinActividad, "Sin actividad"));
        m.ActividadPor[sinActividad] = [];

        var resultado = await Leer(m);

        resultado.Select(r => r.Subcontrata.Id).Should().Equal(_empresa, sinActividad);
        resultado[1].Centros.Should().BeEmpty();
        m.Enviadas.OfType<ObtenerCumplimientoDeAsignacionesPorCentrosYEmpresasQuery>().Single()
            .EmpresaEmpleadoraSubcontratistaIds.Should().BeEquivalentTo([_empresa]);
    }
    [Fact]
    public async Task Centro_visible_sin_Trabajadores_sigue_en_el_resultado_con_cero_de_cero()
    {
        var m = Datos();
        var vacio = Guid.NewGuid();
        m.Propios.Add(new(vacio, "Sin trabajadores", "Empresa propia"));
        m.Centros.Add(new(new(vacio, "Sin trabajadores", "Cliente", 1), new(0, 0), null));
        m.SinTrabajadores.Add(vacio);
        var centros = (await Leer(m)).Single().Centros;
        centros.Select(c => c.Centro.Id).Should().Equal(_centro, vacio);
        centros[1].Cumplimiento.Should().Be(new FraccionCumplimiento(0, 0));
        centros[1].PeorEstado.Should().BeNull();
    }
    [Fact]
    public async Task Usuario_fuera_de_cartera_solo_pide_el_lote_de_sus_Centros_visibles()
    {
        var m = Datos();
        var ajeno = Guid.NewGuid();
        m.Propios.Add(new(ajeno, "Fuera de cartera", "Empresa propia"));
        m.Centros.Add(new(new(ajeno, "Fuera de cartera", "Cliente", 1), new(0, 4), EstadoDocumento.Faltante));
        var resultado = await Leer(m, new(tieneAccesoTotal: false, clienteIdsVisibles: [_cliente], subcontrataIdsVisibles: [_empresa], centroIdsVisibles: [_centro]));
        resultado.Single().Centros.Select(c => c.Centro.Id).Should().Equal(_centro);
        m.Enviadas.OfType<ObtenerCumplimientoDeAsignacionesPorCentrosYEmpresasQuery>().Should().ContainSingle()
            .Which.CentroIds.Should().BeEquivalentTo([_centro]);
    }
    private sealed class Mediador : IMediator
    {
        public List<CentroDeClienteDto> Propios { get; } = [];
        public List<SubcontrataDeClienteDto> Subcontratas { get; } = [];
        /// <summary>Los Centros con actividad de cada Subcontrata salvo que <see cref="ActividadPor"/> diga otra cosa.</summary>
        public List<CentroCumplimientoSubcontrataDto> Centros { get; } = [];
        public Dictionary<Guid, List<CentroConActividadDto>> ActividadPor { get; } = [];
        /// <summary>El cumplimiento de un par Subcontrata y Centro; sin entrada, el de <see cref="Centros"/>.</summary>
        public Dictionary<(Guid Empresa, Guid Centro), IReadOnlyList<CumplimientoDeAsignacionDto>> CumplimientoPor { get; } = [];
        public List<object> Enviadas { get; } = [];
        /// <summary>Como el lote real: un Centro sin Asignación activa no trae clave en el diccionario.</summary>
        public HashSet<Guid> SinTrabajadores { get; } = [];
        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken ct = default)
        {
            Enviadas.Add(request);
            object respuesta = request switch
            {
                ObtenerCentrosDeClienteQuery => Propios,
                ObtenerSubcontratasDeClienteQuery => Subcontratas,
                ObtenerCentrosConActividadDeSubcontratasQuery q => (IReadOnlyDictionary<Guid, IReadOnlyList<CentroConActividadDto>>)q.SubcontrataIds
                    .ToDictionary(id => id, id => (IReadOnlyList<CentroConActividadDto>)(ActividadPor.GetValueOrDefault(id) ?? Centros.Select(c => c.Centro).ToList())),
                ObtenerCumplimientoDeAsignacionesPorCentrosYEmpresasQuery q => (IReadOnlyDictionary<Guid, IReadOnlyDictionary<Guid, IReadOnlyList<CumplimientoDeAsignacionDto>>>)q.EmpresaEmpleadoraSubcontratistaIds
                    .ToDictionary(empresa => empresa, empresa => (IReadOnlyDictionary<Guid, IReadOnlyList<CumplimientoDeAsignacionDto>>)q.CentroIds
                        .Where(id => !SinTrabajadores.Contains(id))
                        .ToDictionary(id => id, id => CumplimientoPor.GetValueOrDefault((empresa, id)) ?? Documentos(id))),
                _ => throw new NotSupportedException()
            };
            return Task.FromResult((TResponse)respuesta);
        }
        private IReadOnlyList<CumplimientoDeAsignacionDto> Documentos(Guid id)
        {
            var centro = Centros.Single(c => c.Centro.Id == id);
            return [new(Guid.NewGuid(), centro.PeorEstado ?? EstadoDocumento.Vigente, centro.Cumplimiento)];
        }
        public Task Send<TRequest>(TRequest request, CancellationToken ct = default) where TRequest : IRequest => Task.CompletedTask;
        public Task<object?> Send(object request, CancellationToken ct = default) => throw new NotSupportedException();
        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken ct = default) => throw new NotSupportedException();
        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task Publish(object notification, CancellationToken ct = default) => Task.CompletedTask;
        public Task Publish<TNotification>(TNotification notification, CancellationToken ct = default) where TNotification : INotification => Task.CompletedTask;
    }
}
