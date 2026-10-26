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
        var operacion = AsignacionOperacion.Externa(Destino, caso == "otro-operador" ? Guid.NewGuid() : Origen,
            ServicioCae.Outbound, AmbitoAsignacion.Universal, desde, null, ahora);
        var cartera = AsignacionCartera.Externa(operacion, caso == "otro-usuario" ? Guid.NewGuid() : Usuario,
            "GestorCae", AmbitoAsignacion.Universal, caso == "futura" ? ahora.AddDays(1) : desde,
            caso == "caducada" ? ahora.AddDays(-1) : null, ahora);
        if (caso == "suspendida") cartera.Suspender();
        if (caso == "operacion-suspendida") operacion.Suspender();
        db.AddRange(operacion, cartera);
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

    private Task<IResult> Cambiar(Guid tenant, HttpContext http, ContextoPrueba db) =>
        ClienteActivoEndpoints.CambiarAsync(tenant, "/", http, db, new UsuarioActual(), db, _protector, default);

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

    private static async Task<ContextoPrueba> CrearContexto()
    {
        var conexion = new SqliteConnection("Data Source=:memory:");
        await conexion.OpenAsync();
        var db = new ContextoPrueba(new DbContextOptionsBuilder<ContextoPrueba>().UseSqlite(conexion, contextOwnsConnection: true).Options);
        await db.Database.EnsureCreatedAsync();
        return db;
    }

    // Solo las tablas que consulta el predicado; este instrumento no sustituye los tests PostgreSQL/RLS de CI.
    private sealed class ContextoPrueba(DbContextOptions<ContextoPrueba> opciones) : DbContext(opciones), ITenantsQueryContext, IOperacionesQueryContext
    {
        public IQueryable<Tenant> Tenants => throw new NotSupportedException();
        public IQueryable<RegistroActividadSoporte> RegistrosActividadSoporte => throw new NotSupportedException();
        public IQueryable<DelegacionTenant> DelegacionesTenant => Set<DelegacionTenant>();
        public IQueryable<AsignacionOperadorDelegado> AsignacionesOperadorDelegado => Set<AsignacionOperadorDelegado>().Where(a => a.RevocadaEnUtc == null);
        public IQueryable<AsignacionOperacion> AsignacionesOperacion => Set<AsignacionOperacion>();
        public IQueryable<AsignacionCartera> AsignacionesCartera => Set<AsignacionCartera>();
        protected override void OnModelCreating(ModelBuilder model)
        {
            model.Entity<DelegacionTenant>();
            model.Entity<AsignacionOperadorDelegado>();
            model.Entity<AsignacionOperacion>().HasBaseType((Type?)null);
            model.Entity<AsignacionCartera>().HasBaseType((Type?)null);
            model.Entity<AsignacionOperacion>().Ignore(a => a.Ambito);
            model.Entity<AsignacionCartera>().Ignore(a => a.Ambito);
        }
    }

    private sealed class UsuarioActual : ICurrentUserService
    {
        public Task<Guid?> ObtenerUsuarioActualIdAsync() => Task.FromResult<Guid?>(Usuario);
        public Task<Guid?> ObtenerTenantOrigenIdAsync() => Task.FromResult<Guid?>(Origen);
        public Task<string?> ObtenerRolEfectivoAsync() => Task.FromResult<string?>("GestorCae");
        public Task<string?> ObtenerRolOrigenAsync() => throw new NotSupportedException();
        public Task<bool> TieneDobleFactorActivoAsync() => Task.FromResult(false);
    }
}
