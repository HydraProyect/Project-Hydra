using CaeManager.Application.Asignaciones.Queries.ObtenerAsignacionesDocumentacionPorCentro;
using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.Documentos.SituacionEnCentro;
using CaeManager.Application.Plataforma;
using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Integraciones;
using CaeManager.Domain.Operaciones;
using CaeManager.Domain.RelacionesEmpresariales;
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
using Xunit;

namespace CaeManager.IntegrationTests.Subcontratas;

/// <summary>
/// El lote de <see cref="ObtenerCumplimientoDeAsignacionesPorCentrosQuery"/> (Subcontrata 360: una carga para todos los
/// Centros) frente a <see cref="ObtenerAsignacionesDocumentacionPorCentroQuery"/> lanzada Centro a Centro con la misma
/// Empresa empleadora, que es el resultado independiente: cada Centro del lote tiene que dar exactamente lo mismo que dio
/// siempre su consulta propia. Se lee <b>como <c>cae_app_runtime</c></b> (sin BYPASSRLS) con los interceptores de sellado y
/// de sesión RLS de producción, y el alcance parcial es el real: un Gestor CAE cuya Asignación de Operación se acota a un
/// Cliente empresarial (mismo arnés que <c>DesgloseDocumentalDeClientesBajoAlcanceTests</c>).
///
/// <para>
/// Escenario, en el Tenant propietario de la sesión. La Subcontrata S (Empresa empleadora) trabaja para dos Clientes
/// empresariales; el Gestor CAE solo tiene en su cartera al Cliente A. Dos tipos de documento de Trabajador exigidos, la
/// Formación (bloquea el acceso en los tres Centros con plataforma) y la Aptitud médica (sin fila de Centro, obligatoria por
/// defecto):
/// <list type="bullet">
/// <item>C1 (Cliente A): «Ana», de S, con los dos al día y la Formación validada en la plataforma del Centro; y «Pepe», de
/// la Empresa propia, sin documentos (no es de S: ni la consulta ni el lote lo cuentan).</item>
/// <item>C2 (Cliente A): «Ana» otra vez, con la MISMA Formación pendiente de subir a la plataforma de C2 (bloqueado en
/// plataforma: ahí no cuenta como al día); «Beto», de S, con la Formación vencida. Este Centro NO exige la Aptitud
/// (fila de Centro con «incluido» en falso): lo que se exige es distinto en cada Centro, y el lote no puede usar la unión.</item>
/// <item>C3 (Cliente B, fuera de la cartera del Gestor): «Cleo», de S, con la Formación validada y sin Aptitud.</item>
/// <item>C4 (Cliente A): sin ninguna Asignación activa.</item>
/// <item>C5 (Cliente A): no exige ninguno de los dos tipos (filas de Centro «incluido» en falso); «Beto» tiene una
/// Asignación allí. Es la rama «sin requisitos», que el lote mezcla con los Centros que sí los tienen.</item>
/// <item>«Dora», de la Subcontrata S' (que solo trabaja para el Cliente B y está fuera de la cartera del Gestor), con una
/// Asignación en C1: si el lote dejara de comprobar la visibilidad de la Subcontrata empleadora, la devolvería.</item>
/// </list>
/// Otro Tenant propietario tiene un Centro que ningún resultado puede nombrar. La siembra va como propietario de la base,
/// porque no es lo que se mide.
/// </para>
/// </summary>
public class CumplimientoPorCentrosEnLoteBajoRlsTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _gestor = Guid.NewGuid();
    private CaeManagerDbContext _propietario = null!;
    private CaeManagerDbContext _runtimeDelGestor = null!;
    private CaeManagerDbContext _runtimeDeAdministrador = null!;
    private TenantActualDeLaPeticion _tenantDeLaPeticion = null!;
    private Guid _tenantSesion;
    private Guid _subcontrata;
    private Guid _subcontrataDelClienteB;
    private Guid _centro1;
    private Guid _centro2;
    private Guid _centro3;
    private Guid _centro4;
    private Guid _centro5;
    private Guid _centroAjeno;
    private Guid _ana;
    private Guid _beto;
    private Guid _cleo;
    private Guid _pepe;
    private Guid _dora;

    // Instante único del test: la siembra y las superficies que se miden leen el mismo día de negocio aunque la ejecución cruce
    // la medianoche de Europe/Madrid (RelojDePrueba, issue #2073). Se fija dentro del cuerpo de cada test, no solo en InitializeAsync.
    private readonly DateTimeOffset _instante = DateTimeOffset.UtcNow;

    public async Task InitializeAsync()
    {
        using var reloj = RelojDePrueba.FijarEn(_instante);
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

        await SembrarTenantDeLaSesionAsync();
        await SembrarTenantAjenoAsync(tenantAjeno.Id);

        _tenantDeLaPeticion = new TenantActualDeLaPeticion(_tenantSesion);
        _runtimeDelGestor = CrearRuntime(UsuarioGestor());
        _runtimeDeAdministrador = CrearRuntime(new CurrentUserServiceFalso(Guid.NewGuid(), Roles.Administrador, tenantOrigenId: _tenantSesion));
    }

    public async Task DisposeAsync()
    {
        await _runtimeDeAdministrador.DisposeAsync();
        await _runtimeDelGestor.DisposeAsync();
        await _propietario.DisposeAsync();
        await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);
    }

    /// <summary>Control del instrumento: la conexión de lectura está de verdad bajo RLS, y lo «de fuera» existe.</summary>
    [Fact]
    public async Task La_lectura_va_bajo_RLS_efectiva_y_el_Centro_del_Tenant_ajeno_existe()
    {
        using var reloj = RelojDePrueba.FijarEn(_instante);
        (await _runtimeDeAdministrador.Centros.IgnoreQueryFilters().CountAsync(c => c.Id == _centroAjeno)).Should().Be(0,
            "sin filtro global de EF, solo RLS puede ocultar el Centro del Tenant ajeno");
        (await _runtimeDeAdministrador.Centros.IgnoreQueryFilters().CountAsync()).Should().Be(5,
            "control positivo: los cinco Centros del Tenant de la sesión sí se ven");
        (await _propietario.Centros.IgnoreQueryFilters().CountAsync(c => c.Id == _centroAjeno)).Should().Be(1);
    }

    /// <summary>
    /// El caso principal: un Gestor CAE con cartera sobre el Cliente A pide el lote de los cinco Centros (y uno repetido) de
    /// la Subcontrata S. Cada Centro que ve da lo mismo que su consulta propia; el de otro Cliente empresarial y el que no
    /// tiene Asignaciones no aparecen, igual que su consulta propia da una lista vacía.
    /// </summary>
    [Fact]
    public async Task El_lote_de_un_Gestor_con_alcance_parcial_da_en_cada_Centro_lo_mismo_que_la_consulta_Centro_a_Centro()
    {
        using var reloj = RelojDePrueba.FijarEn(_instante);
        var (lote, porCentro) = Handlers(_runtimeDelGestor, AlcanceDelGestor(), UsuarioGestor());
        Guid[] pedidos = [_centro1, _centro2, _centro3, _centro4, _centro5, _centro1];

        var delLote = await lote.Handle(new ObtenerCumplimientoDeAsignacionesPorCentrosQuery(pedidos, _subcontrata), CancellationToken.None);
        var independientes = await CentroACentroAsync(porCentro, pedidos.Distinct(), _subcontrata);

        delLote.Keys.Should().BeEquivalentTo([_centro1, _centro2, _centro5], "solo los Centros de la cartera con Asignaciones de S");
        independientes[_centro3].Should().BeEmpty("control: el Centro del Cliente B no es visible para el Gestor en su consulta propia");
        independientes[_centro4].Should().BeEmpty("control: sin Asignaciones activas no hay filas");
        foreach (var centro in pedidos.Distinct().Where(c => independientes[c].Count > 0))
            Proyectar(delLote[centro]).Should().Equal(independientes[centro], $"el lote de {centro} tiene que ser el de su consulta propia");
        delLote.Count.Should().Be(independientes.Count(par => par.Value.Count > 0));

        // El oráculo, escrito a mano: la misma Formación de Ana cuenta en C1 (validada) y no en C2 (pendiente en su plataforma),
        // y C2 no exige la Aptitud (lo que se exige es propio de cada Centro, no la unión de los Centros del lote).
        Proyectar(delLote[_centro1]).Should().Equal(
            (_ana, EstadoDocumento.Vigente, new FraccionCumplimiento(2, 2)));
        var deC2 = delLote[_centro2].ToDictionary(t => t.TrabajadorId);
        deC2.Keys.Should().BeEquivalentTo([_ana, _beto]);
        deC2[_ana].Cumplimiento.Should().Be(new FraccionCumplimiento(0, 1),
            "C2 solo exige la Formación, y está pendiente de subir a la plataforma de C2: no cuenta como al día");
        deC2[_beto].Cumplimiento.Should().Be(new FraccionCumplimiento(0, 1), "C2 solo exige la Formación, y la de Beto está vencida");
        deC2[_beto].PeorEstado.Should().Be(EstadoDocumento.Vencido);
        // Sin requisitos en C5: la rama inmediata, mezclada en el mismo lote con los Centros que sí los tienen.
        Proyectar(delLote[_centro5]).Should().Equal((_beto, EstadoDocumento.Vigente, FraccionCumplimiento.SinRequisitos));

        // Quien no es de S no entra aunque trabaje en un Centro visible: sin el filtro de S la consulta propia sí lo cuenta.
        var sinFiltro = await porCentro.Handle(new ObtenerAsignacionesDocumentacionPorCentroQuery(_centro1), CancellationToken.None);
        sinFiltro.Select(t => t.TrabajadorId).Should().BeEquivalentTo([_ana, _pepe, _dora],
            "control positivo: Pepe, de la Empresa propia, y Dora, de la otra Subcontrata, trabajan en C1");
        delLote[_centro1].Select(t => t.TrabajadorId).Should().NotContain([_pepe, _dora]);
    }

    /// <summary>Pedir solo un Centro visible, o solo uno que no lo es, también coincide con la consulta propia.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task El_lote_de_un_solo_Centro_coincide_con_su_consulta_propia_visible_o_no(int numeroDeCentro)
    {
        using var reloj = RelojDePrueba.FijarEn(_instante);
        var centro = numeroDeCentro switch { 1 => _centro1, 2 => _centro2, _ => _centro3 };
        var (lote, porCentro) = Handlers(_runtimeDelGestor, AlcanceDelGestor(), UsuarioGestor());

        var delLote = await lote.Handle(new ObtenerCumplimientoDeAsignacionesPorCentrosQuery([centro], _subcontrata), CancellationToken.None);
        var independiente = (await CentroACentroAsync(porCentro, [centro], _subcontrata))[centro];

        if (independiente.Count == 0)
            delLote.Should().BeEmpty("el Centro fuera de la cartera no se calcula ni se nombra");
        else
            Proyectar(delLote[centro]).Should().Equal(independiente);
        (numeroDeCentro == 3).Should().Be(independiente.Count == 0, "control: solo el Centro del Cliente B queda fuera de la cartera del Gestor");
    }

    /// <summary>La Subcontrata que el Gestor no ve (solo trabaja para el Cliente B) no devuelve nada, ni en el lote ni Centro a Centro.</summary>
    [Fact]
    public async Task Una_Subcontrata_fuera_de_la_cartera_no_devuelve_nada_ni_en_el_lote_ni_Centro_a_Centro()
    {
        using var reloj = RelojDePrueba.FijarEn(_instante);
        var (lote, porCentro) = Handlers(_runtimeDelGestor, AlcanceDelGestor(), UsuarioGestor());
        Guid[] pedidos = [_centro1, _centro2, _centro3, _centro4, _centro5];

        var delLote = await lote.Handle(new ObtenerCumplimientoDeAsignacionesPorCentrosQuery(pedidos, _subcontrataDelClienteB), CancellationToken.None);
        var independientes = await CentroACentroAsync(porCentro, pedidos, _subcontrataDelClienteB);

        delLote.Should().BeEmpty("Dora, de esa Subcontrata, tiene una Asignación en C1 (visible): solo la visibilidad de la Subcontrata la deja fuera");
        independientes.Values.Should().OnlyContain(filas => filas.Count == 0);

        // Control positivo: sin restricción de alcance, la misma petición sí devuelve a Dora en C1.
        var administrador = new CurrentUserServiceFalso(Guid.NewGuid(), Roles.Administrador, tenantOrigenId: _tenantSesion);
        var (loteSinAlcance, _) = Handlers(_runtimeDeAdministrador, new AlcanceDatosServiceFalso(), administrador);
        var conAlcanceTotal = await loteSinAlcance.Handle(
            new ObtenerCumplimientoDeAsignacionesPorCentrosQuery(pedidos, _subcontrataDelClienteB), CancellationToken.None);
        conAlcanceTotal.Keys.Should().BeEquivalentTo([_centro1]);
        conAlcanceTotal[_centro1].Select(t => t.TrabajadorId).Should().ContainSingle().Which.Should().Be(_dora);
    }

    /// <summary>
    /// Sin restricción de alcance (Administrador) el lote también nombra el Centro del Cliente B, y los tres siguen
    /// coincidiendo con su consulta propia: control positivo de que lo que el Gestor no ve existe y se calcula.
    /// </summary>
    [Fact]
    public async Task Sin_restriccion_de_alcance_el_lote_incluye_el_Centro_del_Cliente_B_y_coincide_Centro_a_Centro()
    {
        using var reloj = RelojDePrueba.FijarEn(_instante);
        var administrador = new CurrentUserServiceFalso(Guid.NewGuid(), Roles.Administrador, tenantOrigenId: _tenantSesion);
        var (lote, porCentro) = Handlers(_runtimeDeAdministrador, new AlcanceDatosServiceFalso(), administrador);
        Guid[] pedidos = [_centro1, _centro2, _centro3, _centro4, _centro5];

        var delLote = await lote.Handle(new ObtenerCumplimientoDeAsignacionesPorCentrosQuery(pedidos, _subcontrata), CancellationToken.None);
        var independientes = await CentroACentroAsync(porCentro, pedidos, _subcontrata);

        delLote.Keys.Should().BeEquivalentTo([_centro1, _centro2, _centro3, _centro5]);
        foreach (var centro in pedidos.Where(c => independientes[c].Count > 0))
            Proyectar(delLote[centro]).Should().Equal(independientes[centro]);
        Proyectar(delLote[_centro3]).Should().Equal((_cleo, EstadoDocumento.Faltante, new FraccionCumplimiento(1, 2)));
    }

    private static async Task<Dictionary<Guid, List<(Guid TrabajadorId, EstadoDocumento PeorEstado, FraccionCumplimiento Cumplimiento)>>> CentroACentroAsync(
        ObtenerAsignacionesDocumentacionPorCentroQueryHandler porCentro, IEnumerable<Guid> centros, Guid subcontrata)
    {
        var resultado = new Dictionary<Guid, List<(Guid, EstadoDocumento, FraccionCumplimiento)>>();
        foreach (var centro in centros)
        {
            var filas = await porCentro.Handle(
                new ObtenerAsignacionesDocumentacionPorCentroQuery(centro, EmpresaEmpleadoraSubcontratistaId: subcontrata), CancellationToken.None);
            resultado[centro] = filas.Select(t => (t.TrabajadorId, t.PeorEstado, t.Cumplimiento)).ToList();
        }
        return resultado;
    }

    private static List<(Guid TrabajadorId, EstadoDocumento PeorEstado, FraccionCumplimiento Cumplimiento)> Proyectar(
        IReadOnlyList<CumplimientoDeAsignacionDto> filas) =>
        filas.Select(t => (t.TrabajadorId, t.PeorEstado, t.Cumplimiento)).ToList();

    /// <summary>Los dos handlers sobre el mismo contexto, el MISMO servicio de alcance y la misma carga de situación, como en una petición.</summary>
    private static (ObtenerCumplimientoDeAsignacionesPorCentrosQueryHandler Lote, ObtenerAsignacionesDocumentacionPorCentroQueryHandler PorCentro) Handlers(
        CaeManagerDbContext c, IAlcanceDatosService alcance, ICurrentUserService usuario)
    {
        var situacion = new SituacionDocumentosEnCentrosService(c, c, c, c, c, alcance, usuario);
        return (
            new ObtenerCumplimientoDeAsignacionesPorCentrosQueryHandler(c, c, c, c, c, c, alcance, situacion),
            new ObtenerAsignacionesDocumentacionPorCentroQueryHandler(c, c, c, c, c, c, alcance, situacion));
    }

    private AlcanceDatosService AlcanceDelGestor() =>
        new(_runtimeDelGestor, UsuarioGestor(), _tenantDeLaPeticion, new SesionPrivilegiadaAusente());

    private CurrentUserServiceFalso UsuarioGestor() => new(_gestor, Roles.GestorCae, tenantOrigenId: _tenantSesion);

    private CaeManagerDbContext CrearRuntime(ICurrentUserService usuario)
    {
        var opciones = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(BaseDatosPostgresDePruebas.CadenaComoRuntime(_cadenaConexion))
            .AddInterceptors(
                new TenantSelladoInterceptor(_tenantDeLaPeticion),
                new TenantRlsConnectionInterceptor(_tenantDeLaPeticion, new SinClienteActivo(), usuario, BaseDatosPostgresDePruebas.FirmanteContextoRls))
            .Options;
        return new CaeManagerDbContext(opciones, new EphemeralDataProtectionProvider(), _tenantDeLaPeticion);
    }

    private async Task SembrarTenantDeLaSesionAsync()
    {
        using var ambito = AmbitoTenantExplicito.Establecer(_tenantSesion);
        var hoy = DiaDeNegocio.Hoy();
        var ahora = _instante.UtcDateTime;

        var clienteA = Empresa.CrearComoCliente("Cervezas Duff Ibérica", "B12345674", false, null, null);
        var clienteB = Empresa.CrearComoCliente("Central Nuclear de Springfield", "B87654323", false, null, null);
        var propia = new Empresa("Montajes Springfield S.L.", "B66778895");
        var subcontrata = Empresa.CrearComoSubcontrata("Aislamientos Shelbyville S.L.", null, NivelServicioSubcontrata.Gestionada.ToString());
        var subcontrataDelB = Empresa.CrearComoSubcontrata("Reformas Capital City S.L.", null, NivelServicioSubcontrata.Gestionada.ToString());
        _propietario.Empresas.AddRange(clienteA, clienteB, propia, subcontrata, subcontrataDelB);
        _propietario.ParametrosSistema.Add(new ParametroSistema(umbralAmbarDias: 30, umbralRojoDias: 15));
        var formacion = TipoDeTrabajador("Formación 60h");
        var aptitud = TipoDeTrabajador("Aptitud médica");
        _propietario.TiposDocumento.AddRange(formacion, aptitud);
        var proveedor = new ProveedorPlataformaCae($"PLAT-{Guid.NewGuid():N}"[..14], "Plataforma de prueba");
        _propietario.ProveedoresPlataformaCae.Add(proveedor);
        await _propietario.SaveChangesAsync();

        // S trabaja para los dos Clientes empresariales; la otra Subcontrata, solo para el B (fuera de la cartera del Gestor).
        _propietario.RelacionesEmpresariales.AddRange(
            RelacionEmpresarial.Migrar(subcontrata.Id, clienteA.Id, ahora, ahora),
            RelacionEmpresarial.Migrar(subcontrata.Id, clienteB.Id, ahora, ahora),
            RelacionEmpresarial.Migrar(subcontrataDelB.Id, clienteB.Id, ahora, ahora));

        var centro1 = new Centro(clienteA.Id, propia.Id, "Fábrica de Springfield");
        var centro2 = new Centro(clienteA.Id, propia.Id, "Almacén de Shelbyville");
        var centro3 = new Centro(clienteB.Id, propia.Id, "Sector 7G");
        var centro4 = new Centro(clienteA.Id, propia.Id, "Oficina sin asignaciones");
        var centro5 = new Centro(clienteA.Id, propia.Id, "Taller sin requisitos");
        _propietario.Centros.AddRange(centro1, centro2, centro3, centro4, centro5);
        await _propietario.SaveChangesAsync();

        var canal1 = CanalGestionDocumental.DePlataforma(centro1.Id, "Acceso de prueba", proveedor.Id, null, null, null);
        var canal2 = CanalGestionDocumental.DePlataforma(centro2.Id, "Acceso de prueba", proveedor.Id, null, null, null);
        var canal3 = CanalGestionDocumental.DePlataforma(centro3.Id, "Acceso de prueba", proveedor.Id, null, null, null);
        _propietario.CanalesGestionDocumental.AddRange(canal1, canal2, canal3);
        // La Formación se exige en C1, C2 y C3 y bloquea el acceso en los tres; la Aptitud (obligatoria por defecto, sin fila)
        // se exige en C1 y C3 pero C2 la excluye, y C5 no exige ninguno de los dos: lo exigido es propio de cada Centro.
        _propietario.TiposDocumentoCentros.AddRange(
            new TipoDocumentoCentro(formacion.Id, centro1.Id, incluido: true, null, bloqueaAcceso: true),
            new TipoDocumentoCentro(formacion.Id, centro2.Id, incluido: true, null, bloqueaAcceso: true),
            new TipoDocumentoCentro(formacion.Id, centro3.Id, incluido: true, null, bloqueaAcceso: true),
            new TipoDocumentoCentro(aptitud.Id, centro2.Id, incluido: false, null, bloqueaAcceso: false),
            new TipoDocumentoCentro(formacion.Id, centro5.Id, incluido: false, null, bloqueaAcceso: false),
            new TipoDocumentoCentro(aptitud.Id, centro5.Id, incluido: false, null, bloqueaAcceso: false));

        var ana = Trabajador.DeSubcontrata(subcontrata.Id, "Ana", "Dentro", "77189989B");
        var beto = Trabajador.DeSubcontrata(subcontrata.Id, "Beto", "Dentro", "12345678Z");
        var cleo = Trabajador.DeSubcontrata(subcontrata.Id, "Cleo", "Fuera", "22334455Y");
        var pepe = Trabajador.DeEmpresa(propia.Id, "Pepe", "Propio", "33445566R");
        var dora = Trabajador.DeSubcontrata(subcontrataDelB.Id, "Dora", "Otra", "99887766P");
        _propietario.Trabajadores.AddRange(ana, beto, cleo, pepe, dora);
        await _propietario.SaveChangesAsync();

        _propietario.Asignaciones.AddRange(
            new Asignacion(ana.Id, centro1.Id, hoy.AddDays(-30)),
            new Asignacion(ana.Id, centro2.Id, hoy.AddDays(-20)),
            new Asignacion(pepe.Id, centro1.Id, hoy.AddDays(-30)),
            new Asignacion(dora.Id, centro1.Id, hoy.AddDays(-30)),
            new Asignacion(beto.Id, centro2.Id, hoy.AddDays(-30)),
            new Asignacion(beto.Id, centro5.Id, hoy.AddDays(-10)),
            new Asignacion(cleo.Id, centro3.Id, hoy.AddDays(-30)));

        var formacionDeAna = Documento.DeTrabajador(ana.Id, formacion.Id, hoy.AddDays(-5), VigenciaDocumento.VenceEl(hoy.AddYears(1)));
        var aptitudDeAna = Documento.DeTrabajador(ana.Id, aptitud.Id, hoy.AddDays(-5), VigenciaDocumento.VenceEl(hoy.AddYears(1)));
        var formacionVencidaDeBeto = Documento.DeTrabajador(beto.Id, formacion.Id, hoy.AddYears(-1), VigenciaDocumento.VenceEl(hoy.AddDays(-10)));
        var formacionDeCleo = Documento.DeTrabajador(cleo.Id, formacion.Id, hoy.AddDays(-5), VigenciaDocumento.VenceEl(hoy.AddYears(1)));
        _propietario.Documentos.AddRange(formacionDeAna, aptitudDeAna, formacionVencidaDeBeto, formacionDeCleo);
        await _propietario.SaveChangesAsync();

        // Acreditaciones en la plataforma de cada Centro: la Formación de Ana, validada en C1 y sin subir en C2.
        _propietario.AcreditacionesDocumentoPlataforma.AddRange(
            Acreditacion(formacionDeAna.Id, canal1.Id, validada: true),
            Acreditacion(formacionDeAna.Id, canal2.Id, validada: false),
            Acreditacion(aptitudDeAna.Id, canal1.Id, validada: true),
            Acreditacion(aptitudDeAna.Id, canal2.Id, validada: true),
            Acreditacion(formacionDeCleo.Id, canal3.Id, validada: true));

        // Cartera del Gestor CAE: el Tenant entero en la cartera, acotado a UN Cliente empresarial en la operación.
        var operacion = AsignacionOperacion.Interna(
            _tenantSesion, ServicioCae.Outbound, AmbitoAsignacion.DeRelacionCliente(clienteA.Id), ahora, null, ahora);
        _propietario.AsignacionesOperacion.Add(operacion);
        _propietario.AsignacionesCartera.Add(AsignacionCartera.Interna(operacion, _gestor, AmbitoAsignacion.Universal, ahora, null, ahora));
        await _propietario.SaveChangesAsync();

        (_subcontrata, _subcontrataDelClienteB) = (subcontrata.Id, subcontrataDelB.Id);
        (_centro1, _centro2, _centro3, _centro4, _centro5) = (centro1.Id, centro2.Id, centro3.Id, centro4.Id, centro5.Id);
        (_ana, _beto, _cleo, _pepe, _dora) = (ana.Id, beto.Id, cleo.Id, pepe.Id, dora.Id);
    }

    private async Task SembrarTenantAjenoAsync(Guid tenantAjenoId)
    {
        using var ambito = AmbitoTenantExplicito.Establecer(tenantAjenoId);
        var cliente = Empresa.CrearComoCliente("Cervezas Duff Ibérica", "B12345674", false, null, null);
        var propia = new Empresa("Montajes Springfield S.L.", "B66778895");
        _propietario.Empresas.AddRange(cliente, propia);
        await _propietario.SaveChangesAsync();

        var centro = new Centro(cliente.Id, propia.Id, "Fábrica de Springfield");
        _propietario.Centros.Add(centro);
        await _propietario.SaveChangesAsync();
        _centroAjeno = centro.Id;
    }

    private static AcreditacionDocumentoPlataforma Acreditacion(Guid documentoId, Guid canalId, bool validada)
    {
        var acreditacion = new AcreditacionDocumentoPlataforma(documentoId, canalId);
        if (validada)
        {
            acreditacion.MarcarSubida();
            acreditacion.MarcarAceptada(VigenciaEnPlataforma.NoVenceAqui);
        }
        return acreditacion;
    }

    private static TipoDocumento TipoDeTrabajador(string nombre) =>
        new(nombre, null, aplicaVencimientoAutomatico: false, 1, AmbitoAplicacion.Trabajador, requerido: RequisitoDocumental.Si);

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
