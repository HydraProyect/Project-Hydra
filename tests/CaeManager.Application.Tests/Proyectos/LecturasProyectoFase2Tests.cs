using CaeManager.Application.Common;
using CaeManager.Application.Documentos;
using CaeManager.Application.Proyectos;
using CaeManager.Application.Proyectos.Queries.ObtenerEmpresasProyecto;
using CaeManager.Application.Proyectos.Queries.ObtenerVisitasProyecto;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Documentos;
using CaeManager.Application.Tests.Integraciones;
using CaeManager.Application.Tests.Plantillas;
using CaeManager.Application.Trabajadores;
using CaeManager.Application.Visitas;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Proyectos;
using CaeManager.Domain.Trabajadores;
using CaeManager.Domain.Visitas;
using FluentAssertions;

namespace CaeManager.Application.Tests.Proyectos;

public class LecturasProyectoFase2Tests
{
    private sealed class ProyectosContexto : IProyectosQueryContext
    {
        public List<Proyecto> Lista { get; } = [];
        public List<ProyectoTecnico> Tecnicos { get; } = [];
        public IQueryable<Proyecto> Proyectos => new TestAsyncQueryable<Proyecto>(Lista.AsQueryable());
        public IQueryable<ProyectoTecnico> ProyectosTecnicos => new TestAsyncQueryable<ProyectoTecnico>(Tecnicos.AsQueryable());
    }

    private sealed class VisitasContexto : IVisitasQueryContext
    {
        public List<Visita> Lista { get; } = [];
        public List<VisitaTrabajador> Trabajadores { get; } = [];
        public IQueryable<Visita> Visitas => new TestAsyncQueryable<Visita>(Lista.AsQueryable());
        public IQueryable<VisitaTrabajador> VisitasTrabajadores => new TestAsyncQueryable<VisitaTrabajador>(Trabajadores.AsQueryable());
    }

    private readonly ProyectosContexto _proyectos = new();
    private readonly VisitasContexto _visitas = new();
    private readonly CentrosQueryContextFalso _centros = new();
    private readonly EmpresasQueryContextFalso _empresas = new();
    private readonly TrabajadoresQueryContextFalso _trabajadores = new();
    private readonly Empresa _empleadora = new("Empresa empleadora del Proyecto");
    private readonly Centro _centro;
    private readonly Proyecto _proyecto;

    public LecturasProyectoFase2Tests()
    {
        var clienteEmpresarialId = Guid.NewGuid();
        _centro = new Centro(clienteEmpresarialId, Guid.NewGuid(), "Centro del Proyecto");
        _proyecto = Proyecto.Crear(clienteEmpresarialId, _centro.Id, "Proyecto", new(2026, 10, 1), new(2026, 10, 31), null);
        _proyectos.Lista.Add(_proyecto);
        _centros.ListaCentros.Add(_centro);
        _empresas.ListaEmpresas.Add(_empleadora);
        var tecnico = Trabajador.DeEmpresa(_empleadora.Id, "Ana", "Alba", "60005002A");
        _trabajadores.ListaTrabajadores.Add(tecnico);
        _proyectos.Tecnicos.Add(new ProyectoTecnico(_proyecto.Id, tecnico.Id, new(2026, 10, 1)));
        _visitas.Lista.Add(new Visita(_centro.Id, new(2026, 10, 4), new(2026, 10, 5), null));
    }

    private readonly CalculoDocumentalFalso Calculo = new();

    private Task<IReadOnlyList<EmpresaProyectoDto>> EmpresasAsync(IAlcanceDatosService? alcance = null) =>
        new ObtenerEmpresasProyectoQueryHandler(_proyectos, _trabajadores, _empresas, _centros,
            alcance ?? new AlcanceDatosServiceFalso(), Calculo)
            .Handle(new(_proyecto.Id), CancellationToken.None);

    private Task<IReadOnlyList<VisitaProyectoDto>> VisitasAsync(IAlcanceDatosService? alcance = null) =>
        new ObtenerVisitasProyectoQueryHandler(_proyectos, _centros, _visitas, alcance ?? new AlcanceDatosServiceFalso())
            .Handle(new(_proyecto.Id), CancellationToken.None);

