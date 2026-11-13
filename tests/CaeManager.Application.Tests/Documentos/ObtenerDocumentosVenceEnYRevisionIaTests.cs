using CaeManager.Application.Common;
using CaeManager.Application.Configuracion;
using CaeManager.Application.Documentos.Queries.ObtenerDocumentos;
using CaeManager.Application.Documentos.Queries.ObtenerRevisionesIaPendientes;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Integraciones;
using CaeManager.Application.Tests.Plantillas;
using CaeManager.Application.Tests.Proyectos;
using CaeManager.Application.Tests.TiposDocumento;
using CaeManager.Application.Vehiculos;
using CaeManager.Domain.Common;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using FluentAssertions;

namespace CaeManager.Application.Tests.Documentos;

/// <summary>
/// Los filtros «Vence en» (D-21) y «Pendiente de revisión IA» del listado de Documentos. «Vence en» es el rango
/// hoy..hoy+N con el reloj de negocio (la misma definición que «Por vencer»: con fecha, desde hoy); «Pendiente de revisión IA»
/// es la cola de Revisión IA (<see cref="ColaRevisionIa"/>) y solo se atiende a los roles que acceden a esa pestaña. Observa
/// el handler con contextos en memoria: no la traducción a SQL, que solo ve la suite de integración.
/// </summary>
public class ObtenerDocumentosVenceEnYRevisionIaTests
{
    private static readonly DateTimeOffset AhoraFijo = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Hoy = new(2026, 11, 16);

