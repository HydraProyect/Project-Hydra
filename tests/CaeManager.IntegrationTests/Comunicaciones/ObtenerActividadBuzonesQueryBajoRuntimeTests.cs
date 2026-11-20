using CaeManager.Application.Common;
using CaeManager.Application.Comercial.LineaOutbound;
using CaeManager.Application.DependencyInjection;
using CaeManager.Application.Integraciones;
using CaeManager.Application.Plataforma;
using CaeManager.Application.Tenants;
using CaeManager.Domain.AltaAutonoma;
using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Comercial;
using CaeManager.Domain.Common;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Integraciones;
using CaeManager.Domain.Operaciones;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Autorizacion;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Interceptors;
using CaeManager.IntegrationTests.Arranque;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CaeManager.IntegrationTests.Comunicaciones;

/// <summary>
/// Las columnas «Último correo» y «Abiertas» de la tabla de Buzones (<c>ObtenerActividadBuzonesQuery</c>, PR-B1) en
/// PostgreSQL real, bajo <c>cae_app_runtime</c> con los interceptores de sellado y de sesión RLS, y por el mismo camino que
/// la página: el puerto <see cref="IActividadBuzones"/> de <c>AddApplication</c>, que envía la petición por el mediador
/// real con todo su pipeline (la guarda que fija el ámbito del Tenant solo Inbound incluida), con el
/// <see cref="AlcanceDatosService"/> y el <see cref="ICriterioDeOutbound"/> reales. Los tests de Application usan dobles
/// del contexto y fijan el ámbito a mano; aquí la traducción a SQL, la RLS y la guarda son las de verdad.
///
/// <para>
/// Tres Tenants: el propio (sin filas de línea: el Outbound histórico), uno ajeno y uno solo Inbound (con la línea Inbound
/// declarada «solo Inbound», opción A'). En el propio hay dos buzones compartidos, el personal del Administrador y el de
/// otro Gestor CAE, con hilos de varios estados y anclas.
/// </para>
/// </summary>
public class ObtenerActividadBuzonesQueryBajoRuntimeTests : IAsyncLifetime
{
    private static readonly DateTime Base = new(2026, 11, 18, 9, 0, 0, DateTimeKind.Utc);

    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _administrador = Guid.NewGuid();
    private readonly Guid _gestorConCartera = Guid.NewGuid();
    private readonly Guid _otroGestor = Guid.NewGuid();
    private readonly List<IAsyncDisposable> _liberar = [];
    private CaeManagerDbContext _propietario = null!;
    private int _hilos;

    private Guid _tenant;
    private Guid _tenantAjeno;
    private Guid _tenantSoloInbound;
    private Guid _clienteEnCartera;

    private Guid _compartido;
    private Guid _otroCompartido;
    private Guid _personalPropio;
    private Guid _personalDeOtroGestor;
    private Guid _deOtroTenant;
    private Guid _deSoloInbound;

