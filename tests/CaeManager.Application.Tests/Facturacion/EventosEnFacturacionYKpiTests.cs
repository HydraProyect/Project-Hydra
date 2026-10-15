using CaeManager.Application.Centros;
using CaeManager.Application.Dashboard;
using CaeManager.Application.Dashboard.Queries;
using CaeManager.Application.Documentos;
using CaeManager.Application.Empresas;
using CaeManager.Application.Facturacion;
using CaeManager.Application.Facturacion.Queries.ObtenerResumenFacturacion;
using CaeManager.Application.Proyectos;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Integraciones;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Facturacion;
using CaeManager.Domain.Proyectos;
using CaeManager.Domain.RelacionesEmpresariales;
using FluentAssertions;

namespace CaeManager.Application.Tests.Facturacion;

/// <summary>
/// Decisión de producto de 2026-10-15 sobre los Proyectos de tipo Evento: (1) no cuentan para la facturación, así que
/// <see cref="ObtenerResumenFacturacionQuery"/> los excluye de los tres conceptos de Proyecto; (2) en la estimación
/// de facturación del catálogo de KPI (<see cref="ObtenerCatalogoKpisQueryHandler"/>) un evento cuenta como Centro
/// temporal, vigente solo entre su fecha de inicio y su fecha de fin (el último día cuenta), no como obra abierta
/// hasta el cierre. Los dos lados se ejercitan con las mismas filas para que la diferencia sea la del tipo.
/// </summary>
public class EventosEnFacturacionYKpiTests
{
    private const decimal PrecioTecnico = 100m;
    private const decimal PrecioDia = 1m;
    private const decimal PrecioGestion = 1000m;
    private static readonly DateOnly InicioMarzo = new(2026, 3, 1);
    private static readonly DateOnly FinMarzo = new(2026, 3, 31);

    private readonly Empresa _cliente = Empresa.CrearComoCliente("Obras del Sur S.L.", "B12345674", false, null, null);
    private readonly Contextos _contextos = new();
    private readonly Centro _centro;

    public EventosEnFacturacionYKpiTests()
    {
        _centro = new Centro(_cliente.Id, Guid.NewGuid(), "Sede Sevilla");
        _contextos.ListaEmpresas.Add(_cliente);
        _contextos.Centros.Add(_centro);
    }

    private Proyecto Obra(DateOnly inicio, DateOnly? cierre)
    {
        var obra = Proyecto.Crear(_cliente.Id, _centro.Id, $"Obra {Guid.NewGuid():N}", inicio, null, null);
        if (cierre is { } fecha) obra.Cerrar(fecha);
        _contextos.Proyectos.Add(obra);
        return obra;
    }

    private Proyecto Evento(DateOnly inicio, DateOnly fin, DateOnly? cierre = null)
    {
        var evento = Proyecto.Crear(_cliente.Id, _centro.Id, $"Evento {Guid.NewGuid():N}", inicio, fin, null, TipoProyecto.Evento);
        if (cierre is { } fecha) evento.Cerrar(fecha);
        _contextos.Proyectos.Add(evento);
        return evento;
    }

    private void Tecnico(Proyecto proyecto, DateOnly alta, DateOnly? baja = null, Guid? trabajadorId = null)
    {
        var tecnico = new ProyectoTecnico(proyecto.Id, trabajadorId ?? Guid.NewGuid(), alta);
        if (baja is { } fecha) tecnico.DarDeBaja(fecha);
        _contextos.Tecnicos.Add(tecnico);
    }

    private void Tarifa(ConceptoFacturable concepto, decimal precio) =>
        _contextos.Tarifas.Add(TarifaCliente.Crear(_cliente.Id, concepto, precio, "EUR"));

    private async Task<ResumenFacturacionDto> ResumenAsync(int anyo, int mes)
    {
        var handler = new ObtenerResumenFacturacionQueryHandler(
            null!, _contextos, _contextos, _contextos, _contextos, _contextos, null!, null!);
        return (await handler.Handle(new ObtenerResumenFacturacionQuery(_cliente.Id, anyo, mes), CancellationToken.None))!;
    }

