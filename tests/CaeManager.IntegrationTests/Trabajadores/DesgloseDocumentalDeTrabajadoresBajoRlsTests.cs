using System.Data.Common;
using CaeManager.Application.Common;
using CaeManager.Application.Documentos;
using CaeManager.Application.Plataforma;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadores;
using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Operaciones;
using CaeManager.Domain.Subcontratas;
using CaeManager.Domain.Tenants;
using CaeManager.Domain.Trabajadores;
using CaeManager.Infrastructure.Autorizacion;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Interceptors;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace CaeManager.IntegrationTests.Trabajadores;

/// <summary>
/// El desglose documental de la fila de un Trabajador (incidencias y «vigentes / registrados») y el filtro por
/// Centro del listado, leídos <b>como <c>cae_app_runtime</c></b> con los interceptores de sellado y de sesión
/// RLS de producción (mismo arnés que <c>CumplimientoCentroDocumentosDuplicadosBajoRlsTests</c>) y, donde se
/// mide el alcance, con el <see cref="AlcanceDatosService"/> real sobre una Asignación de Cartera acotada a un
/// Cliente empresarial.
///
/// <para>
/// Escenario, en el Tenant propietario de la sesión: «Nora» (plantilla de la Empresa propia, asignada al Centro
/// del Cliente empresarial de la cartera y además a otro que la cartera no alcanza) con un documento vencido,
/// uno urgente, uno sin confirmar, uno vigente, uno eliminado y uno sustituido por su renovación; «Iker»
/// (Subcontrata, solo en el Centro fuera de la cartera) con un vencido; «Ana», de baja en el Centro de la
/// cartera. Otro Tenant propietario tiene un Trabajador gemelo de Nora con dos documentos vencidos. La siembra
/// va como propietario de la base, porque no es lo que se mide.
/// </para>
/// </summary>
public class DesgloseDocumentalDeTrabajadoresBajoRlsTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _gestor = Guid.NewGuid();
    private readonly ContadorDeConsultasDeDesglose _contador = new();
    private CaeManagerDbContext _propietario = null!;
    private CaeManagerDbContext _runtime = null!;
    private CaeManagerDbContext _runtimeDelGestor = null!;
    private TenantActualDeLaPeticion _tenantDeLaPeticion = null!;
    private Guid _tenantSesion;
    private Guid _tenantAjeno;
    private Guid _centroDentro;
    private Guid _centroFuera;
    private Guid _nora;
    private Guid _iker;
    private Guid _ana;
    private Guid _gemeloAjeno;
    private Guid _documentoAjeno;
    private Guid _aptitudVencida;

    public async Task InitializeAsync()
    {
        var tenantPorAmbito = new TenantActualPorAmbito();
        var opcionesPropietario = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantPorAmbito))
            .Options;
        _propietario = new CaeManagerDbContext(opcionesPropietario, new EphemeralDataProtectionProvider(), tenantPorAmbito);
        await _propietario.Database.MigrateAsync();

        var tenantSesion = new Tenant("Tenant propietario de la sesión");
        var tenantAjeno = new Tenant("Tenant propietario ajeno");
        _propietario.Tenants.AddRange(tenantSesion, tenantAjeno);
        await _propietario.SaveChangesAsync();
        _tenantSesion = tenantSesion.Id;
        _tenantAjeno = tenantAjeno.Id;

        await SembrarTenantDeLaSesionAsync();
        await SembrarTenantAjenoAsync();

        _tenantDeLaPeticion = new TenantActualDeLaPeticion(_tenantSesion);
        _runtime = CrearRuntime(new CurrentUserServiceFalso(Guid.NewGuid(), Roles.Administrador, tenantOrigenId: _tenantSesion));
        _runtimeDelGestor = CrearRuntime(UsuarioGestor());
    }

    public async Task DisposeAsync()
    {
        await _runtimeDelGestor.DisposeAsync();
        await _runtime.DisposeAsync();
        await _propietario.DisposeAsync();
        await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);
    }

    /// <summary>Control del instrumento: la conexión de lectura está de verdad bajo RLS, y lo «de fuera» existe.</summary>
    [Fact]
    public async Task La_lectura_va_bajo_RLS_efectiva_y_lo_que_no_debe_contar_existe()
    {
        (await _runtime.Documentos.IgnoreQueryFilters().CountAsync(d => d.Id == _documentoAjeno)).Should().Be(0,
            "sin filtro global de EF, solo RLS puede ocultar el Documento del Tenant ajeno");
        (await _runtime.Trabajadores.IgnoreQueryFilters().CountAsync(t => t.Id == _gemeloAjeno)).Should().Be(0);
        (await _runtime.Documentos.IgnoreQueryFilters().CountAsync()).Should().Be(7,
            "control positivo: los siete documentos del Tenant de la sesión sí se ven (seis de Nora, uno de Iker)");

        // Con la conexión de propietario, lo que el desglose no debe contar está ahí.
        (await _propietario.Documentos.IgnoreQueryFilters().CountAsync(d => d.TrabajadorId == _gemeloAjeno)).Should().Be(2);
        (await _propietario.Documentos.IgnoreQueryFilters().CountAsync(d => d.TrabajadorId == _nora && d.EstaEliminado)).Should().Be(1);
        (await _propietario.Documentos.IgnoreQueryFilters().CountAsync(d => d.TrabajadorId == _nora && d.SustituidoEnUtc != null)).Should().Be(1);
        (await _propietario.Documentos.IgnoreQueryFilters().CountAsync(d => d.TrabajadorId == _iker)).Should().Be(1);
    }

    /// <summary>
    /// (a) y (c): el desglose de Nora no cuenta los documentos del gemelo del otro Tenant, ni el eliminado, ni
    /// el sustituido. Por los dos caminos del handler (con recuentos por estado, que es el de la pantalla, y sin
    /// ellos).
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task El_desglose_cuenta_solo_los_documentos_operativos_del_Tenant_de_la_sesion(bool conRecuentosPorEstado)
    {
        var resultado = await HandlerSinCartera().Handle(
            new ObtenerTrabajadoresQuery(null, ConRecuentosPorEstado: conRecuentosPorEstado), CancellationToken.None);

        resultado.Elementos.Select(t => t.Id).Should().BeEquivalentTo([_nora, _iker, _ana],
            "el gemelo del Tenant ajeno no es una fila de este listado");

        var nora = resultado.Elementos.Single(t => t.Id == _nora);
        nora.Incidencias.Select(i => (i.TipoDocumentoNombre, i.Estado)).Should().Equal(
            ("Aptitud médica", EstadoDocumento.Vencido),
            ("Formación Art. 19", EstadoDocumento.Urgente),
            ("Entrega de EPI", EstadoDocumento.SinConfirmar));
        nora.Incidencias[0].DocumentoId.Should().Be(_aptitudVencida, "la incidencia lleva el Id del documento que se abre al pulsarla");
        nora.Incidencias[1].FechaVencimiento.Should().Be(DiaDeNegocio.Hoy().AddDays(5));
        nora.DocumentosRegistrados.Should().Be(4, "vencido, urgente, sin confirmar y vigente: ni el eliminado ni el sustituido ni los dos del gemelo ajeno");
        nora.DocumentosVigentes.Should().Be(2, "el urgente sigue valiendo hoy y el vigente; el vencido y el sin confirmar no");
        nora.EstadoDocumental.Should().Be(EstadoDocumento.Vencido);

        var ana = resultado.Elementos.Single(t => t.Id == _ana);
        ana.Incidencias.Should().BeEmpty();
        (ana.DocumentosRegistrados, ana.DocumentosVigentes).Should().Be((0, 0), "sin documentos no hay fracción");
    }

    /// <summary>
    /// Coherencia: el peor estado de las incidencias de una fila es el estado de la fila, y una fila sin
    /// incidencias está en un estado que no pide acción (o no tiene estado).
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task El_peor_estado_de_las_incidencias_coincide_con_el_estado_de_la_fila(bool conRecuentosPorEstado)
    {
        var resultado = await HandlerSinCartera().Handle(
            new ObtenerTrabajadoresQuery(null, ConRecuentosPorEstado: conRecuentosPorEstado), CancellationToken.None);

        resultado.Elementos.Should().HaveCount(3, "control positivo: hay filas con y sin incidencias");
        resultado.Elementos.Count(t => t.Incidencias.Count > 0).Should().Be(2, "Nora e Iker");

        foreach (var fila in resultado.Elementos)
        {
            if (fila.Incidencias.Count > 0)
            {
                fila.Incidencias.MinBy(i => SeveridadEstadoDocumento.Rango(i.Estado))!.Estado
                    .Should().Be(fila.EstadoDocumental, $"la incidencia más grave de {fila.Nombre} es la que da su estado");
                fila.Incidencias.Select(i => SeveridadEstadoDocumento.Rango(i.Estado)).Should().BeInAscendingOrder();
            }
            else
            {
                (fila.EstadoDocumental is null or EstadoDocumento.Vigente or EstadoDocumento.SinCaducidad).Should().BeTrue(
                    $"{fila.Nombre} no tiene incidencias y su estado es {fila.EstadoDocumental}");
            }
        }
    }

    /// <summary>Sin pedirlo, el desglose no se calcula (exportación y API v1).</summary>
    [Fact]
    public async Task Sin_pedir_el_desglose_las_filas_llegan_sin_el_y_no_se_consulta()
    {
        _contador.Reiniciar();

        var resultado = await HandlerSinCartera().Handle(
            new ObtenerTrabajadoresQuery(null, ConDesgloseDocumental: false), CancellationToken.None);

        resultado.Elementos.Should().HaveCount(3);
        resultado.Elementos.Should().OnlyContain(t => t.Incidencias.Count == 0 && t.DocumentosRegistrados == 0);
        resultado.Elementos.Single(t => t.Id == _nora).EstadoDocumental.Should().Be(EstadoDocumento.Vencido, "el estado de la fila no depende del desglose");
        _contador.Consultas.Should().Be(0);
    }

    /// <summary>Una sola consulta de desglose para toda la página, no una por Trabajador.</summary>
    [Fact]
    public async Task El_desglose_de_la_pagina_es_una_sola_consulta()
    {
        _contador.Reiniciar();

        var resultado = await HandlerSinCartera().Handle(
            new ObtenerTrabajadoresQuery(null, ConRecuentosPorEstado: true), CancellationToken.None);

        resultado.Elementos.Count(t => t.DocumentosRegistrados > 0).Should().Be(2, "control positivo: dos Trabajadores con documentos en la página");
        _contador.Consultas.Should().Be(1, "el desglose se pide en lote para los Trabajadores de la página");
    }

    /// <summary>
    /// La consulta por id (la que la página usa para refrescar UNA fila tras guardar en la vista rápida) devuelve
    /// los mismos campos del desglose que la consulta de página: la fila refrescada no pierde el motivo ni
    /// «Registrados vigentes». Y sigue acotada por el alcance: el id de quien el usuario no ve no devuelve nada.
    /// </summary>
    [Fact]
    public async Task La_consulta_por_id_devuelve_el_mismo_desglose_que_la_fila_de_la_pagina()
    {
        var consultaDeFila = new ObtenerTrabajadoresQuery(Busqueda: null, ConRecuentosPorEstado: true, TrabajadorId: _nora);
        var pagina = await HandlerSinCartera().Handle(
            new ObtenerTrabajadoresQuery(null, ConRecuentosPorEstado: true), CancellationToken.None);
        var enLaPagina = pagina.Elementos.Single(t => t.Id == _nora);
        enLaPagina.Incidencias.Should().HaveCount(3, "control positivo: la fila de la página lleva desglose que comparar");

        var porId = await HandlerSinCartera().Handle(consultaDeFila, CancellationToken.None);

        var fila = porId.Elementos.Should().ContainSingle().Subject;
        fila.Should().BeEquivalentTo(enLaPagina, o => o.WithStrictOrdering(),
            "la fila refrescada es la misma fila, con sus incidencias en el mismo orden y su fracción");
        (fila.DocumentosRegistrados, fila.DocumentosVigentes).Should().Be((4, 2));

        var fueraDelAlcance = await HandlerDelGestor().Handle(
            new ObtenerTrabajadoresQuery(Busqueda: null, ConRecuentosPorEstado: true, TrabajadorId: _iker), CancellationToken.None);
        fueraDelAlcance.Elementos.Should().BeEmpty("pedir por id no salta la cartera");
        var dentroDelAlcance = await HandlerDelGestor().Handle(consultaDeFila, CancellationToken.None);
        dentroDelAlcance.Elementos.Should().ContainSingle("control positivo: el mismo Gestor sí recibe a Nora por id")
            .Which.Incidencias.Should().HaveCount(3);
    }

    /// <summary>(b): un Gestor CAE con cartera sobre un solo Cliente empresarial no recibe a quien queda fuera, ni su desglose.</summary>
    [Fact]
    public async Task Un_Gestor_CAE_con_cartera_acotada_no_recibe_filas_ni_desglose_de_fuera_de_su_alcance()
    {
        var resultado = await HandlerDelGestor().Handle(
            new ObtenerTrabajadoresQuery(null, ConRecuentosPorEstado: true), CancellationToken.None);

        resultado.Elementos.Select(t => t.Id).Should().BeEquivalentTo([_nora, _ana],
            "Iker solo trabaja en el Centro de un Cliente empresarial que la cartera no alcanza");
        resultado.Elementos.SelectMany(t => t.Incidencias).Should().HaveCount(3, "solo las tres incidencias de Nora");
        resultado.TotalElementos.Should().Be(2);

        // Control positivo: sin cartera que acote, Iker y su vencido salen.
        var sinCartera = await HandlerSinCartera().Handle(
            new ObtenerTrabajadoresQuery(null, ConRecuentosPorEstado: true), CancellationToken.None);
        sinCartera.Elementos.Single(t => t.Id == _iker).Incidencias.Should().ContainSingle()
            .Which.Estado.Should().Be(EstadoDocumento.Vencido);
    }

    /// <summary>
    /// (d): filtrar por un Centro fuera del alcance devuelve vacío, aunque un Trabajador visible (Nora) tenga
    /// una Asignación activa en él: el filtro no dice quién trabaja donde el usuario no ve.
    /// </summary>
    [Fact]
    public async Task El_filtro_por_un_Centro_fuera_del_alcance_devuelve_vacio()
    {
        var resultado = await HandlerDelGestor().Handle(
            new ObtenerTrabajadoresQuery(null, CentroId: _centroFuera, ConRecuentosPorEstado: true), CancellationToken.None);

        resultado.Elementos.Should().BeEmpty();
        resultado.TotalElementos.Should().Be(0);
        resultado.RecuentosPorEstado!.Values.Sum().Should().Be(0, "tampoco los recuentos de la franja cuentan a nadie");

        // Control positivo: quien ve el Centro recibe a los dos que trabajan en él.
        var sinCartera = await HandlerSinCartera().Handle(
            new ObtenerTrabajadoresQuery(null, CentroId: _centroFuera), CancellationToken.None);
        sinCartera.Elementos.Select(t => t.Id).Should().BeEquivalentTo([_nora, _iker]);
    }

    [Fact]
    public async Task El_filtro_por_Centro_deja_solo_a_quien_tiene_una_Asignacion_activa_en_el()
    {
        var delGestor = await HandlerDelGestor().Handle(
            new ObtenerTrabajadoresQuery(null, CentroId: _centroDentro), CancellationToken.None);
        delGestor.Elementos.Select(t => t.Id).Should().Equal([_nora], "Ana está de baja en ese Centro; Iker no trabaja en él");

        var sinCartera = await HandlerSinCartera().Handle(
            new ObtenerTrabajadoresQuery(null, CentroId: _centroDentro), CancellationToken.None);
        sinCartera.Elementos.Select(t => t.Id).Should().Equal([_nora]);

        // Control positivo: la Asignación de baja de Ana existe.
        (await _propietario.Asignaciones.IgnoreQueryFilters()
            .CountAsync(a => a.TrabajadorId == _ana && a.CentroId == _centroDentro && a.FechaBaja != null)).Should().Be(1);
    }

    private ObtenerTrabajadoresQueryHandler HandlerSinCartera() => new(
        _runtime, _runtime, _runtime, _runtime, new AlcanceDatosServiceFalso(),
        new CalculoEstadoDocumentalService(_runtime, _runtime, _runtime), _runtime);

    private ObtenerTrabajadoresQueryHandler HandlerDelGestor() => new(
        _runtimeDelGestor, _runtimeDelGestor, _runtimeDelGestor, _runtimeDelGestor,
        new AlcanceDatosService(_runtimeDelGestor, UsuarioGestor(), _tenantDeLaPeticion, new SesionPrivilegiadaAusente()),
        new CalculoEstadoDocumentalService(_runtimeDelGestor, _runtimeDelGestor, _runtimeDelGestor), _runtimeDelGestor);

    private CurrentUserServiceFalso UsuarioGestor() => new(_gestor, Roles.GestorCae, tenantOrigenId: _tenantSesion);

    private CaeManagerDbContext CrearRuntime(ICurrentUserService usuario)
    {
        var opciones = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(BaseDatosPostgresDePruebas.CadenaComoRuntime(_cadenaConexion))
            .AddInterceptors(
                new TenantSelladoInterceptor(_tenantDeLaPeticion),
                new TenantRlsConnectionInterceptor(_tenantDeLaPeticion, new SinClienteActivo(), usuario, BaseDatosPostgresDePruebas.FirmanteContextoRls),
                _contador)
            .Options;
        return new CaeManagerDbContext(opciones, new EphemeralDataProtectionProvider(), _tenantDeLaPeticion);
    }

    private async Task SembrarTenantDeLaSesionAsync()
    {
        using var ambito = AmbitoTenantExplicito.Establecer(_tenantSesion);
        var hoy = DiaDeNegocio.Hoy();
        var ahora = DateTime.UtcNow;

        var clienteDentro = Empresa.CrearComoCliente("Cervezas Duff Ibérica", "B12345674", false, null, null);
        var clienteFuera = Empresa.CrearComoCliente("Central Nuclear de Springfield", "B87654323", false, null, null);
        var propia = new Empresa("Montajes Springfield S.L.", "B10380186");
        var subcontrataFuera = Empresa.CrearComoSubcontrata("Aislamientos Shelbyville S.L.", null, NivelServicioSubcontrata.Gestionada.ToString());
        _propietario.Empresas.AddRange(clienteDentro, clienteFuera, propia, subcontrataFuera);
        _propietario.ParametrosSistema.Add(new ParametroSistema(umbralAmbarDias: 30, umbralRojoDias: 15));

        var aptitud = TipoDeTrabajador("Aptitud médica");
        var formacion = TipoDeTrabajador("Formación Art. 19");
        var epi = TipoDeTrabajador("Entrega de EPI");
        var contrato = TipoDeTrabajador("Contrato de trabajo");
        _propietario.TiposDocumento.AddRange(aptitud, formacion, epi, contrato);
        await _propietario.SaveChangesAsync();

        var centroDentro = new Centro(clienteDentro.Id, propia.Id, "Fábrica de Springfield");
        var centroFuera = new Centro(clienteFuera.Id, subcontrataFuera.Id, "Sector 7G");
        var nora = Trabajador.DeEmpresa(propia.Id, "Nora", "Dentro", "22334455Y");
        var ana = Trabajador.DeEmpresa(propia.Id, "Ana", "Baja", "77189989B");
        var iker = Trabajador.DeSubcontrata(subcontrataFuera.Id, "Iker", "Fuera", "33445566R");
        _propietario.Centros.AddRange(centroDentro, centroFuera);
        _propietario.Trabajadores.AddRange(nora, ana, iker);
        await _propietario.SaveChangesAsync();

        var bajaDeAna = new Asignacion(ana.Id, centroDentro.Id, hoy.AddDays(-30));
        bajaDeAna.DarDeBaja(hoy.AddDays(-1));
        _propietario.Asignaciones.AddRange(
            new Asignacion(nora.Id, centroDentro.Id, hoy.AddDays(-30)),
            new Asignacion(nora.Id, centroFuera.Id, hoy.AddDays(-30)),
            new Asignacion(iker.Id, centroFuera.Id, hoy.AddDays(-30)),
            bajaDeAna);

        var aptitudVencida = Documento.DeTrabajador(nora.Id, aptitud.Id, hoy.AddYears(-1), VigenciaDocumento.VenceEl(hoy.AddDays(-10)));
        var formacionUrgente = Documento.DeTrabajador(nora.Id, formacion.Id, hoy.AddYears(-1), VigenciaDocumento.VenceEl(hoy.AddDays(5)));
        var epiSinConfirmar = Documento.DeTrabajador(nora.Id, epi.Id, hoy.AddDays(-20), VigenciaDocumento.SinConfirmar);
        var contratoAnterior = Documento.DeTrabajador(nora.Id, contrato.Id, hoy.AddYears(-2), VigenciaDocumento.VenceEl(hoy.AddDays(-40)));
        var contratoVigente = Documento.DeTrabajador(nora.Id, contrato.Id, hoy.AddDays(-5), VigenciaDocumento.VenceEl(hoy.AddYears(1)));
        var epiEliminado = Documento.DeTrabajador(nora.Id, epi.Id, hoy.AddYears(-2), VigenciaDocumento.VenceEl(hoy.AddDays(-200)));
        epiEliminado.MarcarComoEliminado(Guid.NewGuid());
        var vencidoDeIker = Documento.DeTrabajador(iker.Id, aptitud.Id, hoy.AddYears(-1), VigenciaDocumento.VenceEl(hoy.AddDays(-3)));
        _propietario.Documentos.AddRange(
            aptitudVencida, formacionUrgente, epiSinConfirmar, contratoAnterior, contratoVigente, epiEliminado, vencidoDeIker);
        await _propietario.SaveChangesAsync();

        contratoAnterior.SustituirPor(contratoVigente, MotivoSustitucionDocumento.Renovacion, ahora);

        // Cartera del Gestor CAE: el Tenant entero en la cartera, acotado a UN Cliente empresarial en la operación.
        var operacion = AsignacionOperacion.Interna(
            _tenantSesion, ServicioCae.Outbound, AmbitoAsignacion.DeRelacionCliente(clienteDentro.Id), ahora, null, ahora);
        _propietario.AsignacionesOperacion.Add(operacion);
        _propietario.AsignacionesCartera.Add(AsignacionCartera.Interna(operacion, _gestor, AmbitoAsignacion.Universal, ahora, null, ahora));
        await _propietario.SaveChangesAsync();

        (_centroDentro, _centroFuera) = (centroDentro.Id, centroFuera.Id);
        (_nora, _iker, _ana) = (nora.Id, iker.Id, ana.Id);
        _aptitudVencida = aptitudVencida.Id;
    }

    private async Task SembrarTenantAjenoAsync()
    {
        using var ambito = AmbitoTenantExplicito.Establecer(_tenantAjeno);
        var hoy = DiaDeNegocio.Hoy();

        var propia = new Empresa("Montajes Springfield S.L.", "B10380186");
        _propietario.Empresas.Add(propia);
        _propietario.ParametrosSistema.Add(new ParametroSistema(umbralAmbarDias: 30, umbralRojoDias: 15));
        var aptitud = TipoDeTrabajador("Aptitud médica");
        var formacion = TipoDeTrabajador("Formación Art. 19");
        _propietario.TiposDocumento.AddRange(aptitud, formacion);
        await _propietario.SaveChangesAsync();

        var gemelo = Trabajador.DeEmpresa(propia.Id, "Nora", "Dentro", "22334455Y");
        _propietario.Trabajadores.Add(gemelo);
        await _propietario.SaveChangesAsync();

        var vencido = Documento.DeTrabajador(gemelo.Id, aptitud.Id, hoy.AddYears(-1), VigenciaDocumento.VenceEl(hoy.AddDays(-7)));
        _propietario.Documentos.AddRange(
            vencido,
            Documento.DeTrabajador(gemelo.Id, formacion.Id, hoy.AddYears(-1), VigenciaDocumento.VenceEl(hoy.AddDays(-8))));
        await _propietario.SaveChangesAsync();

        _gemeloAjeno = gemelo.Id;
        _documentoAjeno = vencido.Id;
    }

    private static TipoDocumento TipoDeTrabajador(string nombre) =>
        new(nombre, null, aplicaVencimientoAutomatico: false, 1, AmbitoAplicacion.Trabajador, requerido: RequisitoDocumental.Si);

    /// <summary>
    /// Cuenta las consultas del desglose: las únicas de estos handlers que unen <c>Documentos</c> con
    /// <c>TiposDocumento</c>.
    /// </summary>
    private sealed class ContadorDeConsultasDeDesglose : DbCommandInterceptor
    {
        private int _consultas;

        public int Consultas => Volatile.Read(ref _consultas);

        public void Reiniciar() => Interlocked.Exchange(ref _consultas, 0);

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("\"Documentos\"", StringComparison.Ordinal)
                && command.CommandText.Contains("\"TiposDocumento\"", StringComparison.Ordinal))
                Interlocked.Increment(ref _consultas);

            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private sealed class TenantActualPorAmbito : ITenantActual
    {
        public Guid? TenantId => AmbitoTenantExplicito.TenantIdActual;
    }

    /// <summary>Como el <c>TenantActual</c> real: primero el ámbito explícito, luego el Tenant de la sesión.</summary>
    private sealed class TenantActualDeLaPeticion(Guid tenantDeLaSesion) : ITenantActual
    {
        public Guid? TenantId => AmbitoTenantExplicito.TenantIdActual ?? tenantDeLaSesion;
    }

    private sealed class SinClienteActivo : IClienteActivoSeleccionado
    {
        public Guid? TenantIdSeleccionado => null;
        public Guid? AsignacionOperacionIdSeleccionada => null;
        public Guid? SesionPrivilegiadaIdSeleccionada => null;
    }

    private sealed class SesionPrivilegiadaAusente : ISesionPrivilegiadaActual
    {
        public Task<SesionPrivilegiadaActiva?> ObtenerAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<SesionPrivilegiadaActiva?>(null);

        public Task<SesionPrivilegiadaActiva?> RevalidarAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<SesionPrivilegiadaActiva?>(null);
    }
}
