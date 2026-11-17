using CaeManager.Application.Common;
using CaeManager.Application.Operaciones;
using CaeManager.Application.Tenants;
using CaeManager.Domain.Operaciones;
using CaeManager.Domain.Soporte;
using CaeManager.Domain.Tenants;
using CaeManager.Web.Features.Tenants;
using CaeManager.Web.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

public class ClienteActivoEndpointsAutorizacionTests
{
    private static readonly Guid Usuario = Guid.NewGuid();
    private static readonly Guid Origen = Guid.NewGuid();
    private static readonly Guid Destino = Guid.NewGuid();
    private readonly IDataProtectionProvider _protector = new EphemeralDataProtectionProvider();

    [Fact]
    public async Task El_Tenant_de_origen_borra_la_seleccion_anterior()
    {
        await using var db = await CrearContexto();
        var http = Contexto();
        var resultado = await Cambiar(Origen, http, db);
        resultado.Should().BeOfType<RedirectHttpResult>();
        http.Response.Headers.SetCookie.Should().Contain(c => c!.StartsWith(ClienteActivoSeleccionado.NombreCookie + "=;", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task La_via_heredada_exige_Delegacion_activa_y_asignacion_del_usuario(bool activa, bool propia)
    {
        await using var db = await CrearContexto();
        var delegacion = new DelegacionTenant(Origen, Destino);
        if (!activa) delegacion.Desactivar();
        db.AddRange(delegacion, new AsignacionOperadorDelegado(delegacion.Id, propia ? Usuario : Guid.NewGuid(), "Consulta"));
        await db.SaveChangesAsync();
        var http = Contexto();
        var resultado = await Cambiar(Destino, http, db);
        Comprobar(resultado, http, activa && propia, null);
    }

    [Theory]
    [InlineData("vigente", true)]
    [InlineData("caducada", false)]
    [InlineData("futura", false)]
    [InlineData("suspendida", false)]
    [InlineData("otro-usuario", false)]
    [InlineData("otro-operador", false)]
    [InlineData("operacion-suspendida", false)]
    public async Task El_Gestor_CAE_necesita_cartera_y_operacion_vigentes_de_su_Operador_CAE(string caso, bool permitido)
    {
        await using var db = await CrearContexto();
        var ahora = DateTime.UtcNow;
        var desde = ahora.AddDays(-2);
        var otroOperador = Guid.NewGuid();
        var operacion = AsignacionOperacion.Externa(Destino, caso == "otro-operador" ? otroOperador : Origen,
            ServicioCae.Outbound, AmbitoAsignacion.Universal, desde, null, ahora);
        var cartera = AsignacionCartera.Externa(operacion, caso == "otro-usuario" ? Guid.NewGuid() : Usuario,
            "GestorCae", AmbitoAsignacion.Universal, caso == "futura" ? ahora.AddDays(1) : desde,
            caso == "caducada" ? ahora.AddDays(-1) : null, ahora);
        if (caso == "suspendida") cartera.Suspender();
        if (caso == "operacion-suspendida") operacion.Suspender();
        // La vía de Operación exige además la DelegacionTenant de la que nació la operación, vigente (PR #2223).
        // En «otro-operador» la operación también tiene un origen válido, el de la delegación de ese otro Operador CAE
        // externo, y el propio sigue teniendo la suya: solo la niega que la operación no sea de su Operador CAE.
        var delegacion = new DelegacionTenant(Origen, Destino);
        var delegacionDelOtro = new DelegacionTenant(otroOperador, Destino);
        operacion.FijarOrigen(caso == "otro-operador" ? delegacionDelOtro : delegacion);
        db.AddRange(operacion, cartera, delegacion, delegacionDelOtro);
        await db.SaveChangesAsync();
        var http = Contexto();
        Comprobar(await Cambiar(Destino, http, db), http, permitido, permitido ? operacion.Id : null);
    }

    [Fact]
    public async Task Un_Guid_ajeno_no_sustituye_la_cookie_ni_modifica_el_contexto_de_entrada()
    {
        await using var db = await CrearContexto();
        var http = Contexto();
        var cookie = http.Request.Headers.Cookie.ToString();
        Comprobar(await Cambiar(Guid.NewGuid(), http, db), http, false, null);
        http.Request.Headers.Cookie.ToString().Should().Be(cookie);
        db.ChangeTracker.HasChanges().Should().BeFalse();
    }

    [Fact]
    public async Task El_POST_de_formulario_exige_antiforgery_en_sus_metadatos_reales()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton<ITenantsQueryContext>(_ => throw new NotSupportedException());
        builder.Services.AddSingleton<IOperacionesQueryContext>(_ => throw new NotSupportedException());
        builder.Services.AddSingleton<ICurrentUserService>(new UsuarioActual());
        builder.Services.AddSingleton<IDataProtectionProvider>(_protector);
        builder.Services.AddSingleton<IContadorPendientesSelectorTenant>(_ => throw new NotSupportedException());
        await using var app = builder.Build();
        app.MapClienteActivoEndpoints();
        var endpoint = ((IEndpointRouteBuilder)app).DataSources.SelectMany(d => d.Endpoints).Single();
        var antiforgery = endpoint.Metadata.GetMetadata<IAntiforgeryMetadata>();
        antiforgery.Should().NotBeNull();
        antiforgery!.RequiresValidation.Should().BeTrue();
        endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Should().Equal("POST");
    }

    private Task<IResult> Cambiar(Guid tenant, HttpContext http, ContextoPrueba db, ServicioCae? servicio = null, string rol = "GestorCae") =>
        ClienteActivoEndpoints.CambiarAsync(tenant, "/", http, db, new UsuarioActual(rol), db, _protector, default, servicio: servicio);

    /// <summary>
    /// B-02b (DN-21): con <c>servicio=Inbound</c> el POST solo pide la vía de Operación Inbound, amparada por la línea
    /// Inbound vigente del Tenant TCE y sin DelegacionTenant; nunca cae a la vía de Outbound ni a la heredada.
    /// </summary>
    [Theory]
    [InlineData("con-linea", "Administrador", true)]
    [InlineData("con-linea-y-cartera", "GestorCae", true)]
    [InlineData("con-linea", "GestorCae", false)]
    [InlineData("sin-linea", "Administrador", false)]
    [InlineData("cerrada", "Administrador", false)]
    public async Task Con_servicio_Inbound_solo_abre_la_operacion_Inbound_amparada_por_la_linea(string caso, string rol, bool permitido)
    {
        await using var db = await CrearContexto();
        var ahora = DateTime.UtcNow;
        var inbound = AsignacionOperacion.Externa(Destino, Origen, ServicioCae.Inbound, AmbitoAsignacion.Universal,
            ahora.AddDays(-2), null, ahora);
        if (caso == "cerrada") inbound.Cerrar(MotivoCierreAsignacion.Revocada, ahora);
        db.Add(inbound);
        if (caso == "con-linea-y-cartera")
            db.Add(AsignacionCartera.Externa(inbound, Usuario, "GestorCae", AmbitoAsignacion.Universal, ahora.AddDays(-1), null, ahora));
        if (caso != "sin-linea") db.TenantsConLineaInbound.Add(Destino);
        await db.SaveChangesAsync();

        var http = Contexto();
        Comprobar(await Cambiar(Destino, http, db, ServicioCae.Inbound, rol), http, permitido, permitido ? inbound.Id : null);
    }

    [Fact]
    public async Task Con_servicio_Inbound_no_cae_a_la_via_de_Outbound_ni_a_la_heredada()
    {
        await using var db = await CrearContexto();
        var ahora = DateTime.UtcNow;
        // Una operación Outbound amparada por su DelegacionTenant y una asignación heredada vigente sobre el mismo Tenant.
        var delegacion = new DelegacionTenant(Origen, Destino);
        var outbound = AsignacionOperacion.Externa(Destino, Origen, ServicioCae.Outbound, AmbitoAsignacion.Universal,
            ahora.AddDays(-2), null, ahora);
        outbound.FijarOrigen(delegacion);
        db.AddRange(delegacion, outbound, new AsignacionOperadorDelegado(delegacion.Id, Usuario, "Consulta"));
        db.TenantsConLineaInbound.Add(Destino);
        await db.SaveChangesAsync();

        var http = Contexto();
        Comprobar(await Cambiar(Destino, http, db, ServicioCae.Inbound, "Administrador"), http, false, null);

        var control = Contexto();
        Comprobar(await Cambiar(Destino, control, db, servicio: null, rol: "Administrador"), control, true, outbound.Id);
    }

    [Fact]
    public async Task Sin_servicio_o_con_Outbound_una_operacion_Inbound_no_abre_el_Tenant()
    {
        await using var db = await CrearContexto();
        var ahora = DateTime.UtcNow;
        db.Add(AsignacionOperacion.Externa(Destino, Origen, ServicioCae.Inbound, AmbitoAsignacion.Universal,
            ahora.AddDays(-2), null, ahora));
        db.TenantsConLineaInbound.Add(Destino);
        await db.SaveChangesAsync();

        foreach (var servicio in new ServicioCae?[] { null, ServicioCae.Outbound })
        {
            var http = Contexto();
            Comprobar(await Cambiar(Destino, http, db, servicio, "Administrador"), http, false, null);
        }
    }

    [Fact]
    public async Task Un_servicio_no_definido_o_Inbound_sobre_el_propio_origen_es_una_peticion_invalida()
    {
        await using var db = await CrearContexto();

        var indefinido = Contexto();
        (await Cambiar(Destino, indefinido, db, (ServicioCae)99, "Administrador")).Should().BeOfType<BadRequest>();
        indefinido.Response.Headers.SetCookie.Should().BeEmpty();

        var origen = Contexto();
        (await Cambiar(Origen, origen, db, ServicioCae.Inbound, "Administrador")).Should().BeOfType<BadRequest>();
        origen.Response.Headers.SetCookie.Should().BeEmpty();
    }

    /// <summary>
    /// D-4: el papel recordado es una preferencia del selector, protegida y ligada al usuario. Una cookie de otro usuario,
    /// manipulada o de otra clave se lee como vacía.
    /// </summary>
    [Theory]
    [InlineData("propia", true)]
    [InlineData("de-otro-usuario", false)]
    [InlineData("manipulada", false)]
    [InlineData("de-otra-clave", false)]
    [InlineData("en-claro", false)]
    public void El_papel_recordado_solo_se_lee_si_es_del_usuario_y_de_esta_clave(string caso, bool seLee)
    {
        var valor = caso switch
        {
            "de-otro-usuario" => CookiePapel(_protector, Guid.NewGuid(), ServicioCae.Inbound),
            "manipulada" => CookiePapel(_protector, Usuario, ServicioCae.Inbound)[..^4] + "AAAA",
            "de-otra-clave" => CookiePapel(new EphemeralDataProtectionProvider(), Usuario, ServicioCae.Inbound),
            "en-claro" => $"{Usuario:N}|{Destino:N}:I",
            _ => CookiePapel(_protector, Usuario, ServicioCae.Inbound),
        };

        var leidos = PapelRecordadoSelectorTenant.Interpretar(valor, _protector, Usuario);

        if (seLee) leidos.Should().Equal(new System.Collections.Generic.Dictionary<Guid, ServicioCae> { [Destino] = ServicioCae.Inbound });
        else leidos.Should().BeEmpty();
    }

    /// <summary>
    /// El endpoint no lee el papel recordado: sin <c>servicio</c> (Mi trabajo, Visión de cartera, enlaces a una ficha) abre
    /// Outbound aunque la cookie recuerde Inbound, y la cookie nunca abre por sí misma un Tenant que no autoriza.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Sin_servicio_el_papel_recordado_no_cambia_la_via_que_se_pide(bool conOutbound)
    {
        await using var db = await CrearContexto();
        var ahora = DateTime.UtcNow;
        var inbound = AsignacionOperacion.Externa(Destino, Origen, ServicioCae.Inbound, AmbitoAsignacion.Universal,
            ahora.AddDays(-2), null, ahora);
        db.AddRange(inbound,
            AsignacionCartera.Externa(inbound, Usuario, "GestorCae", AmbitoAsignacion.Universal, ahora.AddDays(-1), null, ahora));
        AsignacionOperacion? outbound = null;
        if (conOutbound)
        {
            var delegacion = new DelegacionTenant(Origen, Destino);
            outbound = AsignacionOperacion.Externa(Destino, Origen, ServicioCae.Outbound, AmbitoAsignacion.Universal,
                ahora.AddDays(-2), null, ahora);
            outbound.FijarOrigen(delegacion);
            db.AddRange(delegacion, outbound,
                AsignacionCartera.Externa(outbound, Usuario, "GestorCae", AmbitoAsignacion.Universal, ahora.AddDays(-1), null, ahora));
        }
        db.TenantsConLineaInbound.Add(Destino);
        await db.SaveChangesAsync();

        var http = ContextoConPapelRecordado(CookiePapel(_protector, Usuario, ServicioCae.Inbound));

        Comprobar(await Cambiar(Destino, http, db), http, conOutbound, outbound?.Id);
        if (conOutbound) PapelRecordadoEn(http).Should().Be(ServicioCae.Outbound, "se recuerda el papel que se acaba de abrir");
    }

    [Fact]
    public async Task Con_servicio_Inbound_autorizado_se_recuerda_Inbound_para_ese_Tenant()
    {
        await using var db = await CrearContexto();
        var ahora = DateTime.UtcNow;
        var inbound = AsignacionOperacion.Externa(Destino, Origen, ServicioCae.Inbound, AmbitoAsignacion.Universal,
            ahora.AddDays(-2), null, ahora);
        db.AddRange(inbound,
            AsignacionCartera.Externa(inbound, Usuario, "GestorCae", AmbitoAsignacion.Universal, ahora.AddDays(-1), null, ahora));
        db.TenantsConLineaInbound.Add(Destino);
        await db.SaveChangesAsync();

        var http = Contexto();
        Comprobar(await Cambiar(Destino, http, db, ServicioCae.Inbound), http, true, inbound.Id);
        PapelRecordadoEn(http).Should().Be(ServicioCae.Inbound);
    }

    [Fact]
    public async Task Con_servicio_explicito_el_papel_recordado_no_cuenta_y_se_recuerda_el_elegido()
    {
        await using var db = await CrearContexto();
        var ahora = DateTime.UtcNow;
        var delegacion = new DelegacionTenant(Origen, Destino);
        var outbound = AsignacionOperacion.Externa(Destino, Origen, ServicioCae.Outbound, AmbitoAsignacion.Universal,
            ahora.AddDays(-2), null, ahora);
        outbound.FijarOrigen(delegacion);
        var inbound = AsignacionOperacion.Externa(Destino, Origen, ServicioCae.Inbound, AmbitoAsignacion.Universal,
            ahora.AddDays(-2), null, ahora);
        db.AddRange(delegacion, outbound, inbound,
            AsignacionCartera.Externa(outbound, Usuario, "GestorCae", AmbitoAsignacion.Universal, ahora.AddDays(-1), null, ahora),
            AsignacionCartera.Externa(inbound, Usuario, "GestorCae", AmbitoAsignacion.Universal, ahora.AddDays(-1), null, ahora));
        db.TenantsConLineaInbound.Add(Destino);
        await db.SaveChangesAsync();

        var http = ContextoConPapelRecordado(CookiePapel(_protector, Usuario, ServicioCae.Inbound));
        Comprobar(await Cambiar(Destino, http, db, ServicioCae.Outbound), http, true, outbound.Id);
        PapelRecordadoEn(http).Should().Be(ServicioCae.Outbound);
    }

    private static string CookiePapel(IDataProtectionProvider proveedor, Guid usuario, ServicioCae servicio)
    {
        var http = new DefaultHttpContext();
        PapelRecordadoSelectorTenant.Registrar(http, proveedor, usuario, Destino, servicio);
        var cabecera = http.Response.Headers.SetCookie.Single()!;
        return Uri.UnescapeDataString(cabecera.Split(';')[0][(PapelRecordadoSelectorTenant.NombreCookie.Length + 1)..]);
    }

    private DefaultHttpContext ContextoConPapelRecordado(string valor)
    {
        var http = Contexto();
        http.Request.Headers.Cookie = http.Request.Headers.Cookie + "; " + PapelRecordadoSelectorTenant.NombreCookie + "=" + valor;
        return http;
    }

    private ServicioCae? PapelRecordadoEn(HttpContext http)
    {
        var cabecera = http.Response.Headers.SetCookie
            .Single(c => c!.StartsWith(PapelRecordadoSelectorTenant.NombreCookie + "=", StringComparison.Ordinal))!;
        var valor = Uri.UnescapeDataString(cabecera.Split(';')[0][(PapelRecordadoSelectorTenant.NombreCookie.Length + 1)..]);
        return PapelRecordadoSelectorTenant.Interpretar(valor, _protector, Usuario).TryGetValue(Destino, out var s) ? s : null;
    }

    private DefaultHttpContext Contexto()
    {
        var http = new DefaultHttpContext();
        http.Request.Headers.Cookie = ClienteActivoSeleccionado.NombreCookie + "=" +
            ClienteActivoSeleccionado.Proteger(_protector, Usuario, Guid.NewGuid(), null);
        return http;
    }

    private void Comprobar(IResult resultado, HttpContext http, bool permitido, Guid? operacion)
    {
        if (!permitido)
        {
            resultado.Should().BeOfType<ForbidHttpResult>();
            http.Response.Headers.SetCookie.Should().BeEmpty("el rechazo no debe cambiar ninguna cookie de contexto ni de recientes");
            return;
        }
        resultado.Should().BeOfType<RedirectHttpResult>();
        var cabecera = http.Response.Headers.SetCookie.Single(c => c!.StartsWith(ClienteActivoSeleccionado.NombreCookie + "=", StringComparison.Ordinal))!;
        var token = Uri.UnescapeDataString(cabecera.Split(';')[0][(ClienteActivoSeleccionado.NombreCookie.Length + 1)..]);
        var carga = ClienteActivoSeleccionado.LeerCargaUtil(_protector, token, Usuario);
        carga.TenantId.Should().Be(Destino);
        carga.AsignacionOperacionId.Should().Be(operacion);
        carga.SesionPrivilegiadaId.Should().BeNull();
    }

    internal static async Task<ContextoPrueba> CrearContexto()
    {
        var conexion = new SqliteConnection("Data Source=:memory:");
        await conexion.OpenAsync();
        var db = new ContextoPrueba(new DbContextOptionsBuilder<ContextoPrueba>().UseSqlite(conexion, contextOwnsConnection: true).Options);
        await db.Database.EnsureCreatedAsync();
        return db;
    }

    // Solo las tablas que consulta el predicado; este instrumento no sustituye los tests PostgreSQL/RLS de CI.
    internal sealed class ContextoPrueba(DbContextOptions<ContextoPrueba> opciones) : DbContext(opciones), ITenantsQueryContext, IOperacionesQueryContext
    {
        /// <summary>Tenants con la línea Inbound vigente: el papel de <c>app_operacion_inbound_amparada</c> en PostgreSQL.</summary>
        public List<Guid> TenantsConLineaInbound { get; } = [];
        public IQueryable<AsignacionOperacion> OperacionesInboundAmparadas =>
            Set<AsignacionOperacion>().Where(o => o.Servicio == ServicioCae.Inbound && TenantsConLineaInbound.Contains(o.PropietarioTenantId));
        public IQueryable<Tenant> Tenants => Set<Tenant>();
        public IQueryable<RegistroActividadSoporte> RegistrosActividadSoporte => throw new NotSupportedException();
        public IQueryable<DelegacionTenant> DelegacionesTenant => Set<DelegacionTenant>();
        public IQueryable<AsignacionOperadorDelegado> AsignacionesOperadorDelegado => Set<AsignacionOperadorDelegado>().Where(a => a.RevocadaEnUtc == null);
        public IQueryable<AsignacionOperacion> AsignacionesOperacion => Set<AsignacionOperacion>();
        public IQueryable<Guid> TenantsSuspendidosIds => Set<Tenant>().Where(t => t.Estado == EstadoTenant.Suspendido).Select(t => t.Id);
        public IQueryable<AsignacionCartera> AsignacionesCartera => Set<AsignacionCartera>();
        protected override void OnModelCreating(ModelBuilder model)
        {
            model.Entity<Tenant>();
            model.Entity<DelegacionTenant>();
            model.Entity<AsignacionOperadorDelegado>();
            model.Entity<AsignacionOperacion>().HasBaseType((Type?)null);
            model.Entity<AsignacionCartera>().HasBaseType((Type?)null);
            model.Entity<AsignacionOperacion>().Ignore(a => a.Ambito);
            model.Entity<AsignacionCartera>().Ignore(a => a.Ambito);
        }
    }

    private sealed class UsuarioActual(string rol = "GestorCae") : ICurrentUserService
    {
        public Task<Guid?> ObtenerUsuarioActualIdAsync() => Task.FromResult<Guid?>(Usuario);
        public Task<Guid?> ObtenerTenantOrigenIdAsync() => Task.FromResult<Guid?>(Origen);
        public Task<string?> ObtenerRolEfectivoAsync() => Task.FromResult<string?>(rol);
        public Task<string?> ObtenerRolOrigenAsync() => throw new NotSupportedException();
        public Task<bool> TieneDobleFactorActivoAsync() => Task.FromResult(false);
    }
}