    private async Task<decimal> EstimacionDeKpiAsync(DateOnly desde, DateOnly hasta)
    {
        var handler = new ObtenerCatalogoKpisQueryHandler(
            _contextos, null!, null!, _contextos, null!, new AlcanceDatosServiceFalso(), null!, null!, _contextos, _contextos, null!, null!, null!);
        return await handler.CalcularFacturacionEstimadaAsync(PeriodoKpi.DeFechas(desde, hasta), CancellationToken.None);
    }

    private static int Unidades(ResumenFacturacionDto resumen, ConceptoFacturable concepto) =>
        resumen.Lineas.Single(l => l.Concepto == concepto).Unidades;

    [Fact]
    public async Task La_facturacion_cuenta_solo_los_tecnicos_de_la_obra_y_no_los_de_un_evento()
    {
        var obra = Obra(new DateOnly(2026, 3, 10), new DateOnly(2026, 3, 20));
        var evento = Evento(new DateOnly(2026, 3, 5), new DateOnly(2026, 3, 8));
        Tecnico(obra, new DateOnly(2026, 3, 10));
        Tecnico(evento, new DateOnly(2026, 3, 5));
        Tecnico(evento, new DateOnly(2026, 3, 6));
        Tarifa(ConceptoFacturable.TecnicoAsignadoProyecto, PrecioTecnico);

        var resumen = await ResumenAsync(2026, 3);

        Unidades(resumen, ConceptoFacturable.TecnicoAsignadoProyecto).Should().Be(1, "solo el técnico de la obra; los dos del evento no se facturan");
        resumen.TotalesPorMoneda.Single().Total.Should().Be(PrecioTecnico);
    }

    [Fact]
    public async Task La_facturacion_cuenta_los_dias_de_la_obra_y_ninguno_del_evento()
    {
        Obra(new DateOnly(2026, 3, 10), new DateOnly(2026, 3, 20));
        Evento(new DateOnly(2026, 3, 5), new DateOnly(2026, 3, 8));
        Tarifa(ConceptoFacturable.DiaProyectoAbierto, PrecioDia);

        var resumen = await ResumenAsync(2026, 3);

        Unidades(resumen, ConceptoFacturable.DiaProyectoAbierto).Should().Be(11, "del 10 al 20 inclusive, solo la obra; el evento (4 días) no se factura");
    }

    [Fact]
    public async Task La_facturacion_cuenta_las_gestiones_de_la_obra_y_no_las_del_evento()
    {
        var obra = Obra(new DateOnly(2026, 3, 10), null);
        var evento = Evento(new DateOnly(2026, 3, 5), new DateOnly(2026, 3, 8));
        var tipo = Guid.NewGuid();
        _contextos.Documentos.Add(Documento.DeProyecto(obra.Id, tipo, new DateOnly(2026, 3, 11), VigenciaDocumento.NoCaduca));
        _contextos.Documentos.Add(Documento.DeProyecto(evento.Id, tipo, new DateOnly(2026, 3, 6), VigenciaDocumento.NoCaduca));
        _contextos.Documentos.Add(Documento.DeProyecto(evento.Id, tipo, new DateOnly(2026, 3, 7), VigenciaDocumento.NoCaduca));
        Tarifa(ConceptoFacturable.GestionProyectoRealizada, PrecioGestion);
        var creados = _contextos.Documentos.Select(d => d.CreadoEnUtc).Max();

        var resumen = await ResumenAsync(creados.Year, creados.Month);

        Unidades(resumen, ConceptoFacturable.GestionProyectoRealizada).Should().Be(1, "solo el documento de la obra");
    }

    [Fact]
    public async Task La_estimacion_de_KPI_cuenta_los_dias_del_evento_solo_entre_su_inicio_y_su_fin_y_no_hasta_el_cierre()
    {
        // Evento del 5 al 8 de marzo (4 días, el último cuenta) cerrado de forma tardía el 25: como obra abierta serían 21 días.
        Evento(new DateOnly(2026, 3, 5), new DateOnly(2026, 3, 8), cierre: new DateOnly(2026, 3, 25));
        Obra(new DateOnly(2026, 3, 10), new DateOnly(2026, 3, 20));
        Tarifa(ConceptoFacturable.DiaProyectoAbierto, PrecioDia);

        var estimacion = await EstimacionDeKpiAsync(InicioMarzo, FinMarzo);

        estimacion.Should().Be((4 + 11) * PrecioDia, "4 días del evento (5-8, ambos inclusive) + 11 de la obra");
    }

