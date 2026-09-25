using CaeManager.Application.Common;
using CaeManager.Application.Plataforma.Commands.AbrirSesionPrivilegiada;
using CaeManager.Application.Plataforma.Queries.ObtenerAccesosSoporteTalveg;
using CaeManager.Application.Tenants;
using CaeManager.Domain.Plataforma;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Interceptors;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Xunit;
using PlataformaWriter = CaeManager.Infrastructure.Plataforma.PlataformaWriter;

namespace CaeManager.IntegrationTests.Plataforma;

/// <summary>
/// Transparencia para el Tenant propietario (ADR-011 § 8.7, incremento 2)
/// contra PostgreSQL real y como <c>cae_app_runtime</c>, nunca como propietario.
///
/// <para>
/// Lo que se mide aquí es la <b>barrera de la base</b>, no la de Application:
/// la consulta recibe una autorización que siempre dice que sí, así que todo lo
/// que no ve un usuario lo está ocultando la política
/// <c>administrador_del_tenant_objetivo</c>. La barrera de Application tiene
/// sus propios tests con dobles en <c>Application.Tests</c>.
/// </para>
/// </summary>
public class AccesosSoporteTalvegBajoRuntimeTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenantPlataforma = Guid.NewGuid();
    private readonly Guid _tenantA = Guid.NewGuid();
    private readonly Guid _tenantB = Guid.NewGuid();
    private readonly Guid _tecnico = Guid.NewGuid();

    private Guid _administradorDeA;
    private Guid _gestorDeA;
    private Guid _administradorDeB;
    private Guid _administradorDesactivadoDeA;
    private Guid _sesionSobreA;
    private Guid _sesionSobreB;

    public async Task InitializeAsync()
    {
        await BaseDatosPostgresDePruebas.MigrarAsync(_cadenaConexion);

        // Siembra como propietario: el rol de runtime no crea Tenants ni cuentas.
        await using (var contexto = CrearContexto(_tenantPlataforma, _tecnico, cadena: _cadenaConexion))
        {
            contexto.Tenants.Add(CrearTenant(_tenantPlataforma, "Plataforma", esPlataforma: true));
            contexto.Tenants.Add(CrearTenant(_tenantA, "Tenant A S.L."));
            contexto.Tenants.Add(CrearTenant(_tenantB, "Tenant B S.L."));

            // Los roles los siembra la migración base (HasData): se reutilizan.
            var rolAdministrador = await contexto.Roles.SingleAsync(r => r.Name == Roles.Administrador);
            var rolGestor = await contexto.Roles.SingleAsync(r => r.Name == Roles.GestorCae);

            Guid Cuenta(string nombre, Guid tenant, Guid rol, bool desactivada = false)
            {
                var correo = $"{nombre}@accesos-soporte.test";
                var cuenta = new ApplicationUser
                {
                    UserName = correo, NormalizedUserName = correo.ToUpperInvariant(),
                    Email = correo, NormalizedEmail = correo.ToUpperInvariant(),
                    NombreCompleto = nombre, TenantId = tenant,
                };
                if (desactivada) cuenta.LockoutEnd = ApplicationUser.FinDeBloqueoDeCuentaDesactivada;
                contexto.Users.Add(cuenta);
                contexto.UserRoles.Add(new IdentityUserRole<Guid> { UserId = cuenta.Id, RoleId = rol });
                return cuenta.Id;
            }

            _administradorDeA = Cuenta("admin-a", _tenantA, rolAdministrador.Id);
            _gestorDeA = Cuenta("gestor-a", _tenantA, rolGestor.Id);
            _administradorDeB = Cuenta("admin-b", _tenantB, rolAdministrador.Id);
            _administradorDesactivadoDeA = Cuenta("admin-a-baja", _tenantA, rolAdministrador.Id, desactivada: true);
            await contexto.SaveChangesAsync();
        }

        // Las sesiones las abre Soporte TALVEG por el camino real, bajo runtime.
        var concesionId = await AutoconcederGlobalComoRuntimeAsync();
        _sesionSobreA = await AbrirComoRuntimeAsync(concesionId, _tenantA, "Revisar la incidencia 42", "TCK-42");
        _sesionSobreB = await AbrirComoRuntimeAsync(concesionId, _tenantB, "Revisar la incidencia 77", null);
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task El_Administrador_del_Tenant_propietario_ve_las_sesiones_sobre_su_Tenant_y_solo_esas()
    {
        var accesos = await ConsultarComoAsync(_administradorDeA, _tenantA);

        accesos.Should().ContainSingle();
        var acceso = accesos![0];
        acceso.SesionId.Should().Be(_sesionSobreA);
        acceso.Motivo.Should().Be("Revisar la incidencia 42");
        acceso.Ticket.Should().Be("TCK-42");
        acceso.Estado.Should().Be(EstadoAccesoSoporteTalveg.Abierto);

        (await ContarSesionesVisiblesComoAsync(_administradorDeA, _tenantA)).Should().Be(1,
            "la política entrega la sesión sobre A y ninguna otra, ni siquiera sin el filtro de la consulta");
    }

    [Fact]
    public async Task Un_usuario_del_Tenant_sin_rol_Administrador_no_lee_ninguna_sesion_aunque_Application_le_deje()
    {
        (await ConsultarComoAsync(_gestorDeA, _tenantA)).Should().BeEmpty(
            "la autoridad está también en la base: por Tenant a secas, cualquier usuario de A leería " +
            "el historial de accesos de Soporte TALVEG");
        (await ContarSesionesVisiblesComoAsync(_gestorDeA, _tenantA)).Should().Be(0);
    }

    [Fact]
    public async Task El_Administrador_de_otro_Tenant_no_lee_las_sesiones_de_A_ni_operando_su_workspace()
    {
        (await ContarSesionesVisiblesComoAsync(_administradorDeB, _tenantA)).Should().Be(0,
            "app.tenant_id = A no basta: hay que ser Administrador DE A, no de cualquier Tenant");
        (await ContarSesionesVisiblesComoAsync(_administradorDeB, _tenantB)).Should().Be(1,
            "control positivo: en su propio Tenant sí ve la suya");
    }

    [Fact]
    public async Task Un_Administrador_desactivado_no_lee_ninguna_sesion()
    {
        (await ContarSesionesVisiblesComoAsync(_administradorDesactivadoDeA, _tenantA)).Should().Be(0);
    }

    [Fact]
    public async Task El_Administrador_no_puede_cerrar_ni_modificar_una_sesion_de_Soporte_TALVEG()
    {
        await using var contexto = CrearContexto(_tenantA, _administradorDeA);

        var afectadas = await contexto.SesionesPrivilegiadas
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.CerradaEnUtc, DateTime.UtcNow));

        afectadas.Should().Be(0, "la política nueva es solo de lectura; escribir sigue siendo del titular de la concesión");
    }

    [Fact]
    public async Task Soporte_TALVEG_dentro_de_su_sesion_no_gana_visibilidad_por_la_politica_nueva()
    {
        // Dentro de la sesión, app.tenant_id = A y app.usuario_id = el técnico,
        // que no es Administrador de A: ve su sesión por su concesión, no más.
        (await ContarSesionesVisiblesComoAsync(_tecnico, _tenantA)).Should().Be(2,
            "el titular de la concesión ve sus dos sesiones por privilegio_del_usuario, como antes");

        await using var contexto = CrearContexto(_tenantA, _tecnico);
        (await contexto.SesionesPrivilegiadas.CountAsync(s => s.Id == _sesionSobreB)).Should().Be(1,
            "control: la sesión sobre B la ve por la concesión, no por ser de A");
    }

    // ── Andamiaje ──────────────────────────────────────────────────────────

    private async Task<IReadOnlyList<AccesoSoporteTalvegDto>?> ConsultarComoAsync(Guid usuario, Guid tenant)
    {
        await using var contexto = CrearContexto(tenant, usuario);
        var handler = new ObtenerAccesosSoporteTalvegQueryHandler(
            new CurrentUserServiceFalso(usuario, rol: null, tenantOrigenId: tenant),
            new AutorizacionQueSiempreDiceQueSi(),
            contexto);
        return await handler.Handle(new ObtenerAccesosSoporteTalvegQuery(), CancellationToken.None);
    }

    private async Task<int> ContarSesionesVisiblesComoAsync(Guid usuario, Guid tenant)
    {
        await using var contexto = CrearContexto(tenant, usuario);
        return await contexto.SesionesPrivilegiadas.CountAsync();
    }

    private async Task<Guid> AutoconcederGlobalComoRuntimeAsync()
    {
        await using var contexto = CrearContexto(_tenantPlataforma, _tecnico);
        var ahora = DateTime.UtcNow;
        var concesion = ConcesionPrivilegio.SoporteLecturaGlobal(
            _tecnico, ahora.AddMinutes(-10), ahora.AddDays(30), concedidaPorUsuarioId: _tecnico);
        contexto.ConcesionesPrivilegio.Add(concesion);
        await contexto.SaveChangesAsync();
        return concesion.Id;
    }

    private async Task<Guid> AbrirComoRuntimeAsync(Guid concesionId, Guid tenantObjetivo, string motivo, string? ticket)
    {
        await using var contexto = CrearContexto(_tenantPlataforma, _tecnico);
        var handler = new AbrirSesionPrivilegiadaCommandHandler(
            contexto, contexto, new PlataformaWriter(contexto), UsuarioTecnico(), contexto);

        var resultado = await handler.Handle(
            new AbrirSesionPrivilegiadaCommand(concesionId, tenantObjetivo, motivo, HorasDeVentana: 1, Ticket: ticket),
            CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue(resultado.EsFallido ? resultado.Error.Codigo : "");
        return resultado.Valor;
    }

    private CurrentUserServiceFalso UsuarioTecnico() =>
        new(_tecnico, rol: null, tenantOrigenId: _tenantPlataforma, tieneDobleFactorActivo: true);

    /// <summary>
    /// Conecta como <c>cae_app_runtime</c> salvo que se pase la cadena del
    /// propietario (solo la siembra). <paramref name="usuario"/> es el que el
    /// interceptor fija en <c>app.usuario_id</c>.
    /// </summary>
    private CaeManagerDbContext CrearContexto(Guid tenantId, Guid usuario, string? cadena = null)
    {
        var tenantActual = new TenantActualAmbiental { TenantId = tenantId };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(
                cadena ?? BaseDatosPostgresDePruebas.CadenaComoRuntime(_cadenaConexion),
                npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(
                new TenantSelladoInterceptor(tenantActual),
                new TenantRlsConnectionInterceptor(
                    tenantActual, new SinSeleccion(),
                    new CurrentUserServiceFalso(usuario, rol: null, tenantOrigenId: tenantId),
                    BaseDatosPostgresDePruebas.FirmanteContextoRls))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }

    private static Domain.Tenants.Tenant CrearTenant(Guid id, string nombre, bool esPlataforma = false)
    {
        var tenant = new Domain.Tenants.Tenant(nombre);
        typeof(Domain.Common.Entity).GetProperty(nameof(Domain.Common.Entity.Id))!.SetValue(tenant, id);
        if (esPlataforma) tenant.MarcarComoPlataforma();
        return tenant;
    }

    private sealed class AutorizacionQueSiempreDiceQueSi : IAutorizacionDelegacionTenant
    {
        public Task<bool> PuedeGestionarDelegacionesAsync(
            Guid usuarioId, Guid tenantClienteDeleganteId, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
    }

    private sealed class SinSeleccion : IClienteActivoSeleccionado
    {
        public Guid? TenantIdSeleccionado => null;
        public Guid? AsignacionOperacionIdSeleccionada => null;
        public Guid? SesionPrivilegiadaIdSeleccionada => null;
    }
}
