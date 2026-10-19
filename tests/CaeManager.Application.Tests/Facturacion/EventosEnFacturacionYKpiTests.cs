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
using CaeManager.Application.Tests.Plantillas;
using CaeManager.Domain.Trabajadores;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Facturacion;
using CaeManager.Domain.Proyectos;
using CaeManager.Domain.RelacionesEmpresariales;
using FluentAssertions;

namespace CaeManager.Application.Tests.Facturacion;

/// <summary>
/// Decisión de producto de 2026-10-15 sobre los Proyectos de tipo Evento: no cuentan para la facturación, así que
/// <see cref="ObtenerResumenFacturacionQuery"/> y la estimación de facturación del catálogo de KPI
/// (<see cref="ObtenerCatalogoKpisQueryHandler"/>) los excluyen de los tres conceptos de Proyecto (técnicos,
/// gestiones y días abiertos). Cada test pone una obra y un evento y exige que solo cuente la obra.
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
    private readonly TrabajadoresQueryContextFalso _trabajadores = new();
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

    private async Task<ResumenFacturacionDto> ResumenAsync(int anyo, int mes, DateTime? existenteHastaUtc = null)
    {
        var handler = new ObtenerResumenFacturacionQueryHandler(
            null!, _contextos, _contextos, _contextos, _contextos, _contextos, _trabajadores, null!);
        return (await handler.Handle(new ObtenerResumenFacturacionQuery(_cliente.Id, anyo, mes, existenteHastaUtc), CancellationToken.None))!;
    }

    /// <summary>
    /// Defecto de la serie del Cuadro de dirección: «julio de 2026: 3.963 € de facturación con 0 trabajadores, 0 centros
    /// y 0 gestiones». La facturación valoraba cada mes con las fechas de negocio de los registros (inicio de una
    /// obra, alta de un técnico), que pueden ser anteriores al momento en que se registraron; el resto de la serie
    /// mide por fecha de creación. Un mes anterior a la existencia de los registros salía con importe y sin actividad.
    /// El corte <see cref="ObtenerResumenFacturacionQuery.ExistenteHastaUtc"/> deja fuera lo que aún no existía.
    /// </summary>
    [Fact]
    public async Task Un_mes_anterior_a_la_existencia_de_los_registros_no_se_factura_aunque_sus_fechas_de_negocio_caigan_en_el_mes()
    {
        var obra = Obra(new DateOnly(2026, 3, 10), new DateOnly(2026, 3, 20));
        var tecnico = Trabajador.DeEmpresa(_cliente.Id, "Ana", "Alba", "60005301A");
        _trabajadores.ListaTrabajadores.Add(tecnico);
        Tecnico(obra, new DateOnly(2026, 3, 10), trabajadorId: tecnico.Id);
        Tarifa(ConceptoFacturable.DiaProyectoAbierto, PrecioDia);
        Tarifa(ConceptoFacturable.TecnicoAsignadoProyecto, PrecioTecnico);

        var sinCorte = await ResumenAsync(2026, 3);
        var antesDeExistir = await ResumenAsync(2026, 3, new DateTime(2026, 3, 31, 23, 59, 59, DateTimeKind.Utc));
        var despuesDeExistir = await ResumenAsync(2026, 3, DateTime.UtcNow.AddMinutes(1));

        sinCorte.TotalesPorMoneda.Single().Total.Should().Be(11 * PrecioDia + PrecioTecnico, "sin corte, el mes se valora con lo que hay hoy");
        antesDeExistir.TotalesPorMoneda.Sum(t => t.Total).Should().Be(0m, "ni la obra ni el técnico existían a 31 de marzo de 2026");
        Unidades(antesDeExistir, ConceptoFacturable.DiaProyectoAbierto).Should().Be(0);
        Unidades(antesDeExistir, ConceptoFacturable.TecnicoAsignadoProyecto).Should().Be(0);
        despuesDeExistir.TotalesPorMoneda.Single().Total.Should().Be(sinCorte.TotalesPorMoneda.Single().Total, "con el corte después de la creación nada cambia");
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
    public async Task La_estimacion_de_KPI_no_suma_los_dias_del_evento_ni_aunque_este_cerrado_tarde()
    {
        Evento(new DateOnly(2026, 3, 5), new DateOnly(2026, 3, 8), cierre: new DateOnly(2026, 3, 25));
        Obra(new DateOnly(2026, 3, 10), new DateOnly(2026, 3, 20));
        Tarifa(ConceptoFacturable.DiaProyectoAbierto, PrecioDia);

        var estimacion = await EstimacionDeKpiAsync(InicioMarzo, FinMarzo);

        estimacion.Should().Be(11 * PrecioDia, "solo los 11 días de la obra; ningún importe de evento entra en la estimación");
    }

    [Fact]
    public async Task La_estimacion_de_KPI_no_suma_los_tecnicos_del_evento()
    {
        var obra = Obra(new DateOnly(2026, 3, 10), new DateOnly(2026, 3, 20));
        var evento = Evento(new DateOnly(2026, 3, 5), new DateOnly(2026, 3, 8));
        Tecnico(obra, new DateOnly(2026, 3, 10));
        Tecnico(evento, new DateOnly(2026, 3, 5));
        Tecnico(evento, new DateOnly(2026, 3, 6));
        Tarifa(ConceptoFacturable.TecnicoAsignadoProyecto, PrecioTecnico);

        (await EstimacionDeKpiAsync(InicioMarzo, FinMarzo)).Should().Be(PrecioTecnico, "solo el técnico de la obra");
    }

    [Fact]
    public async Task La_estimacion_de_KPI_no_suma_las_gestiones_del_evento()
    {
        var obra = Obra(new DateOnly(2026, 3, 10), null);
        var evento = Evento(new DateOnly(2026, 3, 5), new DateOnly(2026, 3, 8));
        var tipo = Guid.NewGuid();
        _contextos.Documentos.Add(Documento.DeProyecto(obra.Id, tipo, new DateOnly(2026, 3, 11), VigenciaDocumento.NoCaduca));
        _contextos.Documentos.Add(Documento.DeProyecto(evento.Id, tipo, new DateOnly(2026, 3, 6), VigenciaDocumento.NoCaduca));
        Tarifa(ConceptoFacturable.GestionProyectoRealizada, PrecioGestion);
        var dia = DateOnly.FromDateTime(_contextos.Documentos.Select(d => d.CreadoEnUtc).Max());

        (await EstimacionDeKpiAsync(dia, dia)).Should().Be(PrecioGestion, "solo el documento de la obra");
    }

    [Fact]
    public async Task La_estimacion_de_KPI_cuenta_una_sola_vez_al_tecnico_que_esta_en_dos_obras()
    {
        var obraA = Obra(new DateOnly(2026, 3, 10), new DateOnly(2026, 3, 20));
        var obraB = Obra(new DateOnly(2026, 3, 12), new DateOnly(2026, 3, 22));
        var trabajador = Guid.NewGuid();
        Tecnico(obraA, new DateOnly(2026, 3, 10), trabajadorId: trabajador);
        Tecnico(obraB, new DateOnly(2026, 3, 12), trabajadorId: trabajador);
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
        IQueryable<PlataformaCaeClienteEmpresarial> IEmpresasQueryContext.PlataformasCaeClienteEmpresarial => throw new NotSupportedException();
        IQueryable<AccesoPlataformaCaeEmpresa> IEmpresasQueryContext.AccesosPlataformaCaeEmpresa => throw new NotSupportedException();
        IQueryable<DelegacionEmpresa> IEmpresasQueryContext.DelegacionesEmpresa => throw new NotSupportedException();
        IQueryable<OficioEmpresa> IEmpresasQueryContext.OficiosEmpresa => throw new NotSupportedException();
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