    [Fact]
    public async Task La_estimacion_de_KPI_acorta_el_evento_cerrado_antes_de_su_fin()
    {
        Evento(new DateOnly(2026, 3, 5), new DateOnly(2026, 3, 8), cierre: new DateOnly(2026, 3, 6));
        Tarifa(ConceptoFacturable.DiaProyectoAbierto, PrecioDia);

        var estimacion = await EstimacionDeKpiAsync(InicioMarzo, FinMarzo);

        estimacion.Should().Be(2 * PrecioDia, "el evento terminó al cerrarse: 5 y 6 de marzo");
    }

    [Fact]
    public async Task La_estimacion_de_KPI_no_cuenta_un_evento_terminado_en_un_periodo_posterior()
    {
        Evento(new DateOnly(2026, 3, 5), new DateOnly(2026, 3, 8));
        Tarifa(ConceptoFacturable.DiaProyectoAbierto, PrecioDia);

        var estimacion = await EstimacionDeKpiAsync(new DateOnly(2026, 4, 1), new DateOnly(2026, 4, 30));

        estimacion.Should().Be(0m, "un evento no sigue vigente tras su fin; una obra sin cerrar sí seguiría contando");
    }

    [Fact]
    public async Task La_estimacion_de_KPI_cuenta_el_ultimo_dia_del_evento()
    {
        // El periodo del KPI se resuelve por meses naturales: el evento cruza de marzo a abril y acaba el 2 de abril.
        Evento(new DateOnly(2026, 3, 30), new DateOnly(2026, 4, 2));
        Tarifa(ConceptoFacturable.DiaProyectoAbierto, PrecioDia);

        (await EstimacionDeKpiAsync(InicioMarzo, FinMarzo)).Should().Be(2 * PrecioDia, "30 y 31 de marzo");
        (await EstimacionDeKpiAsync(new DateOnly(2026, 4, 1), new DateOnly(2026, 4, 30))).Should().Be(2 * PrecioDia,
            "1 y 2 de abril: el día de fin cuenta y el 3 ya no");
    }

    [Fact]
    public async Task La_estimacion_de_KPI_cuenta_los_tecnicos_del_evento_solo_mientras_esta_vigente()
    {
        var obra = Obra(new DateOnly(2026, 3, 10), new DateOnly(2026, 3, 20));
        var evento = Evento(new DateOnly(2026, 3, 5), new DateOnly(2026, 3, 8));
        Tecnico(obra, new DateOnly(2026, 3, 10));
        Tecnico(evento, new DateOnly(2026, 3, 5));
        Tarifa(ConceptoFacturable.TecnicoAsignadoProyecto, PrecioTecnico);

        (await EstimacionDeKpiAsync(InicioMarzo, FinMarzo)).Should().Be(2 * PrecioTecnico, "el técnico de la obra y el del evento");
        (await EstimacionDeKpiAsync(new DateOnly(2026, 4, 1), new DateOnly(2026, 4, 30))).Should().Be(PrecioTecnico,
            "en abril el evento ya no está vigente aunque su técnico siga con alta sin baja; el de la obra sí cuenta");
    }

    [Fact]
    public async Task La_estimacion_de_KPI_no_cuenta_el_tecnico_dado_de_alta_tras_el_fin_del_evento()
    {
        var evento = Evento(new DateOnly(2026, 3, 5), new DateOnly(2026, 3, 8));
        Tecnico(evento, new DateOnly(2026, 3, 20));
        Tarifa(ConceptoFacturable.TecnicoAsignadoProyecto, PrecioTecnico);

        (await EstimacionDeKpiAsync(InicioMarzo, FinMarzo)).Should().Be(0m, "el alta es posterior al fin del evento: nunca coincidió con él");
    }