    private sealed class RelojFijo(DateTimeOffset ahora) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => ahora;
    }

    private sealed class ConfiguracionFalsa : IConfiguracionQueryContext
    {
        public IQueryable<ParametroSistema> ParametrosSistema => new TestAsyncQueryable<ParametroSistema>(new[] { new ParametroSistema(30, 7) }.AsQueryable());
        public IQueryable<PreferenciaDashboardUsuario> PreferenciasDashboardUsuario => Array.Empty<PreferenciaDashboardUsuario>().AsQueryable();
        public IQueryable<FiltroGuardado> FiltrosGuardados => Array.Empty<FiltroGuardado>().AsQueryable();
        public IQueryable<OrdenCajasFicha> OrdenesCajasFicha => Array.Empty<OrdenCajasFicha>().AsQueryable();
        public IQueryable<EstadoAutomatizacion> EstadosAutomatizacion => Array.Empty<EstadoAutomatizacion>().AsQueryable();
    }

    private sealed class VehiculosFalsos : IVehiculosQueryContext
    {
        public IQueryable<CaeManager.Domain.Vehiculos.Vehiculo> Vehiculos =>
            new TestAsyncQueryable<CaeManager.Domain.Vehiculos.Vehiculo>(Array.Empty<CaeManager.Domain.Vehiculos.Vehiculo>().AsQueryable());
    }

    private sealed class Escenario
    {
        public DocumentosQueryContextFalso Documentos { get; } = new();
        public EmpresasQueryContextFalso Empresas { get; } = new();
        public TrabajadoresQueryContextFalso Trabajadores { get; } = new();
        public ProyectosQueryContextFalso Proyectos { get; } = new();
        public TiposDocumentoQueryContextFalso Tipos { get; } = new();
        public CentrosQueryContextFalso Centros { get; } = new();
        public ProveedoresPlataformaCaeQueryContextFalso Proveedores { get; } = new();
        public TipoDocumento Tipo { get; } = new("Certificado", null, false, 1, AmbitoAplicacion.Empresa);
        public Empresa Contratista { get; } = new("Contratista");

        public Escenario()
        {
            Tipos.ListaTiposDocumento.Add(Tipo);
            Empresas.ListaEmpresas.Add(Contratista);
        }

        public ObtenerDocumentosQueryHandler Handler(string? rol = "Administrador", AlcanceDatosServiceFalso? alcance = null) => new(
            new ConfiguracionFalsa(), Documentos, Empresas, Proyectos, Tipos, Trabajadores, new VehiculosFalsos(),
            alcance ?? new(), Centros, Proveedores, new CurrentUserServiceFalso(rol: rol));

        public Documento ConDocumento(VigenciaDocumento vigencia)
        {
            var documento = Documento.DeEmpresa(Contratista.Id, Tipo.Id, new(2026, 1, 1), vigencia);
            Documentos.ListaDocumentos.Add(documento);
            return documento;
        }

        public Documento ConVencimientoEl(DateOnly fecha) => ConDocumento(VigenciaDocumento.VenceEl(fecha));

        public void EnRevisionIa(Documento documento, bool resuelta = false)
        {
            var revision = RevisionIaDocumento.Crear(documento.Id, 60, null, null, null, null, "Revisar");
            if (resuelta) revision.Resolver();
            Documentos.ListaRevisionesIaDocumento.Add(revision);
        }
    }

    private static ObtenerDocumentosQuery Consulta(int? venceEnDias = null, bool revisionIa = false, DateOnly? hasta = null) =>
        new(null, null, null, TamanoPagina: 100, FechaVencimientoHasta: hasta, VenceEnDias: venceEnDias, PendienteDeRevisionIa: revisionIa);

    private static async Task<List<Guid>> IdsAsync(ObtenerDocumentosQueryHandler handler, ObtenerDocumentosQuery consulta) =>
        (await handler.Handle(consulta, default)).Elementos.Select(x => x.Id).ToList();

    // ------------------------------------------------------------- «Vence en»

    [Fact]
    public async Task Vence_en_N_dias_cubre_desde_hoy_hasta_hoy_mas_N_ambos_incluidos()
    {
        using var _ = DiaDeNegocio.FijarRelojEnEsteFlujo(new RelojFijo(AhoraFijo));
        var e = new Escenario();
        var ayer = e.ConVencimientoEl(Hoy.AddDays(-1));
        var hoy = e.ConVencimientoEl(Hoy);
        var dentroDeLaVentana = e.ConVencimientoEl(Hoy.AddDays(15));
        var ultimoDia = e.ConVencimientoEl(Hoy.AddDays(30));
        var primerDiaFuera = e.ConVencimientoEl(Hoy.AddDays(31));
        var sinFecha = e.ConDocumento(VigenciaDocumento.NoCaduca);
        var sinConfirmar = e.ConDocumento(VigenciaDocumento.SinConfirmar);

        var ids = await IdsAsync(e.Handler(), Consulta(venceEnDias: 30));

        ids.Should().BeEquivalentTo([hoy.Id, dentroDeLaVentana.Id, ultimoDia.Id],
            "el rango es hoy..hoy+30: ni lo vencido ayer, ni hoy+31, ni lo que no tiene fecha de vencimiento");
        ids.Should().NotContain([ayer.Id, primerDiaFuera.Id, sinFecha.Id, sinConfirmar.Id]);
    }

    [Fact]
    public async Task Vence_en_usa_el_reloj_de_negocio_y_no_una_fecha_guardada()
    {
        var e = new Escenario();
        var enSiete = e.ConVencimientoEl(Hoy.AddDays(7));
        var enOcho = e.ConVencimientoEl(Hoy.AddDays(8));

        using (DiaDeNegocio.FijarRelojEnEsteFlujo(new RelojFijo(AhoraFijo)))
            (await IdsAsync(e.Handler(), Consulta(venceEnDias: 7))).Should().Equal(enSiete.Id);

        // Un día después, los mismos 7 días ya llegan al otro Documento: «30 días desde hoy», no «hasta el 23-11».
        using (DiaDeNegocio.FijarRelojEnEsteFlujo(new RelojFijo(AhoraFijo.AddDays(1))))
            (await IdsAsync(e.Handler(), Consulta(venceEnDias: 7))).Should().BeEquivalentTo([enSiete.Id, enOcho.Id]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task Vence_en_cero_o_negativo_no_filtra(int dias)
    {
        using var _ = DiaDeNegocio.FijarRelojEnEsteFlujo(new RelojFijo(AhoraFijo));
        var e = new Escenario();
        var vencido = e.ConVencimientoEl(Hoy.AddDays(-10));
        var lejano = e.ConVencimientoEl(Hoy.AddDays(400));
        var sinFecha = e.ConDocumento(VigenciaDocumento.NoCaduca);

        var ids = await IdsAsync(e.Handler(), Consulta(venceEnDias: dias));

        ids.Should().BeEquivalentTo([vencido.Id, lejano.Id, sinFecha.Id]);
    }

    [Fact]
    public async Task Con_Vence_en_y_una_fecha_limite_manda_la_mas_estricta()
    {
        using var _ = DiaDeNegocio.FijarRelojEnEsteFlujo(new RelojFijo(AhoraFijo));
        var e = new Escenario();
        var en5 = e.ConVencimientoEl(Hoy.AddDays(5));
        var en20 = e.ConVencimientoEl(Hoy.AddDays(20));
        var en60 = e.ConVencimientoEl(Hoy.AddDays(60));

        (await IdsAsync(e.Handler(), Consulta(venceEnDias: 30, hasta: Hoy.AddDays(10)))).Should().Equal(en5.Id);
        (await IdsAsync(e.Handler(), Consulta(venceEnDias: 30, hasta: Hoy.AddDays(90)))).Should().BeEquivalentTo([en5.Id, en20.Id]);
        (await IdsAsync(e.Handler(), Consulta(hasta: Hoy.AddDays(90)))).Should().BeEquivalentTo([en5.Id, en20.Id, en60.Id],
            "control: sin «Vence en» la ventana sigue siendo la de siempre");
    }

    [Fact]
    public async Task Un_Vence_en_desmesurado_se_acota_y_no_desborda_la_fecha()
    {
        using var _ = DiaDeNegocio.FijarRelojEnEsteFlujo(new RelojFijo(AhoraFijo));
        var e = new Escenario();
        var cercano = e.ConVencimientoEl(Hoy.AddDays(3));

        var ids = await IdsAsync(e.Handler(), Consulta(venceEnDias: int.MaxValue));

        ids.Should().Equal(cercano.Id);
    }

    // ------------------------------------------------------------- «Pendiente de revisión IA»

    [Fact]
    public async Task Pendiente_de_revision_IA_deja_los_documentos_de_la_cola_y_no_los_resueltos()
    {
        var e = new Escenario();
        var pendiente = e.ConVencimientoEl(Hoy.AddDays(100));
        var resuelta = e.ConVencimientoEl(Hoy.AddDays(100));
        var sinRevision = e.ConVencimientoEl(Hoy.AddDays(100));
        e.EnRevisionIa(pendiente);
        e.EnRevisionIa(resuelta, resuelta: true);

        var ids = await IdsAsync(e.Handler(), Consulta(revisionIa: true));

        ids.Should().Equal(pendiente.Id);
        (await IdsAsync(e.Handler(), Consulta())).Should().BeEquivalentTo([pendiente.Id, resuelta.Id, sinRevision.Id],
            "control: sin el filtro salen todos");
    }

    /// <summary>
    /// La cola y el filtro leen la misma consulta (<see cref="ColaRevisionIa"/>): el filtro devuelve exactamente los
    /// Documentos de la cola. La cola enseña además revisiones de Documentos ya sustituidos (análisis tardío de un
    /// histórico) y el listado solo enseña operativos: esa es la única diferencia, y está fijada aquí.
    /// </summary>
    [Fact]
    public async Task El_filtro_da_los_mismos_documentos_que_la_cola_salvo_los_sustituidos_que_el_listado_no_enseña()
    {
        var e = new Escenario();
        var uno = e.ConVencimientoEl(Hoy.AddDays(100));
        var dos = e.ConVencimientoEl(Hoy.AddDays(100));
        var historico = e.ConVencimientoEl(Hoy.AddDays(100));
        var nuevo = e.ConVencimientoEl(Hoy.AddDays(300));
        historico.SustituirPor(nuevo, MotivoSustitucionDocumento.Renovacion, DateTime.UtcNow);
        e.EnRevisionIa(uno);
        e.EnRevisionIa(dos);
        e.EnRevisionIa(dos);
        e.EnRevisionIa(historico);

        var enLaCola = ColaRevisionIa.DocumentoIds(e.Documentos, null).Distinct().ToList();
        var enElFiltro = await IdsAsync(e.Handler(), Consulta(revisionIa: true));

        enLaCola.Should().BeEquivalentTo([uno.Id, dos.Id, historico.Id]);
        enElFiltro.Should().BeEquivalentTo(enLaCola.Where(id => id != historico.Id),
            "el filtro es la cola sin los Documentos sustituidos, que el listado nunca enseña");
        ColaRevisionIa.Pendientes(e.Documentos, null).Count().Should().Be(4, "la cola cuenta revisiones, el listado Documentos");
    }

    [Fact]
    public void La_cola_acota_los_documentos_de_trabajador_a_la_cartera_y_los_de_empresa_son_de_vision_total()
    {
        var e = new Escenario();
        var trabajadorVisible = Guid.NewGuid();
        var trabajadorAjeno = Guid.NewGuid();
        var deVisible = Documento.DeTrabajador(trabajadorVisible, e.Tipo.Id, new(2026, 1, 1), VigenciaDocumento.NoCaduca);
        var deAjeno = Documento.DeTrabajador(trabajadorAjeno, e.Tipo.Id, new(2026, 1, 1), VigenciaDocumento.NoCaduca);
        var deEmpresa = e.ConDocumento(VigenciaDocumento.NoCaduca);
        e.Documentos.ListaDocumentos.AddRange([deVisible, deAjeno]);
        foreach (var documento in new[] { deVisible, deAjeno, deEmpresa }) e.EnRevisionIa(documento);

        ColaRevisionIa.DocumentoIds(e.Documentos, null).Should().BeEquivalentTo([deVisible.Id, deAjeno.Id, deEmpresa.Id]);
        ColaRevisionIa.DocumentoIds(e.Documentos, [trabajadorVisible]).Should().Equal(deVisible.Id);
        ColaRevisionIa.DocumentoIds(e.Documentos, []).Should().BeEmpty("una cartera vacía no ve nada, ni lo de Empresa");
    }

    /// <summary>
    /// El filtro usa el alcance de la cola (cartera por Trabajador), no solo el del listado: un Gestor CAE con cartera
    /// acotada ve en el listado el Documento de Empresa de su cartera, pero la cola solo es de visión total para los de
    /// Empresa, así que con «Pendiente de revisión IA» ese Documento no sale.
    /// </summary>
    [Fact]
    public async Task El_filtro_aplica_el_alcance_de_cartera_de_la_cola_en_el_handler()
    {
        var e = new Escenario();
        var deEmpresa = e.ConVencimientoEl(Hoy.AddDays(100));
        e.EnRevisionIa(deEmpresa);
        var cartera = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, trabajadorIdsVisibles: [Guid.NewGuid()],
            empresaIdsVisibles: [e.Contratista.Id]);

        (await IdsAsync(e.Handler("GestorCae", cartera), Consulta())).Should().Equal(
            [deEmpresa.Id], "control: el Documento está en el listado de ese Gestor CAE");
        (await IdsAsync(e.Handler("GestorCae", cartera), Consulta(revisionIa: true))).Should().BeEmpty(
            "la cola no le enseña revisiones de Empresa con una cartera acotada");
        (await IdsAsync(e.Handler("GestorCae"), Consulta(revisionIa: true))).Should().Equal(
            [deEmpresa.Id], "control: con visión total sí");
    }

    [Theory]
    [InlineData("Administrador", true)]
    [InlineData("DireccionCae", true)]
    [InlineData("CoordinadorCae", true)]
    [InlineData("GestorCae", true)]
    [InlineData("Consulta", false)]
    [InlineData("Cliente", false)]
    [InlineData("Inventado", false)]
    [InlineData(null, false)]
    public async Task El_filtro_solo_se_atiende_a_los_roles_que_acceden_a_la_pestana_Revision_IA(string? rol, bool seAtiende)
    {
        var e = new Escenario();
        var pendiente = e.ConVencimientoEl(Hoy.AddDays(100));
        var otro = e.ConVencimientoEl(Hoy.AddDays(100));
        e.EnRevisionIa(pendiente);

        var ids = await IdsAsync(e.Handler(rol), Consulta(revisionIa: true));

        if (seAtiende)
            ids.Should().Equal(pendiente.Id);
        else
            ids.Should().BeEquivalentTo([pendiente.Id, otro.Id], "con otro rol el filtro se ignora: ni oráculo de la cola ni listado vacío");
    }

    [Fact]
    public async Task Sin_servicio_de_usuario_el_filtro_de_revision_IA_se_ignora_y_falla_cerrado()
    {
        var e = new Escenario();
        var pendiente = e.ConVencimientoEl(Hoy.AddDays(100));
        var otro = e.ConVencimientoEl(Hoy.AddDays(100));
        e.EnRevisionIa(pendiente);
        var sinUsuario = new ObtenerDocumentosQueryHandler(
            new ConfiguracionFalsa(), e.Documentos, e.Empresas, e.Proyectos, e.Tipos, e.Trabajadores, new VehiculosFalsos(),
            new AlcanceDatosServiceFalso(), e.Centros, e.Proveedores);

        (await IdsAsync(sinUsuario, Consulta(revisionIa: true))).Should().BeEquivalentTo([pendiente.Id, otro.Id]);
    }

    [Fact]
    public async Task Vence_en_y_Pendiente_de_revision_IA_se_combinan_con_Y()
    {
        using var _ = DiaDeNegocio.FijarRelojEnEsteFlujo(new RelojFijo(AhoraFijo));
        var e = new Escenario();
        var ambos = e.ConVencimientoEl(Hoy.AddDays(10));
        var soloVence = e.ConVencimientoEl(Hoy.AddDays(10));
        var soloRevision = e.ConVencimientoEl(Hoy.AddDays(200));
        e.EnRevisionIa(ambos);
        e.EnRevisionIa(soloRevision);

        (await IdsAsync(e.Handler(), Consulta(venceEnDias: 30, revisionIa: true))).Should().Equal(ambos.Id);
        (await IdsAsync(e.Handler(), Consulta(venceEnDias: 30))).Should().BeEquivalentTo([ambos.Id, soloVence.Id]);
    }
}