    [Fact]
    public async Task Sin_alcance_al_Cliente_empresarial_no_se_revela_ninguna_Empresa_ni_Visita()
    {
        var alcance = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, clienteIdsVisibles: [],
            centroIdsVisibles: [_centro.Id], empresaIdsVisibles: [_empleadora.Id]);
        (await EmpresasAsync(alcance)).Should().BeEmpty();
        (await VisitasAsync(alcance)).Should().BeEmpty();
    }

    [Fact]
    public async Task Sin_alcance_al_Centro_no_se_revela_ninguna_Empresa_ni_Visita()
    {
        var alcance = new AlcanceDatosServiceFalso(tieneAccesoTotal: false,
            clienteIdsVisibles: [_proyecto.ClienteId], centroIdsVisibles: [], empresaIdsVisibles: [_empleadora.Id]);
        (await EmpresasAsync(alcance)).Should().BeEmpty();
        (await VisitasAsync(alcance)).Should().BeEmpty();
    }

    [Fact]
    public async Task La_Empresa_empleadora_fuera_del_alcance_no_aporta_nombre_ni_tecnicos()
    {
        var alcance = new AlcanceDatosServiceFalso(tieneAccesoTotal: false,
            clienteIdsVisibles: [_proyecto.ClienteId], centroIdsVisibles: [_centro.Id], empresaIdsVisibles: []);
        (await EmpresasAsync(alcance)).Should().BeEmpty();
    }

    [Fact]
    public async Task Un_Proyecto_inexistente_no_revela_datos()
    {
        _proyectos.Lista.Clear();
        (await EmpresasAsync()).Should().BeEmpty();
        (await VisitasAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task La_relacion_Subcontrata_exige_su_alcance_y_conserva_su_ruta_contextual()
    {
        var trabajador = Trabajador.DeSubcontrata(_empleadora.Id, "Ana", "Alba", "60005002A");
        _trabajadores.ListaTrabajadores.Clear();
        _trabajadores.ListaTrabajadores.Add(trabajador);
        _proyectos.Tecnicos.Clear();
        _proyectos.Tecnicos.Add(new ProyectoTecnico(_proyecto.Id, trabajador.Id, new(2026, 10, 1)));
        var restringido = new AlcanceDatosServiceFalso(tieneAccesoTotal: false,
            clienteIdsVisibles: [_proyecto.ClienteId], centroIdsVisibles: [_centro.Id], subcontrataIdsVisibles: []);
        (await EmpresasAsync(restringido)).Should().BeEmpty();
        var permitido = new AlcanceDatosServiceFalso(tieneAccesoTotal: false,
            clienteIdsVisibles: [_proyecto.ClienteId], centroIdsVisibles: [_centro.Id], subcontrataIdsVisibles: [_empleadora.Id]);
        (await EmpresasAsync(permitido)).Should().ContainSingle().Which.DesdeRelacionSubcontrata.Should().BeTrue();
    }

    [Fact]
    public async Task Un_tecnico_de_baja_o_de_otro_Proyecto_no_aporta_Empresa()
    {
        _proyectos.Tecnicos[0].DarDeBaja(new(2026, 10, 2));
        _proyectos.Tecnicos.Add(new ProyectoTecnico(Guid.NewGuid(), _trabajadores.ListaTrabajadores[0].Id, new(2026, 10, 1)));
        (await EmpresasAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Cada_Empresa_lleva_el_estado_y_la_cifra_de_sus_propios_documentos()
    {
        Calculo.PorPropietario[_empleadora.Id] = (EstadoDocumento.Vencido, new DesgloseDocumentalDto([], 8, 6));

        var empresa = (await EmpresasAsync()).Should().ContainSingle().Which;

        empresa.EstadoDocumental.Should().Be(EstadoDocumento.Vencido);
        empresa.DocumentosRegistrados.Should().Be(8);
        empresa.DocumentosVigentes.Should().Be(6);
    }

    [Fact]
    public async Task Una_Empresa_sin_documentos_no_lleva_estado()
    {
        var empresa = (await EmpresasAsync()).Should().ContainSingle().Which;

        empresa.EstadoDocumental.Should().BeNull();
        empresa.DocumentosRegistrados.Should().Be(0);
    }

    [Fact]
    public async Task La_Empresa_con_ambos_papeles_conserva_una_fila_y_los_dos_tecnicos()
    {
        var contraparte = Trabajador.DeSubcontrata(_empleadora.Id, "Leo", "Mas", "11111111H");
        _trabajadores.ListaTrabajadores.Add(contraparte);
        _proyectos.Tecnicos.Add(new ProyectoTecnico(_proyecto.Id, contraparte.Id, new(2026, 10, 1)));
        var empresa = (await EmpresasAsync()).Should().ContainSingle().Which;
        empresa.DesdeEmpresaDirecta.Should().BeTrue();
        empresa.DesdeRelacionSubcontrata.Should().BeTrue();
        empresa.TecnicosActivos.Should().Be(2);
        var directo = new AlcanceDatosServiceFalso(tieneAccesoTotal: false,
            clienteIdsVisibles: [_proyecto.ClienteId], centroIdsVisibles: [_centro.Id],
            empresaIdsVisibles: [_empleadora.Id], subcontrataIdsVisibles: []);
        empresa = (await EmpresasAsync(directo)).Should().ContainSingle().Which;
        empresa.DesdeEmpresaDirecta.Should().BeTrue();
        empresa.DesdeRelacionSubcontrata.Should().BeFalse();
        empresa.TecnicosActivos.Should().Be(1);
    }

    [Fact]
    public async Task El_DTO_de_Empresa_no_expone_fraccion_ni_estado_documental()
    {
        (await EmpresasAsync()).Should().ContainSingle().Which.TecnicosActivos.Should().Be(1);
        typeof(EmpresaProyectoDto).GetProperty("Cumplimiento").Should().BeNull();
        typeof(EmpresaProyectoDto).GetProperty("PeorEstado").Should().BeNull();
    }

    [Fact]
    public async Task Las_Visitas_son_del_Centro_intersectan_el_periodo_y_no_estan_canceladas()
    {
        _visitas.Lista.Add(new Visita(Guid.NewGuid(), new(2026, 10, 4), new(2026, 10, 5), null));
        _visitas.Lista.Add(new Visita(_centro.Id, new(2026, 9, 1), new(2026, 9, 2), null));
        _visitas.Lista.Add(new Visita(_centro.Id, new(2026, 11, 1), new(2026, 11, 2), null));
        var solapada = new Visita(_centro.Id, new(2026, 9, 30), new(2026, 10, 1), null);
        _visitas.Lista.Add(solapada);
        var cancelada = new Visita(_centro.Id, new(2026, 10, 7), new(2026, 10, 8), null);
        cancelada.Cancelar(DateTime.UtcNow, "Cancelada para la prueba");
        _visitas.Lista.Add(cancelada);
        var filas = await VisitasAsync();
        filas.Should().HaveCount(2);
        filas[0].Id.Should().Be(solapada.Id);
    }

    [Fact]
    public async Task Un_periodo_abierto_no_inventa_una_fecha_final()
    {
        var abierto = Proyecto.Crear(_proyecto.ClienteId, _centro.Id, "Periodo abierto", new(2026, 10, 1), null, null);
        _proyectos.Lista.Add(abierto);
        var futura = new Visita(_centro.Id, new(2028, 10, 7), new(2028, 10, 8), null);
        _visitas.Lista.Add(futura);
        var filas = await new ObtenerVisitasProyectoQueryHandler(_proyectos, _centros, _visitas, new AlcanceDatosServiceFalso())
            .Handle(new(abierto.Id), CancellationToken.None);
        filas.Should().Contain(v => v.Id == futura.Id);
    }

    [Fact]
    public async Task El_cierre_real_acota_el_periodo_aunque_la_fecha_prevista_sea_posterior()
    {
        _proyecto.Cerrar(new(2026, 10, 10));
        var posterior = new Visita(_centro.Id, new(2026, 10, 20), new(2026, 10, 21), null);
        _visitas.Lista.Add(posterior);
        (await VisitasAsync()).Should().NotContain(v => v.Id == posterior.Id);
    }
}
