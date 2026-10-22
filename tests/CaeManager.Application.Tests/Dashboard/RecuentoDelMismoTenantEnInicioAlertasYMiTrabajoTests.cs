using CaeManager.Application.Alertas;
using CaeManager.Application.Alertas.Queries.ObtenerAlertas;
using CaeManager.Application.Asignaciones;
using CaeManager.Application.Bandeja.Queries.ObtenerBandejaGestor;
using CaeManager.Application.Centros;
using CaeManager.Application.Clientes;
using CaeManager.Application.Dashboard.Queries;
using CaeManager.Application.Documentos;
using CaeManager.Application.Tenants.Queries.ObtenerClientesAutorizados;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Documentos;
using CaeManager.Application.Tests.Integraciones;
using CaeManager.Application.Tests.Proyectos;
using CaeManager.Application.Tests.Reportes;
using CaeManager.Application.Visitas;
using CaeManager.Domain.Common;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Visitas;
using FluentAssertions;

namespace CaeManager.Application.Tests.Dashboard;

/// <summary>
/// Un mismo Tenant beneficiario da el mismo recuento en las tres pantallas que lo enseñan, sobre la misma siembra: la fila de
/// la Empresa gestionada en Inicio (y Visión de cartera), Alertas y la cola de Mi trabajo. La siembra lleva a propósito los
/// casos en que Inicio contaba otra cosa: un documento vencido de Empresa (Alertas y Mi trabajo lo cuentan) y uno de un
/// Trabajador que ya no existe (ninguna fila lo explica en Alertas ni en Mi trabajo).
/// </summary>
public class RecuentoDelMismoTenantEnInicioAlertasYMiTrabajoTests
{
    [Fact]
    public async Task Inicio_Alertas_y_Mi_trabajo_dan_los_mismos_vencidos_y_por_vencer_del_Tenant()
    {
        var hoy = DiaDeNegocio.Hoy();
        var mundo = new MundoAcreditaciones();
        var empresas = new EmpresasQueryContextFalso();
        var empresa = new Empresa("Empresa gestionada");
        empresas.ListaEmpresas.Add(empresa);

        var formacion = mundo.Tipo(RequisitoDocumental.Si, nombre: "Formación PRL");
        var reconocimiento = mundo.Tipo(RequisitoDocumental.Si, nombre: "Reconocimiento médico");
        var seguro = mundo.Tipo(RequisitoDocumental.Si, AmbitoAplicacion.Empresa, "Seguro de responsabilidad civil");
        var trabajador = mundo.Trabajador();

        Agregar(mundo, Documento.DeTrabajador(trabajador.Id, formacion.Id, hoy.AddYears(-1), VigenciaDocumento.VenceEl(hoy.AddDays(-2))));
        Agregar(mundo, Documento.DeTrabajador(trabajador.Id, reconocimiento.Id, hoy.AddYears(-1), VigenciaDocumento.VenceEl(hoy.AddDays(3))));
        Agregar(mundo, Documento.DeEmpresa(empresa.Id, seguro.Id, hoy.AddYears(-1), VigenciaDocumento.VenceEl(hoy.AddDays(-1))));
        // De un Trabajador que ya no existe: ni Alertas ni Mi trabajo tienen fila que lo explique, así que Inicio tampoco lo cuenta.
        Agregar(mundo, Documento.DeTrabajador(Guid.NewGuid(), formacion.Id, hoy.AddYears(-1), VigenciaDocumento.VenceEl(hoy.AddDays(-5))));

        var configuracion = new ConfiguracionQueryContextFalso();
        configuracion.ListaParametrosSistema.Add(new ParametroSistema(30, 7));
        var proyectos = new ProyectosQueryContextFalso();
        var alcance = new AlcanceDatosServiceFalso();

        // Alertas.
        var alertas = await new ObtenerAlertasQueryHandler(configuracion, mundo.DocumentosContexto, mundo.TiposDocumentoContexto,
                mundo.TrabajadoresContexto, mundo.AsignacionesContexto, mundo.CentrosContexto, empresas,
                new ResolverClientePrincipalService(mundo.AsignacionesContexto, mundo.CentrosContexto, empresas), alcance,
                new DocumentosFaltantesService(mundo.TiposDocumentoContexto, mundo.DocumentosContexto, mundo.CentrosContexto),
                proyectos, proyectos)
            .Handle(new ObtenerAlertasQuery(), CancellationToken.None);

        // Mi trabajo: su cola se construye con esas mismas alertas.
        var cola = ObtenerBandejaGestorQueryHandler.Fusionar(alertas, [], [], [], [], [], [], hoy, 24, 4);

        // Inicio: la fila de la Empresa gestionada sale de los KPI del Tenant, igual que en Visión de cartera.
        var calculo = new CalculoEstadoCentroService(mundo.CentrosContexto, mundo.DocumentosContexto,
            mundo.TiposDocumentoContexto, mundo.TrabajadoresContexto, mundo.AsignacionesContexto,
            configuracion, empresas, proyectos, proyectos);
        var acceso = new EvaluacionDeAccesoPorCentroService(mundo.CentrosContexto, mundo.TiposDocumentoContexto,
            mundo.TrabajadoresContexto, mundo.AsignacionesContexto, mundo.DocumentosContexto, alcance);
        var kpis = await new ObtenerKpisDashboardQueryHandler(mundo.CentrosContexto, configuracion, mundo.DocumentosContexto,
                mundo.TrabajadoresContexto, new VisitasVacias(), alcance, calculo, acceso, empresas, mundo.TiposDocumentoContexto, proyectos)
            .Handle(new ObtenerKpisDashboardQuery(), CancellationToken.None);
        var tenant = new ClienteAutorizadoDto(Guid.NewGuid(), "Empresa gestionada", EsOrigen: false);
        var fila = ObtenerKpisGlobalesQueryHandler.Fusionar([(tenant, kpis)]).ClientesConMasRiesgo.Single();

        // Control de la siembra: dos vencidos (Trabajador y Empresa) y uno por vencer.
        alertas.Count(a => a.Estado == EstadoDocumento.Vencido).Should().Be(2);
        alertas.Count(a => a.Estado == EstadoDocumento.Urgente).Should().Be(1);

        fila.DocumentosVencidos.Should().Be(alertas.Count(a => a.Estado == EstadoDocumento.Vencido), "Inicio frente a Alertas");
        fila.DocumentosVencidos.Should().Be(cola.Count(i => i.Tipo == TipoItemBandeja.Vencido), "Inicio frente a Mi trabajo");
        fila.DocumentosUrgentes.Should().Be(alertas.Count(a => a.Estado == EstadoDocumento.Urgente), "Inicio frente a Alertas");
        fila.DocumentosUrgentes.Should().Be(cola.Count(i => i.Tipo == TipoItemBandeja.Urgente), "Inicio frente a Mi trabajo");

        // La tasa sigue midiendo solo Documentos de Trabajador (el de Empresa no entra) y sin el del Trabajador inexistente.
        kpis.Fraccion.Requeridos.Should().Be(2);
    }

    private static void Agregar(MundoAcreditaciones mundo, Documento documento) => mundo.DocumentosContexto.ListaDocumentos.Add(documento);

    private sealed class VisitasVacias : IVisitasQueryContext
    {
        public IQueryable<Visita> Visitas => new TestAsyncQueryable<Visita>(Array.Empty<Visita>().AsQueryable());
        public IQueryable<VisitaTrabajador> VisitasTrabajadores =>
            new TestAsyncQueryable<VisitaTrabajador>(Array.Empty<VisitaTrabajador>().AsQueryable());
    }
}
