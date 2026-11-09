using CaeManager.Application.Alertas;
using CaeManager.Application.Alertas.Queries.ObtenerAlertas;
using CaeManager.Application.Asignaciones;
using CaeManager.Application.Bandeja.Queries.ObtenerBandejaGestor;
using CaeManager.Application.Bandeja.Queries.ObtenerMiTrabajoAgregado;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Documentos;
using CaeManager.Application.Tests.Plantillas;
using CaeManager.Application.Tests.Proyectos;
using CaeManager.Application.Tests.Reportes;
using CaeManager.Application.Tests.TiposDocumento;
using CaeManager.Domain.Common;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Trabajadores;
using FluentAssertions;

namespace CaeManager.Application.Tests.Alertas;

public class AlertasDeEmpresaTests
{
    private readonly ConfiguracionQueryContextFalso _configuracion = new();
    private readonly DocumentosQueryContextFalso _documentos = new();
    private readonly EmpresasQueryContextFalso _empresas = new();
    private readonly TiposDocumentoQueryContextFalso _tipos = new();
    private readonly TrabajadoresQueryContextFalso _trabajadores = new();
    private readonly Empresa _empresa = new("Empresa propia");
    private readonly TipoDocumento _tipo = new("Seguro", 12, true, 0, AmbitoAplicacion.Empresa);

    public AlertasDeEmpresaTests()
    {
        _configuracion.ListaParametrosSistema.Add(new ParametroSistema(30, 7));
        _empresas.ListaEmpresas.Add(_empresa);
        _tipos.ListaTiposDocumento.Add(_tipo);
    }

    private Documento Agregar(int dias)
    {
        var documento = Documento.DeEmpresa(_empresa.Id, _tipo.Id, DiaDeNegocio.Hoy().AddYears(-1),
            VigenciaDocumento.VenceEl(DiaDeNegocio.Hoy().AddDays(dias)));
        _documentos.ListaDocumentos.Add(documento);
        return documento;
    }

    private ObtenerAlertasQueryHandler Handler(AlcanceDatosServiceFalso? alcance = null)
    {
        var centros = new CentrosQueryContextFalso();
        var asignaciones = new AsignacionesQueryContextFalso();
        var proyectos = new ProyectosQueryContextFalso();
        return new(_configuracion, _documentos, _tipos, _trabajadores, asignaciones,
            centros, _empresas, new ResolverClientePrincipalService(asignaciones, centros, _empresas),
            alcance ?? new AlcanceDatosServiceFalso(), new DocumentosFaltantesService(_tipos, _documentos, centros),
            proyectos, proyectos);
    }

    [Theory]
    [InlineData(-1, EstadoDocumento.Vencido)]
    [InlineData(3, EstadoDocumento.Urgente)]
    [InlineData(20, EstadoDocumento.Proximo)]
    [InlineData(30, EstadoDocumento.Proximo)]
    public async Task El_vencimiento_de_Empresa_sin_Trabajadores_ni_Centros_genera_alerta_y_trabajo(int dias, EstadoDocumento estado)
    {
        var documento = Agregar(dias);

        var alertas = await Handler().Handle(new ObtenerAlertasQuery(), CancellationToken.None);

        var alerta = alertas.Should().ContainSingle().Subject;
        alerta.Estado.Should().Be(estado);
        alerta.DocumentoId.Should().Be(documento.Id);
        alerta.EmpresaId.Should().Be(_empresa.Id);
        alerta.EmpresaNombre.Should().Be(_empresa.RazonSocial);
        alerta.PropietarioNombre.Should().Be(_empresa.RazonSocial);
        alerta.TrabajadorNombre.Should().BeEmpty();
        alerta.TrabajadorId.Should().BeNull();
        alerta.CentroId.Should().BeNull();
        alerta.ClienteId.Should().BeNull();

        var cola = ObtenerBandejaGestorQueryHandler.Fusionar(alertas, [], [], [], [], [], [], DiaDeNegocio.Hoy(), 48, 24);
        var proximos = ObtenerMiTrabajoAgregadoQueryHandler.MapearProximos(alertas);
        var item = cola.Concat(proximos).Should().ContainSingle().Subject;
        item.EmpresaId.Should().Be(_empresa.Id);
        item.TrabajadorId.Should().BeNull();
        item.TrabajadorNombre.Should().BeNull();
        item.DocumentoId.Should().Be(documento.Id);
    }

