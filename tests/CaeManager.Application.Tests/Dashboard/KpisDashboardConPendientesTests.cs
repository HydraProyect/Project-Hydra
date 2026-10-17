using CaeManager.Application.Centros;
using CaeManager.Application.Dashboard.Queries;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Documentos;
using CaeManager.Application.Tests.Integraciones;
using CaeManager.Application.Tests.Proyectos;
using CaeManager.Application.Tests.Reportes;
using CaeManager.Application.Visitas;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Visitas;
using FluentAssertions;

namespace CaeManager.Application.Tests.Dashboard;

public class KpisDashboardConPendientesTests
{
    [Theory]
    [InlineData(EstadoAcreditacion.PendienteDeSubir, true)]
    [InlineData(EstadoAcreditacion.Subida, true)]
    [InlineData(EstadoAcreditacion.Rechazada, true)]
    [InlineData(EstadoAcreditacion.Aceptada, false)]
    public async Task Inicio_recibe_los_pendientes_del_motor_real_aunque_el_documento_este_al_dia(
        EstadoAcreditacion estado, bool pendientes)
    {
        var mundo = new MundoAcreditaciones();
        var centro = mundo.Centro();
        var trabajador = mundo.Trabajador();
        var tipo = mundo.Tipo(RequisitoDocumental.Si);
        mundo.Requisito(tipo, centro, incluido: true);
        mundo.Asignacion(trabajador, centro);
        var documento = mundo.DocumentoDe(trabajador, tipo);
        var canal = mundo.AccesoPlataforma(centro);
        var acreditacion = new AcreditacionDocumentoPlataforma(documento.Id, canal.Id);
        if (estado == EstadoAcreditacion.Subida) acreditacion.MarcarSubida();
        if (estado == EstadoAcreditacion.Aceptada) acreditacion.MarcarAceptada(VigenciaEnPlataforma.NoVenceAqui);
        if (estado == EstadoAcreditacion.Rechazada)
            acreditacion.Rechazar(CausaRechazoAcreditacion.Ilegible, "Firma ilegible", DateTime.UtcNow);
        mundo.DocumentosContexto.ListaAcreditacionesDocumentoPlataforma.Add(acreditacion);

        var kpis = await Handler(mundo, new AlcanceDatosServiceFalso()).Handle(new ObtenerKpisDashboardQuery(), CancellationToken.None);

        kpis.DocumentosVigentes.Should().Be(1, "control: el documento está al día en TALVEG");
        kpis.TienePendientesDocumentales.Should().Be(pendientes);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Una_Empresa_con_documento_vencido_y_sin_Centros_informa_pendientes_solo_si_es_visible(bool visible)
    {
        var mundo = new MundoAcreditaciones();
        var empresa = new Empresa("Empresa propia");
        var empresas = new EmpresasQueryContextFalso();
        empresas.ListaEmpresas.Add(empresa);
        var tipo = mundo.Tipo(RequisitoDocumental.Si, AmbitoAplicacion.Empresa);
        mundo.DocumentosContexto.ListaDocumentos.Add(Documento.DeEmpresa(empresa.Id, tipo.Id,
            DiaDeNegocio.Hoy().AddYears(-1), VigenciaDocumento.VenceEl(DiaDeNegocio.Hoy().AddDays(-1))));
        var alcance = new AlcanceDatosServiceFalso(tieneAccesoTotal: false,
            empresaIdsVisibles: visible ? [empresa.Id] : []);

        var kpis = await Handler(mundo, alcance, empresas).Handle(new ObtenerKpisDashboardQuery(), CancellationToken.None);

        kpis.Centros.Should().Be(0);
        kpis.TrabajadoresActivos.Should().Be(0);
        kpis.TienePendientesDocumentales.Should().Be(visible);
    }

    [Fact]
    public async Task Los_documentos_vencidos_de_una_Empresa_dada_de_baja_no_son_un_pendiente_sin_fila_en_Mi_trabajo()
    {
        // La Empresa eliminada no sale en el contexto (filtro global), pero sus Documentos siguen en la base: sin la
        // misma exigencia que Alertas, un Administrador vería "hay pendientes" sin ninguna fila que lo explique.
        var mundo = new MundoAcreditaciones();
        var tipo = mundo.Tipo(RequisitoDocumental.Si, AmbitoAplicacion.Empresa);
        mundo.DocumentosContexto.ListaDocumentos.Add(Documento.DeEmpresa(Guid.NewGuid(), tipo.Id,
            DiaDeNegocio.Hoy().AddYears(-1), VigenciaDocumento.VenceEl(DiaDeNegocio.Hoy().AddDays(-1))));

        var kpis = await Handler(mundo, new AlcanceDatosServiceFalso()).Handle(new ObtenerKpisDashboardQuery(), CancellationToken.None);

        kpis.TienePendientesDocumentales.Should().BeFalse();
    }

    private static ObtenerKpisDashboardQueryHandler Handler(MundoAcreditaciones mundo, AlcanceDatosServiceFalso alcance,
        EmpresasQueryContextFalso? empresas = null)
    {
        var configuracion = new ConfiguracionQueryContextFalso();
        configuracion.ListaParametrosSistema.Add(new ParametroSistema(30, 7));
        empresas ??= new EmpresasQueryContextFalso();
        var proyectos = new ProyectosQueryContextFalso();
        var calculo = new CalculoEstadoCentroService(mundo.CentrosContexto, mundo.DocumentosContexto,
            mundo.TiposDocumentoContexto, mundo.TrabajadoresContexto, mundo.AsignacionesContexto,
            configuracion, empresas, proyectos, proyectos);
        var acceso = new EvaluacionDeAccesoPorCentroService(mundo.CentrosContexto, mundo.TiposDocumentoContexto,
            mundo.TrabajadoresContexto, mundo.AsignacionesContexto, mundo.DocumentosContexto, alcance);
        return new ObtenerKpisDashboardQueryHandler(mundo.CentrosContexto, configuracion,
            mundo.DocumentosContexto, mundo.TrabajadoresContexto, new VisitasVacias(), alcance, calculo, acceso,
            empresas, mundo.TiposDocumentoContexto);
    }

    private sealed class VisitasVacias : IVisitasQueryContext
    {
        public IQueryable<Visita> Visitas => new TestAsyncQueryable<Visita>(Array.Empty<Visita>().AsQueryable());
        public IQueryable<VisitaTrabajador> VisitasTrabajadores =>
            new TestAsyncQueryable<VisitaTrabajador>(Array.Empty<VisitaTrabajador>().AsQueryable());
    }
}