    public async Task InitializeAsync()
    {
        var tenantPorAmbito = new TenantActualPorAmbito();
        var opcionesPropietario = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantPorAmbito))
            .Options;
        _propietario = new CaeManagerDbContext(opcionesPropietario, new EphemeralDataProtectionProvider(), tenantPorAmbito);
        await _propietario.Database.MigrateAsync();

        var propio = new Tenant("Tenant propio");
        var ajeno = new Tenant("Tenant ajeno");
        var soloInbound = new Tenant("Tenant solo Inbound");
        _propietario.Tenants.AddRange(propio, ajeno, soloInbound);
        await _propietario.SaveChangesAsync();
        (_tenant, _tenantAjeno, _tenantSoloInbound) = (propio.Id, ajeno.Id, soloInbound.Id);

        await SembrarTenantPropioAsync();
        await SembrarTenantAjenoAsync();
        await SembrarTenantSoloInboundAsync();
    }

    /// <summary>
    /// En el buzón compartido: un hilo abierto del Cliente empresarial en cartera con dos correos entrantes y una respuesta
    /// nuestra posterior, uno abierto de otro Cliente empresarial, uno resuelto del Cliente en cartera y uno abierto sin
    /// ancla (triaje). Uno abierto en el otro compartido, otro en el buzón personal del Administrador y otro en el de otro
    /// Gestor CAE. La Gestora CAE con cartera solo tiene el primer Cliente empresarial.
    /// </summary>
    private async Task SembrarTenantPropioAsync()
    {
        using var ambito = AmbitoTenantExplicito.Establecer(_tenant);
        var enCartera = Empresa.CrearComoCliente("Cliente empresarial en cartera", "B10380194", false, null, null);
        var fueraDeCartera = Empresa.CrearComoCliente("Cliente empresarial fuera de cartera", "B12345674", false, null, null);
        _propietario.Empresas.AddRange(enCartera, fueraDeCartera);
        await _propietario.SaveChangesAsync();
        _clienteEnCartera = enCartera.Id;

        _compartido = await BuzonAsync("compartido@propio.test");
        _otroCompartido = await BuzonAsync("otro-compartido@propio.test");
        _personalPropio = await BuzonAsync("administrador@propio.test", gestor: _administrador);
        _personalDeOtroGestor = await BuzonAsync("otro-gestor@propio.test", gestor: _otroGestor);

        await HiloAsync(_compartido, EstadoConversacion.Abierta, clienteId: enCartera.Id, mensajes:
            [(DireccionMensaje.Entrante, Base), (DireccionMensaje.Entrante, Base.AddHours(2)), (DireccionMensaje.Saliente, Base.AddHours(9))]);
        await HiloAsync(_compartido, EstadoConversacion.Abierta, clienteId: fueraDeCartera.Id, mensajes:
            [(DireccionMensaje.Entrante, Base.AddHours(5))]);
        await HiloAsync(_compartido, EstadoConversacion.Resuelta, clienteId: enCartera.Id, mensajes:
            [(DireccionMensaje.Entrante, Base.AddHours(3))]);
        await HiloAsync(_compartido, EstadoConversacion.Abierta, mensajes: [(DireccionMensaje.Entrante, Base.AddHours(4))]);
        await HiloAsync(_otroCompartido, EstadoConversacion.Abierta, clienteId: enCartera.Id, mensajes:
            [(DireccionMensaje.Entrante, Base.AddHours(6))]);
        await HiloAsync(_personalPropio, EstadoConversacion.Abierta, mensajes: [(DireccionMensaje.Entrante, Base.AddHours(7))]);
        await HiloAsync(_personalDeOtroGestor, EstadoConversacion.Abierta, mensajes: [(DireccionMensaje.Entrante, Base.AddHours(8))]);

        // Asignación de Operación interna acotada al Cliente empresarial en cartera, y bajo ella la Asignación de Cartera
        // de la Gestora CAE (el mismo patrón que VisibilidadTriageTests).
        var ahora = DateTime.UtcNow;
        var operacion = AsignacionOperacion.Interna(
            _tenant, ServicioCae.Outbound, AmbitoAsignacion.DeRelacionCliente(enCartera.Id), ahora, null, ahora);
        _propietario.AsignacionesOperacion.Add(operacion);
        _propietario.AsignacionesCartera.Add(
            AsignacionCartera.Interna(operacion, _gestorConCartera, AmbitoAsignacion.Universal, ahora, null, ahora));
        await _propietario.SaveChangesAsync();
    }

    private async Task SembrarTenantAjenoAsync()
    {
        using var ambito = AmbitoTenantExplicito.Establecer(_tenantAjeno);
        _deOtroTenant = await BuzonAsync("compartido@ajeno.test");
        await HiloAsync(_deOtroTenant, EstadoConversacion.Abierta, mensajes: [(DireccionMensaje.Entrante, Base.AddHours(10))]);
    }

    /// <summary>
    /// Un hilo abierto anclado a una Empresa, uno anclado a un Cliente empresarial y uno sin ancla, cada uno más reciente que
    /// el anterior: con el ámbito de D6=a solo cuenta el primero.
    /// </summary>
    private async Task SembrarTenantSoloInboundAsync()
    {
        using var ambito = AmbitoTenantExplicito.Establecer(_tenantSoloInbound);
        var cliente = Empresa.CrearComoCliente("Cliente empresarial solo Inbound", "B10380194", false, null, null);
        var empresa = new Empresa("Empresa contratista solo Inbound", "B10380186");
        _propietario.Empresas.AddRange(cliente, empresa);
        await _propietario.SaveChangesAsync();

        _deSoloInbound = await BuzonAsync("compartido@solo-inbound.test");
        await HiloAsync(_deSoloInbound, EstadoConversacion.Abierta, empresaId: empresa.Id, mensajes: [(DireccionMensaje.Entrante, Base)]);
        await HiloAsync(_deSoloInbound, EstadoConversacion.Abierta, clienteId: cliente.Id, mensajes:
            [(DireccionMensaje.Entrante, Base.AddHours(3))]);
        await HiloAsync(_deSoloInbound, EstadoConversacion.Abierta, mensajes: [(DireccionMensaje.Entrante, Base.AddHours(6))]);

        // Como SoloInboundGuardaYAlcanceBajoRuntimeTests: la línea se siembra con el contexto RLS firmado del Tenant.
        await using var siembra = ContextoDeSiembraConRls(_tenantSoloInbound);
        var hoy = DateTime.UtcNow;
        siembra.ProductosContratados.Add(ProductoContratado.Contratar(
            LineaProductoContratado.Inbound, "cus_InboundActividadBuzones", TramoTrabajadoresActivos.De1A10,
            ModalidadSuscripcionAlta.Anual, hoy.AddDays(-30), ahoraUtc: hoy, declaraSoloInbound: true));
        await siembra.SaveChangesAsync();
    }

    private async Task<Guid> BuzonAsync(string email, Guid? gestor = null)
    {
        var conexion = new ConexionIntegracion(email, email, gestorPropietarioId: gestor);
        _propietario.ConexionesIntegracion.Add(conexion);
        await _propietario.SaveChangesAsync();
        return conexion.Id;
    }

    private async Task HiloAsync(
        Guid buzon, EstadoConversacion estado, Guid? clienteId = null, Guid? empresaId = null,
        (DireccionMensaje Direccion, DateTime FechaUtc)[]? mensajes = null)
    {
        var hilo = new Conversacion($"Hilo {++_hilos}", clienteId: clienteId, empresaId: empresaId);
        hilo.AsociarConexion(buzon, $"hilo-externo-actividad-{_hilos}");
        if (estado != EstadoConversacion.Abierta) hilo.CambiarEstado(estado).EsExitoso.Should().BeTrue();
        _propietario.Conversaciones.Add(hilo);
        await _propietario.SaveChangesAsync();

        foreach (var (direccion, fechaUtc) in mensajes ?? [])
            _propietario.Mensajes.Add(new Mensaje(
                hilo.Id, direccion, CanalConversacion.Correo, "remitente@externo.test", "<p>Texto</p>", fechaUtc));
        await _propietario.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        foreach (var recurso in _liberar)
            await recurso.DisposeAsync();
        await _propietario.DisposeAsync();
        await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);
    }

    [Fact]
    public async Task Devuelve_el_ultimo_correo_entrante_y_las_abiertas_de_cada_buzon_y_no_el_buzon_personal_de_otro_Gestor_CAE()
    {
        var composicion = ComponerAdministrador(_tenant);

        var actividad = await composicion.Actividad.ObtenerAsync(
            [_compartido, _otroCompartido, _personalPropio, _personalDeOtroGestor]);

        // «Último correo»: el entrante más reciente de cualquier estado (el resuelto de las 12:00 UTC no gana al de las
        // 14:00 UTC), nunca la respuesta nuestra de las 18:00 UTC. «Abiertas»: las tres abiertas, no la resuelta.
        actividad[_compartido].Should().Be(new ActividadBuzonDto(Base.AddHours(5), 3));
        actividad[_otroCompartido].Should().Be(new ActividadBuzonDto(Base.AddHours(6), 1));
        actividad[_personalPropio].Should().Be(new ActividadBuzonDto(Base.AddHours(7), 1));
        actividad.Keys.Should().BeEquivalentTo([_compartido, _otroCompartido, _personalPropio],
            "el buzón personal de otro Gestor CAE no se mide ni para el Administrador: la tabla enseña «—»");
    }

    [Fact]
    public async Task Un_buzon_de_otro_Tenant_no_sale_en_la_respuesta_ni_suma_a_los_propios()
    {
        var composicion = ComponerAdministrador(_tenant);

        // Control del instrumento: la conexión existe, y la RLS la oculta al Tenant propio aunque se quiten los filtros.
        (await _propietario.ConexionesIntegracion.IgnoreQueryFilters().AnyAsync(c => c.Id == _deOtroTenant)).Should().BeTrue();
        (await composicion.Runtime.ConexionesIntegracion.IgnoreQueryFilters().AnyAsync(c => c.Id == _deOtroTenant))
            .Should().BeFalse("app.tenant_id es el Tenant propio: la RLS oculta la conexión del Tenant ajeno");

        var actividad = await composicion.Actividad.ObtenerAsync([_compartido, _deOtroTenant]);

        actividad.Keys.Should().BeEquivalentTo([_compartido], "un id de otro Tenant no es un buzón medible: falta y la tabla enseña «—»");
        actividad[_compartido].Should().Be(new ActividadBuzonDto(Base.AddHours(5), 3),
            "el hilo del Tenant ajeno, más reciente, no entra en las cifras del buzón propio");
    }

    [Fact]
    public async Task Con_cartera_acotada_no_cuenta_los_hilos_que_la_Gestora_CAE_no_ve()
    {
        var usuario = new CurrentUserServiceFalso(_gestorConCartera, Roles.GestorCae, tenantOrigenId: _tenant);
        var composicion = Componer(_tenant, usuario);

        var actividad = await composicion.Actividad.ObtenerAsync([_compartido, _otroCompartido]);

        // Ve el abierto y el resuelto de su Cliente empresarial; no el del otro Cliente empresarial (14:00 UTC) ni el
        // triaje (13:00 UTC), que el Administrador sí cuenta en el primer test.
        actividad[_compartido].Should().Be(new ActividadBuzonDto(Base.AddHours(3), 1));
        actividad[_otroCompartido].Should().Be(new ActividadBuzonDto(Base.AddHours(6), 1));
    }

    [Fact]
    public async Task Un_Tenant_solo_Inbound_solo_cuenta_los_hilos_anclados_a_una_Empresa_porque_la_guarda_fija_el_ambito()
    {
        var composicion = ComponerAdministrador(_tenantSoloInbound);
        AmbitoComunicacionesSoloInbound.Activo.Should().BeFalse("el test no fija el ámbito: lo tiene que fijar la guarda del pipeline");

        var actividad = await composicion.Actividad.ObtenerAsync([_deSoloInbound]);

        // Sin el ámbito contarían los tres hilos y el último correo sería el del hilo sin ancla (15:00 UTC).
        actividad[_deSoloInbound].Should().Be(new ActividadBuzonDto(Base, 1));
    }

    [Fact]
    public async Task El_numero_de_sentencias_es_fijo_y_no_crece_con_el_numero_de_buzones()
    {
        var composicion = ComponerAdministrador(_tenant);
        IReadOnlyCollection<Guid> uno = [_compartido];
        IReadOnlyCollection<Guid> varios = [_compartido, _otroCompartido, _personalPropio, _personalDeOtroGestor, _deOtroTenant];

        async Task<int> MedirAsync(IReadOnlyCollection<Guid> buzones)
        {
            await composicion.Actividad.ObtenerAsync(buzones); // calentamiento: modelo de EF y alcance memoizado
            composicion.Runtime.ChangeTracker.Clear();
            var (_, sentencias) = await ArnesPilotoOutbound.Contador.ContarAsync(() => composicion.Actividad.ObtenerAsync(buzones));
            composicion.Runtime.ChangeTracker.Clear();
            return sentencias;
        }

        var conUno = await MedirAsync(uno);
        var conVarios = await MedirAsync(varios);

        conVarios.Should().Be(conUno, "sin N+1: cinco buzones cuestan lo mismo que uno");
        // Techo fijado en lo medido, exacto a propósito para que el verde sea la medición: los buzones medibles, las
        // conexiones que excluye ConversacionesVisibles, las abiertas y los últimos correos. Si una mejora lo baja, se baja.
        conUno.Should().Be(4, $"presupuesto SQL de la actividad de los buzones; medido {conUno} con uno y {conVarios} con cinco");
    }

    private sealed record Composicion(IActividadBuzones Actividad, CaeManagerDbContext Runtime);

    private Composicion ComponerAdministrador(Guid tenant) =>
        Componer(tenant, new CurrentUserServiceFalso(_administrador, Roles.Administrador, tenant, papel: PapelActivo.TenantDeOrigen));

    /// <summary>La composición de SoloInboundGuardaYAlcanceBajoRuntimeTests, con Comunicaciones e Integraciones.</summary>
    private Composicion Componer(Guid tenant, ICurrentUserService usuario)
    {
        var tenantDeLaPeticion = new TenantActualDeLaPeticion(tenant);
        var opcionesRuntime = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(BaseDatosPostgresDePruebas.CadenaComoRuntime(_cadenaConexion))
            .AddInterceptors(
                new TenantSelladoInterceptor(tenantDeLaPeticion),
                new TenantRlsConnectionInterceptor(tenantDeLaPeticion, new SinClienteActivo(), usuario, BaseDatosPostgresDePruebas.FirmanteContextoRls),
                ArnesPilotoOutbound.Contador)
            .Options;
        var runtime = new CaeManagerDbContext(opcionesRuntime, new EphemeralDataProtectionProvider(), tenantDeLaPeticion);
        _liberar.Add(runtime);

        var servicios = new ServiceCollection();
        servicios.AddApplication();
        servicios.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        servicios.AddLogging();
        servicios.AddSingleton<ITenantActual>(tenantDeLaPeticion);
        servicios.AddSingleton<IUnitOfWork>(runtime);
        servicios.AddSingleton<ITenantsQueryContext>(runtime);
        servicios.AddSingleton<ILineaOutboundQueryContext>(runtime);
        servicios.AddSingleton<CaeManager.Application.Operaciones.IOperacionesQueryContext>(runtime);
        servicios.AddSingleton<CaeManager.Application.Empresas.IEmpresasQueryContext>(runtime);
        servicios.AddSingleton<CaeManager.Application.Proyectos.IProyectosQueryContext>(runtime);
        servicios.AddSingleton<CaeManager.Application.Proyectos.IRequisitosEventoQueryContext>(runtime);
        servicios.AddSingleton<CaeManager.Application.Centros.ICentrosQueryContext>(runtime);
        servicios.AddSingleton<CaeManager.Application.EstadoMaterializado.IEstadoMaterializadoQueryContext>(runtime);
        servicios.AddSingleton<CaeManager.Application.EstadoMaterializado.Frescura.ILectorVersionEstadoTenant>(new CaeManager.Infrastructure.EstadoMaterializado.LectorVersionEstadoTenant(runtime));
        servicios.AddSingleton<CaeManager.Application.Trabajadores.ITrabajadoresQueryContext>(runtime);
        servicios.AddSingleton<CaeManager.Application.TiposDocumento.ITiposDocumentoQueryContext>(runtime);
        servicios.AddSingleton<CaeManager.Application.Documentos.IDocumentosQueryContext>(runtime);
        servicios.AddSingleton<CaeManager.Application.DocumentosIa.IDocumentosIaQueryContext>(runtime);
        servicios.AddSingleton<CaeManager.Application.Asignaciones.IAsignacionesQueryContext>(runtime);
        servicios.AddSingleton<CaeManager.Application.Configuracion.IConfiguracionQueryContext>(runtime);
        servicios.AddSingleton<CaeManager.Application.Visitas.IVisitasQueryContext>(runtime);
        servicios.AddSingleton<CaeManager.Application.Comunicaciones.IComunicacionesQueryContext>(runtime);
        servicios.AddSingleton<CaeManager.Application.Integraciones.IProveedoresPlataformaCaeQueryContext>(runtime);
        servicios.AddSingleton<IIntegracionesQueryContext>(runtime);
        servicios.AddSingleton<ICurrentUserService>(usuario);
        servicios.AddSingleton<IAlcanceDatosService>(sp => new AlcanceDatosService(
            runtime, usuario, tenantDeLaPeticion, new SesionPrivilegiadaAusente(),
            criterioDeOutbound: sp.GetRequiredService<ICriterioDeOutbound>()));
        var proveedor = servicios.BuildServiceProvider();
        _liberar.Add(proveedor);
        return new Composicion(proveedor.GetRequiredService<IActividadBuzones>(), runtime);
    }

    private CaeManagerDbContext ContextoDeSiembraConRls(Guid tenant)
    {
        var tenantActual = new TenantActualDeLaPeticion(tenant);
        var opciones = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion)
            .AddInterceptors(
                new TenantSelladoInterceptor(tenantActual),
                new TenantRlsConnectionInterceptor(tenantActual, new SinClienteActivo(),
                    new CurrentUserServiceFalso(_administrador, tenantOrigenId: tenant), BaseDatosPostgresDePruebas.FirmanteContextoRls))
            .Options;
        return new CaeManagerDbContext(opciones, new EphemeralDataProtectionProvider(), tenantActual);
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