    [Fact]
    public async Task La_estimacion_de_KPI_cuenta_una_sola_vez_al_tecnico_que_esta_en_un_evento_y_en_una_obra()
    {
        var obra = Obra(new DateOnly(2026, 3, 10), new DateOnly(2026, 3, 20));
        var evento = Evento(new DateOnly(2026, 3, 5), new DateOnly(2026, 3, 8));
        var trabajador = Guid.NewGuid();
        Tecnico(obra, new DateOnly(2026, 3, 10), trabajadorId: trabajador);
        Tecnico(evento, new DateOnly(2026, 3, 5), trabajadorId: trabajador);
        Tarifa(ConceptoFacturable.TecnicoAsignadoProyecto, PrecioTecnico);

        (await EstimacionDeKpiAsync(InicioMarzo, FinMarzo)).Should().Be(PrecioTecnico, "técnicos únicos por cliente");
    }

    private sealed class Contextos :
        IEmpresasQueryContext, ICentrosQueryContext, IFacturacionQueryContext, IProyectosQueryContext, IDocumentosQueryContext
    {
        public List<Empresa> ListaEmpresas { get; } = [];
        public List<Centro> Centros { get; } = [];
        public List<TarifaCliente> Tarifas { get; } = [];
        public List<Proyecto> Proyectos { get; } = [];
        public List<ProyectoTecnico> Tecnicos { get; } = [];
        public List<Documento> Documentos { get; } = [];

        IQueryable<Empresa> IEmpresasQueryContext.Empresas => new TestAsyncQueryable<Empresa>(ListaEmpresas.AsQueryable());
        IQueryable<Centro> ICentrosQueryContext.Centros => new TestAsyncQueryable<Centro>(Centros.AsQueryable());
        IQueryable<TarifaCliente> IFacturacionQueryContext.TarifasCliente => new TestAsyncQueryable<TarifaCliente>(Tarifas.AsQueryable());
        IQueryable<Proyecto> IProyectosQueryContext.Proyectos => new TestAsyncQueryable<Proyecto>(Proyectos.AsQueryable());
        IQueryable<ProyectoTecnico> IProyectosQueryContext.ProyectosTecnicos => new TestAsyncQueryable<ProyectoTecnico>(Tecnicos.AsQueryable());
        IQueryable<Documento> IDocumentosQueryContext.Documentos => new TestAsyncQueryable<Documento>(Documentos.AsQueryable());

        IQueryable<CredencialAccesoEmpresa> IEmpresasQueryContext.CredencialesAccesoEmpresa => throw new NotSupportedException();
        IQueryable<RelacionEmpresarial> IEmpresasQueryContext.RelacionesEmpresariales => throw new NotSupportedException();
        IQueryable<CanalGestionDocumental> ICentrosQueryContext.CanalesGestionDocumental => throw new NotSupportedException();
        IQueryable<RevisionIaDocumento> IDocumentosQueryContext.RevisionesIaDocumento => throw new NotSupportedException();
        IQueryable<AprobacionDocumento> IDocumentosQueryContext.AprobacionesDocumento => throw new NotSupportedException();
        IQueryable<VerificacionDocumentoOficial> IDocumentosQueryContext.VerificacionesDocumentoOficial => throw new NotSupportedException();
        IQueryable<FirmaDigitalDocumento> IDocumentosQueryContext.FirmasDigitalesDocumento => throw new NotSupportedException();
        IQueryable<FirmaEnCampoDocumento> IDocumentosQueryContext.FirmasEnCampoDocumento => throw new NotSupportedException();
        IQueryable<FirmaGuardadaUsuario> IDocumentosQueryContext.FirmasGuardadasUsuario => throw new NotSupportedException();
        IQueryable<SelloEmpresa> IDocumentosQueryContext.SellosEmpresa => throw new NotSupportedException();
        IQueryable<AcreditacionDocumentoPlataforma> IDocumentosQueryContext.AcreditacionesDocumentoPlataforma => throw new NotSupportedException();
        IQueryable<RechazoAcreditacionDocumentoPlataforma> IDocumentosQueryContext.RechazosAcreditacionDocumentoPlataforma => throw new NotSupportedException();
    }
}
