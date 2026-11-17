using CaeManager.Application.Common;
using CaeManager.Application.Tenants;
using CaeManager.Application.Tenants.Queries.ObtenerActividadSoporte;
using CaeManager.Domain.Soporte;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Interceptors;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Plataforma;

/// <summary>
/// Issue #2016, contra PostgreSQL real y como <c>cae_app_runtime</c>, nunca como propietario: las
/// lecturas que Soporte TALVEG hizo bajo una Sesión Privilegiada sobre el Tenant A aparecen en la
/// consulta del Tenant A y nunca en la del Tenant B, y la actividad por Delegación de Tenant de
/// Soporte sigue apareciendo como antes.
///
/// <para>
/// Las filas se siembran como propietario con <see cref="RegistroActividadSoporte.PorSesionPrivilegiada"/>,
/// la misma forma que escribe <c>app_registrar_actividad_soporte_por_sesion</c> (tipo <c>Lectura</c>,
/// <c>DelegacionTenantId</c> nulo, <c>TenantId</c> = Tenant objetivo; la función la mide
/// <see cref="RegistroLecturaSesionPrivilegiadaBajoRuntimeTests"/>). La autorización de Application
/// dice siempre que sí, como en <see cref="AccesosSoporteTalvegBajoRuntimeTests"/>: lo que un usuario
/// no ve aquí lo ocultan la RLS de lectura de la tabla (bajo el runtime, solo el Administrador del Tenant
/// propietario y la plataforma de la visita, migración <c>SoloElAdministradorLeeLaActividadDeSoporte</c>)
/// o el filtro explícito por Tenant objetivo del handler. Por eso los Administradores son cuentas reales
/// con su rol. La barrera del rol en Application tiene sus tests con dobles en <c>Application.Tests</c>.
/// </para>
/// </summary>
public class ActividadSoporteDelTenantPropietarioBajoRuntimeTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenantPlataforma = Guid.NewGuid();
    private readonly Guid _tenantA = Guid.NewGuid();
    private readonly Guid _tenantB = Guid.NewGuid();
    private readonly Guid _tecnico = Guid.NewGuid();
    private readonly Guid _administradorDeA = Guid.NewGuid();
    private readonly Guid _administradorDeB = Guid.NewGuid();
    private readonly Guid _sesionSobreA = Guid.NewGuid();
    private readonly Guid _sesionSobreB = Guid.NewGuid();

    private Guid _delegacionSoporteDeA;
    private Guid _navegacionPorDelegacion;

    public async Task InitializeAsync()
    {
        await BaseDatosPostgresDePruebas.MigrarAsync(_cadenaConexion);

        // Siembra como propietario: el rol de runtime no crea Tenants ni escribe estas filas.
        await using (var contexto = CrearContexto(_tenantPlataforma, _tecnico, cadena: _cadenaConexion))
        {
            contexto.Tenants.Add(CrearTenant(_tenantPlataforma, "Plataforma", esPlataforma: true));
            contexto.Tenants.Add(CrearTenant(_tenantA, "Tenant A S.L."));
            contexto.Tenants.Add(CrearTenant(_tenantB, "Tenant B S.L."));
            var delegacion = DelegacionTenant.ParaSoporte(_tenantPlataforma, _tenantA);
            contexto.DelegacionesTenant.Add(delegacion);
            await contexto.SaveChangesAsync();
            _delegacionSoporteDeA = delegacion.Id;
        }

        await using (var contexto = CrearContexto(_tenantA, _tecnico, cadena: _cadenaConexion))
        {
            contexto.RegistrosActividadSoporte.Add(RegistroActividadSoporte.PorSesionPrivilegiada(
                _tecnico, _sesionSobreA, TipoActividadSoporte.Lectura, "ObtenerHistorialImportacionesQuery"));
            contexto.RegistrosActividadSoporte.Add(RegistroActividadSoporte.PorSesionPrivilegiada(
                _tecnico, _sesionSobreA, TipoActividadSoporte.Lectura, "EjecutarImportacionCommand"));
            var navegacion = RegistroActividadSoporte.PorViaHeredada(
                _tecnico, _delegacionSoporteDeA, TipoActividadSoporte.Navegacion, "/documentos");
            contexto.RegistrosActividadSoporte.Add(navegacion);
            await contexto.SaveChangesAsync();
            _navegacionPorDelegacion = navegacion.Id;
        }

        await AltaDeAdministradorAsync(_administradorDeA, _tenantA);
        await AltaDeAdministradorAsync(_administradorDeB, _tenantB);

        await using (var contexto = CrearContexto(_tenantB, _tecnico, cadena: _cadenaConexion))
        {
            contexto.RegistrosActividadSoporte.Add(RegistroActividadSoporte.PorSesionPrivilegiada(
                _tecnico, _sesionSobreB, TipoActividadSoporte.Lectura, "AnalizarPlantillaMaestraQuery"));
            await contexto.SaveChangesAsync();
        }
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task Las_lecturas_registradas_en_A_aparecen_en_la_consulta_de_A_sin_la_identidad_del_tecnico()
    {
        var actividad = await ConsultarComoAsync(
            _administradorDeA, tenantDelContexto: _tenantA, tenantOrigen: _tenantA,
            ObtenerActividadSoporteQuery.PorSesionPrivilegiada(_sesionSobreA));

        actividad.Should().HaveCount(2);
        actividad.Select(a => a.Detalle).Should().BeEquivalentTo(["ObtenerHistorialImportacionesQuery", "EjecutarImportacionCommand"]);
        actividad.Should().OnlyContain(a =>
            a.SesionPrivilegiadaId == _sesionSobreA && a.Tipo == TipoActividadSoporte.Lectura && a.UsuarioSoporteId == null);
    }

    [Fact]
    public async Task Las_lecturas_registradas_en_A_nunca_aparecen_en_la_consulta_de_B()
    {
        (await ConsultarComoAsync(
                _administradorDeB, tenantDelContexto: _tenantB, tenantOrigen: _tenantB,
                ObtenerActividadSoporteQuery.PorSesionPrivilegiada(_sesionSobreA)))
            .Should().BeEmpty("la sesión es sobre A: B no la ve ni nombrándola");

        (await ContarFilasVisiblesDeLaSesionAsync(_administradorDeB, _tenantB, _sesionSobreA)).Should().Be(0,
            "la RLS aislamiento_tenant tampoco las entrega a B, sin el filtro de la consulta");

        (await ConsultarComoAsync(
                _administradorDeB, tenantDelContexto: _tenantB, tenantOrigen: _tenantB,
                ObtenerActividadSoporteQuery.PorSesionPrivilegiada(_sesionSobreB)))
            .Should().ContainSingle("control positivo: B sí ve la lectura sobre su propio Tenant")
            .Which.Detalle.Should().Be("AnalizarPlantillaMaestraQuery");
    }

    [Fact]
    public async Task Con_el_contexto_de_datos_en_A_y_el_Tenant_de_origen_B_ni_la_RLS_ni_el_filtro_explicito_entregan_las_de_A()
    {
        // Context Workspace de A con Tenant de origen B: el filtro global deja pasar las filas de A, pero
        // la RLS solo se las entrega al Administrador de A, y el filtro explícito por Tenant objetivo
        // (= Tenant de origen) las corta también.
        (await ContarFilasVisiblesDeLaSesionAsync(_administradorDeA, _tenantA, _sesionSobreA)).Should().Be(2,
            "control positivo: con app.tenant_id = A la base entrega las dos filas a su Administrador");
        (await ContarFilasVisiblesDeLaSesionAsync(_administradorDeB, _tenantA, _sesionSobreA)).Should().Be(0,
            "el Administrador de B no es Administrador de A: la RLS no se las entrega");

        (await ConsultarComoAsync(
                _administradorDeB, tenantDelContexto: _tenantA, tenantOrigen: _tenantB,
                ObtenerActividadSoporteQuery.PorSesionPrivilegiada(_sesionSobreA)))
            .Should().BeEmpty("operar el workspace de A no da autoridad sobre los datos de A");
    }

    [Fact]
    public async Task La_actividad_por_delegacion_de_Soporte_sigue_apareciendo_al_Administrador_del_Tenant_propietario_visitado()
    {
        (await ConsultarComoAsync(
                Guid.NewGuid(), tenantDelContexto: _tenantA, tenantOrigen: _tenantA,
                ObtenerActividadSoporteQuery.PorViaHeredada(_delegacionSoporteDeA)))
            .Should().BeEmpty("una cuenta que no es Administrador de A ya no recibe la traza: la RLS no se la entrega");

        var actividad = await ConsultarComoAsync(
            _administradorDeA, tenantDelContexto: _tenantA, tenantOrigen: _tenantA,
            ObtenerActividadSoporteQuery.PorViaHeredada(_delegacionSoporteDeA));

        var fila = actividad.Should().ContainSingle("solo la fila de la delegación, no las lecturas por Sesión Privilegiada").Subject;
        fila.Id.Should().Be(_navegacionPorDelegacion);
        fila.UsuarioSoporteId.Should().Be(_tecnico, "la vía heredada devuelve lo mismo que antes");
        fila.SesionPrivilegiadaId.Should().BeNull();
    }

    // ── Andamiaje ──────────────────────────────────────────────────────────

    /// <summary>Cuenta real del Tenant con el rol Administrador, sembrada como propietario.</summary>
    private async Task AltaDeAdministradorAsync(Guid id, Guid tenantId)
    {
        var correo = $"admin.{id:N}@rls.local";
        await using (var contexto = CrearContexto(tenantId, id, cadena: _cadenaConexion))
        {
            contexto.Users.Add(new ApplicationUser
            {
                Id = id,
                UserName = correo,
                NormalizedUserName = correo.ToUpperInvariant(),
                Email = correo,
                NormalizedEmail = correo.ToUpperInvariant(),
                NombreCompleto = "Administrador",
                SecurityStamp = Guid.NewGuid().ToString("N"),
                TenantId = tenantId,
            });
            await contexto.SaveChangesAsync();
        }

        await using var conexion = new Npgsql.NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();
        await using var orden = new Npgsql.NpgsqlCommand(
            """
            INSERT INTO "AspNetUserRoles" ("UserId", "RoleId")
            SELECT @cuenta, "Id" FROM "AspNetRoles" WHERE "Name" = @rol
            """, conexion);
        orden.Parameters.AddWithValue("cuenta", id);
        orden.Parameters.AddWithValue("rol", Roles.Administrador);
        (await orden.ExecuteNonQueryAsync()).Should().Be(1, "barrera: el rol existe y la fila entró");
    }

    private async Task<IReadOnlyList<ActividadSoporteDto>> ConsultarComoAsync(
        Guid usuario, Guid tenantDelContexto, Guid tenantOrigen, ObtenerActividadSoporteQuery consulta)
    {
        await using var contexto = CrearContexto(tenantDelContexto, usuario);
        var handler = new ObtenerActividadSoporteQueryHandler(
            contexto,
            new CurrentUserServiceFalso(usuario, rol: null, tenantOrigenId: tenantOrigen),
            new AutorizacionQueSiempreDiceQueSi());
        return await handler.Handle(consulta, CancellationToken.None);
    }

    /// <summary>Cuenta en SQL, sin el filtro global de EF: lo que queda fuera lo quita la RLS.</summary>
    private async Task<int> ContarFilasVisiblesDeLaSesionAsync(Guid usuario, Guid tenant, Guid sesion)
    {
        await using var contexto = CrearContexto(tenant, usuario);
        return await contexto.Database
            .SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM \"RegistrosActividadSoporte\" WHERE \"SesionPrivilegiadaId\" = {sesion}")
            .SingleAsync();
    }

    /// <summary>
    /// Conecta como <c>cae_app_runtime</c> salvo que se pase la cadena del propietario (solo la
    /// siembra). <paramref name="tenantId"/> es el que el interceptor fija en <c>app.tenant_id</c>.
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

    private static Tenant CrearTenant(Guid id, string nombre, bool esPlataforma = false)
    {
        var tenant = new Tenant(nombre);
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