    [Fact]
    public async Task Los_documentos_vigentes_sin_fecha_eliminados_y_sustituidos_no_generan_alerta()
    {
        Agregar(60);
        Agregar(31);
        var eliminado = Agregar(-1);
        eliminado.MarcarComoEliminado(Guid.NewGuid());
        var sustituido = Agregar(-1);
        sustituido.SustituirPor(Agregar(60), MotivoSustitucionDocumento.Renovacion, DateTime.UtcNow);
        _documentos.ListaDocumentos.Add(Documento.DeEmpresa(_empresa.Id, _tipo.Id, DiaDeNegocio.Hoy(), VigenciaDocumento.NoCaduca));

        var alertas = await Handler().Handle(new ObtenerAlertasQuery(), CancellationToken.None);

        alertas.Should().BeEmpty();
    }

    [Fact]
    public async Task La_vigencia_de_un_Trabajador_lleva_el_nombre_de_su_Empresa_y_sin_Empresa_visible_ninguno()
    {
        var hoy = DiaDeNegocio.Hoy();
        var propio = Trabajador.DeEmpresa(_empresa.Id, "Ana", "Alba", "60005301A");
        var deSubcontrata = Trabajador.DeSubcontrata(Guid.NewGuid(), "Bea", "Bueno", "60005302G");
        var deEmpresaQueNoSeLee = Trabajador.DeEmpresa(Guid.NewGuid(), "Carla", "Cano", "60005303M");
        _trabajadores.ListaTrabajadores.AddRange([propio, deSubcontrata, deEmpresaQueNoSeLee]);
        var formacion = new TipoDocumento("Formación", 12, true, 0, AmbitoAplicacion.Trabajador);
        _tipos.ListaTiposDocumento.Add(formacion);
        foreach (var trabajador in new[] { propio, deSubcontrata, deEmpresaQueNoSeLee })
            _documentos.ListaDocumentos.Add(Documento.DeTrabajador(trabajador.Id, formacion.Id, hoy.AddYears(-1), VigenciaDocumento.VenceEl(hoy.AddDays(-1))));
        var deEmpresa = Agregar(3);

        var alertas = await Handler().Handle(new ObtenerAlertasQuery(), CancellationToken.None);

        alertas.Should().HaveCount(4);
        alertas.Take(3).Select(a => (a.TrabajadorId, a.EmpresaId, a.EmpresaNombre, a.Estado)).Should().BeEquivalentTo(new[]
        {
            ((Guid?)propio.Id, (Guid?)_empresa.Id, (string?)_empresa.RazonSocial, EstadoDocumento.Vencido),
            ((Guid?)deSubcontrata.Id, (Guid?)null, (string?)null, EstadoDocumento.Vencido),
            ((Guid?)deEmpresaQueNoSeLee.Id, deEmpresaQueNoSeLee.EmpresaId, (string?)null, EstadoDocumento.Vencido),
        });
        // El empate de gravedad y fecha entre alertas de vigencia lo cierra el Id del documento, no el plan de la consulta.
        alertas.Take(3).Select(a => a.DocumentoId).Should().BeInAscendingOrder();
        alertas[^1].Should().Match<AlertaDto>(a => a.TrabajadorId == null && a.EmpresaId == _empresa.Id
            && a.EmpresaNombre == _empresa.RazonSocial && a.Estado == EstadoDocumento.Urgente && a.DocumentoId == deEmpresa.Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Solo_se_alertan_las_Empresas_que_ya_permite_el_servicio_de_visibilidad(bool visible)
    {
        Agregar(-1);
        var alcance = new AlcanceDatosServiceFalso(tieneAccesoTotal: false,
            empresaIdsVisibles: visible ? [_empresa.Id] : []);

        var alertas = await Handler(alcance).Handle(new ObtenerAlertasQuery(), CancellationToken.None);

        alertas.Should().HaveCount(visible ? 1 : 0);
    }
}
