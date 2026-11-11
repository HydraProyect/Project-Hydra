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

    /// <summary>
    /// Próximo es el umbral «todavía no urgente» que la cola de trabajo deja fuera (ObtenerBandejaGestorQuery) y que la tasa
    /// cuenta como al día: con él, Inicio decía «Hay pendientes» bajo «Nada pendiente ahora mismo» (revisión 1:1 del
    /// 2026-11-09). Urgente y Vencido sí son pendientes. Umbrales del arnés: ámbar 30 días, rojo 7.
    /// </summary>
    [Theory]
    [InlineData(10, false)]
    [InlineData(3, true)]
    [InlineData(-1, true)]
    public async Task Un_documento_de_Empresa_es_pendiente_cuando_es_urgente_o_vencido_y_no_cuando_es_proximo(int dias, bool pendiente)
    {
        var mundo = new MundoAcreditaciones();
        var empresa = new Empresa("Talleres Aurora");
        var empresas = new EmpresasQueryContextFalso();
        empresas.ListaEmpresas.Add(empresa);
        var tipo = mundo.Tipo(RequisitoDocumental.Si, AmbitoAplicacion.Empresa);
        mundo.DocumentosContexto.ListaDocumentos.Add(Documento.DeEmpresa(empresa.Id, tipo.Id,
            DiaDeNegocio.Hoy().AddYears(-1), VigenciaDocumento.VenceEl(DiaDeNegocio.Hoy().AddDays(dias))));

        var kpis = await Handler(mundo, new AlcanceDatosServiceFalso(), empresas)
            .Handle(new ObtenerKpisDashboardQuery(), CancellationToken.None);

        (kpis.DocumentosEmpresaProximos + kpis.DocumentosEmpresaUrgentes + kpis.DocumentosEmpresaVencidos)
            .Should().Be(1, "control: el documento entra en la selección de Alertas en el estado que toca");
        kpis.TienePendientesDocumentales.Should().Be(pendiente);
    }

    /// <summary>
    /// El mismo corte en el Centro de Trabajo: un Centro cuyo peor documento exigido está Próximo no es un pendiente; uno
    /// Urgente o Vencido, sí.
    /// </summary>
    [Theory]
    [InlineData(10, false)]
    [InlineData(3, true)]
    [InlineData(-1, true)]
    public async Task Un_Centro_es_pendiente_cuando_su_documentacion_es_urgente_o_vencida_y_no_cuando_es_proxima(int dias, bool pendiente)
    {
        var mundo = new MundoAcreditaciones();
        var centro = mundo.Centro();
        var trabajador = mundo.Trabajador();
        var tipo = mundo.Tipo(RequisitoDocumental.Si);
        mundo.Requisito(tipo, centro, incluido: true);
        mundo.Asignacion(trabajador, centro);
        mundo.DocumentosContexto.ListaDocumentos.Add(Documento.DeTrabajador(trabajador.Id, tipo.Id,
            DiaDeNegocio.Hoy().AddYears(-1), VigenciaDocumento.VenceEl(DiaDeNegocio.Hoy().AddDays(dias))));

        var kpis = await Handler(mundo, new AlcanceDatosServiceFalso()).Handle(new ObtenerKpisDashboardQuery(), CancellationToken.None);

        (kpis.DocumentosProximos + kpis.DocumentosUrgentes + kpis.DocumentosVencidos)
            .Should().Be(1, "control: el documento entra en la tasa en el estado que toca");
        kpis.CentrosBloqueados.Should().Be(0, "control: sin plataforma el Centro no se bloquea");
        kpis.TienePendientesDocumentales.Should().Be(pendiente);
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

    [Theory]
    [InlineData(31)]
    [InlineData(60)]
    public async Task Los_documentos_de_Empresa_vigentes_cuentan_sin_crear_pendientes_ni_entrar_en_la_tasa(int dias)
    {
        var mundo = new MundoAcreditaciones();
        var empresa = new Empresa("Talleres Aurora");
        var empresas = new EmpresasQueryContextFalso();
        empresas.ListaEmpresas.Add(empresa);
        var tipo = mundo.Tipo(RequisitoDocumental.Si, AmbitoAplicacion.Empresa);
        for (var i = 0; i < 2; i++)
            mundo.DocumentosContexto.ListaDocumentos.Add(Documento.DeEmpresa(empresa.Id, tipo.Id,
                DiaDeNegocio.Hoy(), VigenciaDocumento.VenceEl(DiaDeNegocio.Hoy().AddDays(dias))));

        var kpis = await Handler(mundo, new AlcanceDatosServiceFalso(), empresas)
            .Handle(new ObtenerKpisDashboardQuery(), CancellationToken.None);

        kpis.DocumentosEmpresaVigentes.Should().Be(2);
        kpis.DocumentosVigentes.Should().Be(0);
        kpis.Fraccion.Requeridos.Should().Be(0);
        kpis.TienePendientesDocumentales.Should().BeFalse();
        kpis.DocumentosEmpresaProximos.Should().Be(0);
        kpis.DocumentosEmpresaUrgentes.Should().Be(0);
        kpis.DocumentosEmpresaVencidos.Should().Be(0);
    }

    [Fact]
    public async Task Los_vigentes_de_Empresa_excluyen_documentos_eliminados_sustituidos_Empresas_de_baja_y_fuera_de_visibilidad()
    {
        var mundo = new MundoAcreditaciones();
        var empresa = new Empresa("Talleres Aurora");
        var oculta = new Empresa("Montajes Brisa");
        var empresas = new EmpresasQueryContextFalso();
        empresas.ListaEmpresas.AddRange([empresa, oculta]);
        var tipo = mundo.Tipo(RequisitoDocumental.Si, AmbitoAplicacion.Empresa);
        Documento Agregar(Guid empresaId)
        {
            var documento = Documento.DeEmpresa(empresaId, tipo.Id, DiaDeNegocio.Hoy(),
                VigenciaDocumento.VenceEl(DiaDeNegocio.Hoy().AddDays(60)));
            mundo.DocumentosContexto.ListaDocumentos.Add(documento);
            return documento;
        }
        var vigente = Agregar(empresa.Id);
        Agregar(empresa.Id).MarcarComoEliminado(Guid.NewGuid());
        Agregar(empresa.Id).SustituirPor(vigente, MotivoSustitucionDocumento.Renovacion, DateTime.UtcNow);
        // La Empresa dada de baja no está en el contexto por el filtro global; sus Documentos se conservan.
        var empresaDeBajaId = Guid.NewGuid();
        Agregar(empresaDeBajaId);
        Agregar(oculta.Id);
        var alcance = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, empresaIdsVisibles: [empresa.Id, empresaDeBajaId]);

        var kpis = await Handler(mundo, alcance, empresas).Handle(new ObtenerKpisDashboardQuery(), CancellationToken.None);

        kpis.DocumentosEmpresaVigentes.Should().Be(1);
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
            empresas, mundo.TiposDocumentoContexto, proyectos);
    }

    private sealed class VisitasVacias : IVisitasQueryContext
    {
        public IQueryable<Visita> Visitas => new TestAsyncQueryable<Visita>(Array.Empty<Visita>().AsQueryable());
        public IQueryable<VisitaTrabajador> VisitasTrabajadores =>
            new TestAsyncQueryable<VisitaTrabajador>(Array.Empty<VisitaTrabajador>().AsQueryable());
    }
}
