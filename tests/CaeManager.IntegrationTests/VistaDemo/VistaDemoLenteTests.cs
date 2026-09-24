using CaeManager.Application.Common;
using CaeManager.Application.Plataforma;
using CaeManager.Application.Tenants.Queries.ObtenerClientesAutorizados;
using CaeManager.Application.VistaDemo;
using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Operaciones;
using CaeManager.Domain.Plataforma;
using CaeManager.Domain.Tenants;
using CaeManager.Domain.Trabajadores;
using CaeManager.Infrastructure.Autorizacion;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Seed;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace CaeManager.IntegrationTests.LenteDeDemo;

/// <summary>
/// La lente de demo (selector Dirección / Coordinador CAE / Gestor CAE) es una LENTE PRESENTACIONAL:
/// solo estrecha lo que la cuenta ya alcanza. Dos propiedades se prueban aquí, sobre PostgreSQL real
/// y con el <see cref="AlcanceDatosService"/> de verdad (no un falso):
///
/// 1) FIDELIDAD — la vista Gestor muestra exactamente lo que ese Gestor CAE vería entrando con su
///    propia cuenta (los seis alcances, en cada Tenant), para que la demo no enseñe algo que el
///    producto real no da.
/// 2) NUNCA AMPLÍA — una petición inválida, de otra cuenta, sin el rol de demo, de un Tenant real, o
///    apuntando a un Gestor que no es elegible, deja el resultado igual que sin lente (la
///    autorización real); y la lente sobre un alcance ya restringido solo lo interseca.
///
/// No cubre RLS: estas pruebas corren con el propietario de la base (que la salta por diseño). La
/// lente no toca RLS, identidad de PostgreSQL ni comandos de escritura; que no lo hace se sostiene
/// por construcción (solo se toca IAlcanceDatosService, la lista de Tenants y el menú), no por esta
/// prueba.
/// </summary>
public class VistaDemoLenteTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();

    private Guid _operador, _p1, _p2, _p3, _tenantReal, _tenantReal2, _otroOperador;
    private Guid _admin, _g1, _g2, _gSinCartera, _gCarteraFutura, _usuarioCliente, _gDeOtroOperador, _gSoloTenantReal;
    private Empresa _c1a = null!, _c1b = null!, _c2 = null!, _c3 = null!;

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto(Guid.NewGuid());
        await contexto.Database.MigrateAsync();

        var operador = new Tenant(DelegacionDemoSeeder.NombreTenantConsultora);
        var p1 = new Tenant(DelegacionDemoSeeder.NombreTenantRefrielectric);
        var p2 = new Tenant(DelegacionDemoSeeder.NombreTenantClienteDemo);
        var p3 = new Tenant(DelegacionDemoSeeder.NombreTenantClienteDemo2);
        var real = new Tenant("Cliente Real, S.L. (no es de demo)");
        var otroOperador = new Tenant("Otro Operador Real, S.L.");
        var real2 = new Tenant("Cliente Real 2, S.L. (no es de demo)");
        contexto.Tenants.AddRange(operador, p1, p2, p3, real, otroOperador, real2);
        _tenantReal2 = real2.Id;
        (_operador, _p1, _p2, _p3, _tenantReal, _otroOperador) = (operador.Id, p1.Id, p2.Id, p3.Id, real.Id, otroOperador.Id);

        // Roles y usuarios: la cuenta de demo (Administrador) y los Gestores CAE del Operador.
        var rolGestor = await ObtenerORolAsync(contexto, Roles.GestorCae);
        var rolCliente = await ObtenerORolAsync(contexto, Roles.Cliente);
        var rolAdmin = await ObtenerORolAsync(contexto, Roles.Administrador);
        Guid Usuario(string nombre, Guid tenant, Guid rol)
        {
            var correo = $"{nombre}@vista-demo.test";
            var u = new ApplicationUser
            {
                UserName = correo,
                NormalizedUserName = correo.ToUpperInvariant(),
                Email = correo,
                NormalizedEmail = correo.ToUpperInvariant(),
                NombreCompleto = nombre,
                TenantId = tenant,
            };
            contexto.Users.Add(u);
            contexto.UserRoles.Add(new IdentityUserRole<Guid> { UserId = u.Id, RoleId = rol });
            return u.Id;
        }
        _admin = Usuario("admin", _operador, rolAdmin);
        _g1 = Usuario("gestora-uno", _operador, rolGestor);
        _g2 = Usuario("gestor-dos", _operador, rolGestor);
        _gSinCartera = Usuario("gestor-sin-cartera", _operador, rolGestor);
        _gCarteraFutura = Usuario("gestor-cartera-futura", _operador, rolGestor);
        _usuarioCliente = Usuario("usuario-cliente", _operador, rolCliente);
        _gDeOtroOperador = Usuario("gestor-de-otro-operador", _otroOperador, rolGestor);
        _gSoloTenantReal = Usuario("gestor-solo-tenant-real", _operador, rolGestor);
        await contexto.SaveChangesAsync();

        // Datos de cada Tenant propietario, sellados con su propio Tenant.
        _c1a = await SembrarClienteAsync(_p1, "Cliente 1A", "B12345674", "77189989B");
        _c1b = await SembrarClienteAsync(_p1, "Cliente 1B", "P1234567D", "12345678Z");
        _c2 = await SembrarClienteAsync(_p2, "Cliente 2", "B12345674", null);
        _c3 = await SembrarClienteAsync(_p3, "Cliente 3", "B12345674", null);
        var cReal = await SembrarClienteAsync(_tenantReal, "Cliente Real", "B12345674", null);
        var cReal2 = await SembrarClienteAsync(_tenantReal2, "Cliente Real 2", "B12345674", null);
        var c1c = await SembrarClienteAsync(_p1, "Cliente 1C", "A58818501", null);
        var c1d = await SembrarClienteAsync(_p1, "Cliente 1D", "B87654323", null);

        // Carteras (Operador CAE externo Outbound sobre cada Tenant propietario). G1: 1A y 2; G2: 1B y 3.
        var ahora = DateTime.UtcNow;
        var desde = ahora.AddDays(-2);
        AsignacionOperacion Operacion(Guid propietario, Guid operadorTenant) =>
            AsignacionOperacion.Externa(propietario, operadorTenant, ServicioCae.Outbound, AmbitoAsignacion.Universal, desde, null, ahora);
        AsignacionCartera Cartera(AsignacionOperacion op, Guid usuario, Empresa cliente, DateTime? vigenciaDesde = null) =>
            AsignacionCartera.Externa(op, usuario, Roles.GestorCae, AmbitoAsignacion.DeRelacionCliente(cliente.Id), vigenciaDesde ?? desde, null, ahora);

        var op1 = Operacion(_p1, _operador);
        var op2 = Operacion(_p2, _operador);
        var op3 = Operacion(_p3, _operador);
        var opAjena = Operacion(_tenantReal, _otroOperador);
        var opRealPropia = Operacion(_tenantReal2, _operador);
        contexto.AsignacionesOperacion.AddRange(op1, op2, op3, opAjena, opRealPropia);
        contexto.AsignacionesCartera.AddRange(
            Cartera(op1, _g1, _c1a), Cartera(op2, _g1, _c2),
            Cartera(op1, _g2, _c1b), Cartera(op3, _g2, _c3),
            Cartera(op1, _usuarioCliente, c1c),
            Cartera(opAjena, _gDeOtroOperador, cReal),
            Cartera(opRealPropia, _gSoloTenantReal, cReal2),
            Cartera(op1, _gCarteraFutura, c1d, vigenciaDesde: ahora.AddDays(1)));

        // Lista multi-Tenant real de la cuenta de demo (vía heredada): los tres Tenants propietarios.
        foreach (var propietario in new[] { _p1, _p2, _p3 })
        {
            var delegacion = new DelegacionTenant(_operador, propietario);
            contexto.DelegacionesTenant.Add(delegacion);
            contexto.AsignacionesOperadorDelegado.Add(new AsignacionOperadorDelegado(delegacion.Id, _admin, Roles.Administrador));
        }

        await contexto.SaveChangesAsync();
    }

    public async Task DisposeAsync() => await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    // ---------------------------------------------------------------- 1. FIDELIDAD

    [Theory]
    [InlineData(1, 1)]
    [InlineData(1, 2)]
    [InlineData(1, 3)]
    [InlineData(2, 1)]
    [InlineData(2, 2)]
    [InlineData(2, 3)]
    public async Task La_lente_Gestor_muestra_lo_mismo_que_ve_ese_gestor_con_su_propia_cuenta(int gestor, int propietario)
    {
        var gestorId = gestor == 1 ? _g1 : _g2;
        var tenant = propietario switch { 1 => _p1, 2 => _p2, _ => _p3 };

        // Control positivo: sin lente, la cuenta de demo lo ve TODO (null = sin restricción). Si esto no
        // fuera null, la igualdad de abajo no probaría que la lente estrecha.
        var sinLente = await FotoAsync(tenant, Administrador(), Solicitud(null, null));
        sinLente.Should().Be(Foto.SinRestriccion);

        var real = await FotoAsync(tenant, GestorReal(gestorId), Solicitud(null, null));
        var lente = await FotoAsync(tenant, Administrador(), Solicitud(VistaDemo.GestorCae, gestorId));

        lente.Should().Be(real, "la vista Gestor debe mostrar lo mismo que ese Gestor CAE con su propia cuenta");
        lente.Should().NotBe(Foto.SinRestriccion);
    }

    [Fact]
    public async Task La_lente_Gestor_muestra_su_cartera_y_en_un_Tenant_sin_cartera_no_muestra_nada()
    {
        var conCartera = await FotoAsync(_p1, Administrador(), Solicitud(VistaDemo.GestorCae, _g1));
        conCartera.Clientes.Should().Be(Formato([_c1a.Id]), "G1 solo tiene la cartera del cliente 1A en el Tenant 1");
        conCartera.Centros.Should().NotBe(Formato([]));
        conCartera.Trabajadores.Should().NotBe(Formato([]));

        var sinCartera = await FotoAsync(_p3, Administrador(), Solicitud(VistaDemo.GestorCae, _g1));
        sinCartera.Clientes.Should().Be(Formato([]), "G1 no tiene cartera en el Tenant 3: vacío, nunca null (que sería 'todo')");
        sinCartera.AccesoTotal.Should().BeFalse();
    }

    // ---------------------------------------------------------------- 2. NUNCA AMPLÍA

    private enum Caso
    {
        Desactivada, RolConsulta, RolCliente, SinRolDeOrigen, TenantDeOrigenReal, TenantActivoReal, GestorInexistente,
        GestorSinCartera, GestorConCarteraFutura, UsuarioConCarteraPeroRolCliente, GestorDeOtroOperador, GestorSoloConCarteraEnTenantReal,
        VistaGestorSinGestor, SesionPrivilegiadaDePlataforma,
    }

    public static IEnumerable<object[]> PeticionesQueNoDebenCambiarNada() =>
        Enum.GetNames<Caso>().Select(nombre => new object[] { nombre });

    [Theory]
    [MemberData(nameof(PeticionesQueNoDebenCambiarNada))]
    public async Task Una_peticion_invalida_deja_el_resultado_exactamente_igual_que_sin_lente(string nombreCaso)
    {
        var caso = Enum.Parse<Caso>(nombreCaso);
        ISesionPrivilegiadaActual? sesion = null;
        var tenantActivo = _p1;
        var usuario = Administrador();
        var opciones = Activo(true);
        SolicitudFalsa solicitud;

        switch (caso)
        {
            case Caso.Desactivada:
                opciones = Activo(false);
                solicitud = Solicitud(VistaDemo.GestorCae, _g1);
                break;
            case Caso.RolConsulta:
                usuario = Administrador(Roles.Consulta);
                solicitud = Solicitud(VistaDemo.GestorCae, _g1);
                break;
            case Caso.RolCliente:
                usuario = Administrador(Roles.Cliente);
                solicitud = Solicitud(VistaDemo.GestorCae, _g1);
                break;
            case Caso.SinRolDeOrigen:
                usuario = new CurrentUserServiceFalso(_admin, null, _operador);
                solicitud = Solicitud(VistaDemo.GestorCae, _g1);
                break;
            case Caso.TenantDeOrigenReal:
                usuario = new CurrentUserServiceFalso(_admin, Roles.Administrador, _tenantReal);
                solicitud = Solicitud(VistaDemo.GestorCae, _g1);
                break;
            case Caso.TenantActivoReal:
                tenantActivo = _tenantReal;
                solicitud = Solicitud(VistaDemo.GestorCae, _g1);
                break;
            case Caso.GestorInexistente:
                solicitud = Solicitud(VistaDemo.GestorCae, Guid.NewGuid());
                break;
            case Caso.GestorSinCartera:
                solicitud = Solicitud(VistaDemo.GestorCae, _gSinCartera);
                break;
            case Caso.GestorConCarteraFutura:
                solicitud = Solicitud(VistaDemo.GestorCae, _gCarteraFutura);
                break;
            case Caso.UsuarioConCarteraPeroRolCliente:
                solicitud = Solicitud(VistaDemo.GestorCae, _usuarioCliente);
                break;
            case Caso.GestorDeOtroOperador:
                solicitud = Solicitud(VistaDemo.GestorCae, _gDeOtroOperador);
                break;
            case Caso.GestorSoloConCarteraEnTenantReal:
                solicitud = Solicitud(VistaDemo.GestorCae, _gSoloTenantReal);
                break;
            case Caso.VistaGestorSinGestor:
                solicitud = Solicitud(VistaDemo.GestorCae, null);
                break;
            default:
                sesion = new SesionFalsa(new SesionPrivilegiadaActiva(
                    Guid.NewGuid(), Guid.NewGuid(), _p1, CapacidadPrivilegio.SoporteLectura, null));
                solicitud = Solicitud(VistaDemo.GestorCae, _g1);
                break;
        }

        // Lo que la cuenta veía SIN petición en esas mismas condiciones (mismo usuario, mismo rol,
        // mismo Tenant activo, mismas opciones y sesión).
        var sinPeticion = await FotoAsync(tenantActivo, usuario, Solicitud(null, null), opciones, sesion);
        var conPeticion = await FotoAsync(tenantActivo, usuario, solicitud, opciones, sesion);

        conPeticion.Should().Be(sinPeticion, "una petición inválida no puede ni estrechar ni ampliar nada");
    }

    [Fact]
    public async Task La_lente_solo_interseca_un_alcance_ya_restringido_y_nunca_lo_amplia()
    {
        // Contexto real de la cuenta: en este Tenant su rol efectivo es GestorCae con la cartera de G2
        // (el caso de un Workspace operativo derivado), aunque el rol de su organización de origen sea
        // Administrador. Pedir la vista de G1 NO puede darle la cartera de G1: ∩ de las dos = nada en
        // el Tenant 1.
        var realG2 = await FotoAsync(_p1, GestorReal(_g2), Solicitud(null, null));
        var conLente = await FotoAsync(_p1,
            new CurrentUserServiceFalso(_g2, Roles.GestorCae, _operador, rolOrigen: Roles.Administrador),
            Solicitud(VistaDemo.GestorCae, _g1));

        realG2.Clientes.Should().Be(Formato([_c1b.Id]));
        conLente.Clientes.Should().Be(Formato([]), "cartera de G2 ∩ cartera de G1 en el Tenant 1 es vacío; nunca la de G1");
        conLente.AccesoTotal.Should().BeFalse();
    }

    [Fact]
    public async Task Una_cuenta_con_rol_Gestor_real_no_cambia_de_alcance_por_pedir_otra_vista()
    {
        var real = await FotoAsync(_p1, GestorReal(_g2), Solicitud(null, null));
        var pidiendoOtro = await FotoAsync(_p1, GestorReal(_g2), Solicitud(VistaDemo.GestorCae, _g1));

        pidiendoOtro.Should().Be(real, "el rol GestorCae no es el rol de demo: la petición no llega a validarse");
    }

    [Fact]
    public async Task Las_vistas_Direccion_y_Coordinador_no_cambian_ningun_alcance()
    {
        foreach (var vista in new[] { VistaDemo.Direccion, VistaDemo.CoordinadorCae })
            (await FotoAsync(_p1, Administrador(), Solicitud(vista, null))).Should().Be(Foto.SinRestriccion);
    }

    // ---------------------------------------------------------------- Disponibilidad y Tenants

    [Fact]
    public async Task El_selector_esta_disponible_para_la_cuenta_de_demo_con_independencia_de_la_vista_y_del_Tenant_activo()
    {
        // Ninguna vista (ni un Tenant activo sin lente) puede esconder el selector: la disponibilidad
        // solo mira identidad real y Tenant de origen.
        foreach (var solicitud in new[]
                 {
                     Solicitud(null, null),
                     Solicitud(VistaDemo.CoordinadorCae, null),
                     Solicitud(VistaDemo.GestorCae, _g1),
                 })
            foreach (var tenant in new[] { _operador, _p1, _p3, _tenantReal })
            {
                await using var contexto = CrearContexto(tenant);
                var vista = CrearVista(contexto, tenant, Administrador(), solicitud, Activo(true), null);
                (await vista.EstaDisponibleAsync()).Should().BeTrue();
            }
    }

    /// <summary>
    /// Decisión P7 (2026-09-23), defecto real. Con un Workspace operativo derivado seleccionado,
    /// <c>RolEfectivoDelWorkspaceMiddleware</c> sustituye el claim de rol por el de la cartera en el
    /// Tenant propietario — que nunca es Administrador ni DireccionCae (P8) — y <c>VistaDemoCookie</c>
    /// leía ese claim como «rol de sesión». La cuenta de demo perdía el selector justo al entrar en un
    /// Tenant propietario, contra el contrato de arriba: la disponibilidad depende de la identidad y
    /// del Tenant de origen, nunca del Tenant activo.
    /// </summary>
    [Fact]
    public async Task El_selector_sigue_disponible_en_un_Workspace_operativo_derivado_donde_el_rol_efectivo_es_el_de_la_cartera()
    {
        var enWorkspace = new CurrentUserServiceFalso(_admin, Roles.GestorCae, _operador, rolOrigen: Roles.Administrador);

        await using var contexto = CrearContexto(_p1);
        var vista = CrearVista(contexto, _p1, enWorkspace, Solicitud(null, null), Activo(true), null);

        (await vista.EstaDisponibleAsync()).Should().BeTrue(
            "el rol que decide es el de la organización de origen (Administrador), no el efectivo en el Tenant propietario");
    }

    [Fact]
    public async Task El_selector_no_existe_para_una_cuenta_o_Tenant_real_ni_con_la_funcion_apagada()
    {
        await using var contexto = CrearContexto(_p1);
        var casos = new (CurrentUserServiceFalso Usuario, SolicitudFalsa Solicitud, IOptions<VistaDemoOptions> Opciones)[]
        {
            (new CurrentUserServiceFalso(_admin, Roles.Administrador, _tenantReal), Solicitud(null, null), Activo(true)),
            (Administrador(), Solicitud(null, null), Activo(false)),
            (Administrador(Roles.Consulta), Solicitud(null, null), Activo(true)),
        };

        foreach (var caso in casos)
            (await CrearVista(contexto, _p1, caso.Usuario, caso.Solicitud, caso.Opciones, null).EstaDisponibleAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task Los_gestores_elegibles_son_solo_los_del_Operador_con_cartera_vigente_y_rol_Gestor()
    {
        await using var contexto = CrearContexto(_operador);
        var elegibles = await CrearVista(contexto, _operador, Administrador(), Solicitud(null, null), Activo(true), null)
            .ObtenerGestoresElegiblesAsync();

        elegibles.Select(g => g.UsuarioId).Should().BeEquivalentTo([_g1, _g2]);
    }

    [Fact]
    public async Task La_lista_multi_Tenant_del_Gestor_se_acota_a_sus_Tenants_y_nunca_anade_uno()
    {
        var sinLente = await ClientesAutorizadosAsync(Solicitud(null, null));
        sinLente.Select(c => c.TenantId).Should().BeEquivalentTo([_operador, _p1, _p2, _p3]);

        var lenteG1 = await ClientesAutorizadosAsync(Solicitud(VistaDemo.GestorCae, _g1));
        lenteG1.Select(c => c.TenantId).Should().BeEquivalentTo([_operador, _p1, _p2], "G1 tiene cartera en los Tenants 1 y 2, no en el 3");
        lenteG1.Should().OnlyContain(c => sinLente.Any(s => s.TenantId == c.TenantId), "la lente solo quita entradas");

        var lenteG2 = await ClientesAutorizadosAsync(Solicitud(VistaDemo.GestorCae, _g2));
        lenteG2.Select(c => c.TenantId).Should().BeEquivalentTo([_operador, _p1, _p3]);
    }

    // ---------------------------------------------------------------- Infraestructura de la prueba

    private sealed record Foto(bool AccesoTotal, string Clientes, string Centros, string Empresas, string Subcontratas, string Trabajadores, string Vehiculos)
    {
        public static readonly Foto SinRestriccion = new(true, "null", "null", "null", "null", "null", "null");
    }

    private static string Formato(IEnumerable<Guid>? ids) => ids is null ? "null" : string.Join(",", ids.OrderBy(i => i));

    private async Task<Foto> FotoAsync(
        Guid tenantActivo, CurrentUserServiceFalso usuario, SolicitudFalsa solicitud,
        IOptions<VistaDemoOptions>? opciones = null, ISesionPrivilegiadaActual? sesion = null)
    {
        await using var contexto = CrearContexto(tenantActivo);
        var tenant = new TenantActualAmbiental { TenantId = tenantActivo };
        var sesionEfectiva = sesion ?? new SesionPrivilegiadaAusente();
        var vista = new VistaDemoActual(contexto, new PuertaAccesoDatos(), usuario, tenant, sesionEfectiva, solicitud, opciones ?? Activo(true));
        var servicio = new AlcanceDatosService(contexto, usuario, tenant, sesionEfectiva, vista);

        return new Foto(
            await servicio.TieneAccesoTotalAsync(),
            Formato(await servicio.ObtenerClienteIdsVisiblesAsync()),
            Formato(await servicio.ObtenerCentroIdsVisiblesAsync()),
            Formato(await servicio.ObtenerEmpresaIdsVisiblesAsync()),
            Formato(await servicio.ObtenerSubcontrataIdsVisiblesAsync()),
            Formato(await servicio.ObtenerTrabajadorIdsVisiblesAsync()),
            Formato(await servicio.ObtenerVehiculoIdsVisiblesAsync()));
    }

    private async Task<IReadOnlyList<ClienteAutorizadoDto>> ClientesAutorizadosAsync(SolicitudFalsa solicitud)
    {
        await using var contexto = CrearContexto(_operador);
        var usuario = Administrador();
        var vista = CrearVista(contexto, _operador, usuario, solicitud, Activo(true), null);
        return await new ObtenerClientesAutorizadosQueryHandler(contexto, usuario, vista)
            .Handle(new ObtenerClientesAutorizadosQuery(), CancellationToken.None);
    }

    private static VistaDemoActual CrearVista(
        CaeManagerDbContext contexto, Guid tenantActivo, CurrentUserServiceFalso usuario, SolicitudFalsa solicitud,
        IOptions<VistaDemoOptions> opciones, ISesionPrivilegiadaActual? sesion) =>
        new(contexto, new PuertaAccesoDatos(), usuario, new TenantActualAmbiental { TenantId = tenantActivo }, sesion ?? new SesionPrivilegiadaAusente(), solicitud, opciones);

    private CurrentUserServiceFalso Administrador(string rol = Roles.Administrador) => new(_admin, rol, _operador);

    private CurrentUserServiceFalso GestorReal(Guid gestorId) => new(gestorId, Roles.GestorCae, _operador);

    private static IOptions<VistaDemoOptions> Activo(bool activo) => Options.Create(new VistaDemoOptions { Activo = activo });

    private static SolicitudFalsa Solicitud(VistaDemo? vista, Guid? gestor) => new(new PeticionVistaDemo(vista, gestor));

    private sealed class SolicitudFalsa(PeticionVistaDemo peticion) : ISolicitudVistaDemo
    {
        public Task<PeticionVistaDemo> ObtenerAsync() => Task.FromResult(peticion);
    }

    private sealed class SesionFalsa(SesionPrivilegiadaActiva sesion) : ISesionPrivilegiadaActual
    {
        public Task<SesionPrivilegiadaActiva?> ObtenerAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<SesionPrivilegiadaActiva?>(sesion);

        public Task<SesionPrivilegiadaActiva?> RevalidarAsync(CancellationToken cancellationToken = default) => ObtenerAsync(cancellationToken);
    }

    private static async Task<Guid> ObtenerORolAsync(CaeManagerDbContext contexto, string nombre)
    {
        var existente = await contexto.Roles.Where(r => r.Name == nombre).Select(r => (Guid?)r.Id).FirstOrDefaultAsync();
        if (existente is { } id) return id;

        var rol = new IdentityRole<Guid>(nombre) { NormalizedName = nombre.ToUpperInvariant() };
        contexto.Roles.Add(rol);
        await contexto.SaveChangesAsync();
        return rol.Id;
    }

    private async Task<Empresa> SembrarClienteAsync(Guid tenant, string nombre, string cif, string? dniTrabajador)
    {
        await using var contexto = CrearContexto(tenant);
        var cliente = Empresa.CrearComoCliente(nombre, cif, false, null, null);
        var contratista = new Empresa($"Contrata de {nombre}", null);
        contexto.Empresas.AddRange(cliente, contratista);
        await contexto.SaveChangesAsync();

        var centro = new Centro(cliente.Id, contratista.Id, $"Centro de {nombre}", "Calle Falsa 1");
        contexto.Centros.Add(centro);
        if (dniTrabajador is not null)
        {
            var trabajador = Trabajador.DeEmpresa(contratista.Id, "Ana", "Pérez", dniTrabajador);
            contexto.Trabajadores.Add(trabajador);
            await contexto.SaveChangesAsync();
            contexto.Asignaciones.Add(new Asignacion(trabajador.Id, centro.Id, DateOnly.FromDateTime(DateTime.UtcNow)));
        }

        await contexto.SaveChangesAsync();
        return cliente;
    }

    private CaeManagerDbContext CrearContexto(Guid tenantSellado)
    {
        var tenantActual = new TenantActualAmbiental { TenantId = tenantSellado };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }
}
