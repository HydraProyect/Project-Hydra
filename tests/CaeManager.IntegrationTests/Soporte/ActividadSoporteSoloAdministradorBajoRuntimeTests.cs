using CaeManager.Application.Common;
using CaeManager.Domain.Soporte;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Seed;
using CaeManager.IntegrationTests.Arranque;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace CaeManager.IntegrationTests.Soporte;

/// <summary>
/// <b>La lectura de <c>RegistrosActividadSoporte</c> bajo <c>cae_app_runtime</c> real</b> (login del rol, no
/// <c>SET ROLE</c>), migración <c>SoloElAdministradorLeeLaActividadDeSoporte</c>: <c>aislamiento_tenant</c> ya no
/// cubre SELECT, y la lectura la abren dos políticas PERMISSIVE de SELECT para el runtime, una para el Administrador
/// del Tenant propietario y otra para la plataforma de esa visita, más la de <c>cae_app_soporte</c>, que conserva su
/// lectura por Tenant.
///
/// <para>
/// SQL directo sobre una conexión de runtime con el contexto firmado a mano (<see cref="ContextoRlsDePruebas"/>), sin
/// Application por medio: la propiedad es de la base y tiene que valer aunque aparezca un lector nuevo o el handler
/// pierda su comprobación. Cada caso negativo lleva su control positivo: el propietario ve la fila, así que un cero
/// del runtime es la política y no una fila que no existe.
/// </para>
///
/// <para>
/// El escenario tiene, en el Tenant X, la fila de la visita de la plataforma P (Delegación de Soporte P→X), una fila
/// cruzada (del Tenant X, pero colgada de la Delegación de Soporte P→Y) y una fila de una Delegación de Soporte Y→X,
/// cuya consultora Y no es la plataforma; y en el Tenant Y, la fila de la visita de P a Y.
/// </para>
///
/// <para>
/// Sensibilidad (mutaciones de la migración; las corre el CI, nunca en local):
/// </para>
/// <list type="bullet">
/// <item>Sin la llamada a <c>app_es_administrador_del_tenant</c> en <c>administrador_del_tenant_propietario_lee</c>:
/// rojo <see cref="Otro_usuario_del_Tenant_propietario_no_lee_ninguna_fila"/>.</item>
/// <item>Sin <c>"TenantId" = app_ctx_tenant_id()</c> en <c>administrador_del_tenant_propietario_lee</c>: rojo
/// <see cref="El_Administrador_del_Tenant_propietario_lee_sus_filas_y_solo_las_suyas"/> (ve la fila de Y).</item>
/// <item>Sin <c>t."EsPlataforma"</c> en <c>plataforma_lee_su_visita</c>: rojo
/// <see cref="Un_usuario_de_otro_Tenant_no_lee_ninguna_fila"/> (Y es la consultora de la Delegación Y→X).</item>
/// <item>Sin <c>d."TenantConsultoraId" = app_ctx_tenant_origen_id()</c>, o sin
/// <c>d."TenantClienteId" = "TenantId"</c>: rojo
/// <see cref="La_plataforma_lee_la_traza_de_su_visita_y_no_la_de_otra"/> (fila ajena o fila cruzada).</item>
/// <item>Sin <c>"TenantId" = app_ctx_tenant_id()</c> en <c>plataforma_lee_su_visita</c>: rojo
/// <see cref="La_plataforma_lee_la_traza_de_su_visita_y_no_la_de_otra"/> (desde su propio Tenant vería sus visitas a X
/// y a Y).</item>
/// <item>Con <c>soporte_lee_por_tenant</c> para PUBLIC o para <c>cae_app_runtime</c>: rojo
/// <see cref="Otro_usuario_del_Tenant_propietario_no_lee_ninguna_fila"/>; sin ella: rojo
/// <see cref="Cae_app_soporte_sigue_leyendo_por_Tenant"/>.</item>
/// <item>Con el <c>WITH CHECK</c> de <c>aislamiento_tenant</c> a <c>true</c>: rojo
/// <see cref="El_runtime_sigue_insertando_la_traza_solo_en_el_Tenant_del_contexto"/>.</item>
/// </list>
/// </summary>
public class ActividadSoporteSoloAdministradorBajoRuntimeTests
{
    private static readonly Guid TenantP = TenantSeedData.IdPorDefecto;

    private sealed record Escenario(
        ArnesDeArranqueRuntime Arnes,
        Guid TenantX,
        Guid TenantY,
        Guid AdministradorX,
        Guid ConsultaX,
        Guid AdministradorY,
        Guid SoporteP,
        Guid FilaX,
        Guid FilaCruzadaEnX,
        Guid FilaAjenaEnX,
        Guid FilaY,
        Guid DelegacionPSobreY) : IAsyncDisposable
    {
        public Guid[] FilasDeX => [FilaX, FilaCruzadaEnX, FilaAjenaEnX];

        public ValueTask DisposeAsync() => Arnes.DisposeAsync();
    }

    [Fact]
    public async Task El_Administrador_del_Tenant_propietario_lee_sus_filas_y_solo_las_suyas()
    {
        await using var e = await CrearEscenarioAsync();

        (await VisiblesAsync(e, tenant: e.TenantX, origen: e.TenantX, usuario: e.AdministradorX))
            .Should().BeEquivalentTo(e.FilasDeX, "es el Administrador del Tenant propietario de las tres filas de X");
        (await VisiblesAsync(e, tenant: e.TenantY, origen: e.TenantX, usuario: e.AdministradorX))
            .Should().BeEmpty("con el Tenant Y en el contexto no es su Administrador");
    }

    [Fact]
    public async Task Otro_usuario_del_Tenant_propietario_no_lee_ninguna_fila()
    {
        await using var e = await CrearEscenarioAsync();
        (await ExisteComoPropietarioAsync(e, e.FilaX)).Should().BeTrue("control positivo: la fila existe");

        (await VisiblesAsync(e, tenant: e.TenantX, origen: e.TenantX, usuario: e.ConsultaX))
            .Should().BeEmpty("la cuenta es del Tenant propietario pero no es su Administrador");
        (await VisiblesAsync(e, tenant: e.TenantX, origen: e.TenantX, usuario: null))
            .Should().BeEmpty("sin usuario en el contexto no hay Administrador");
    }

    [Fact]
    public async Task Un_usuario_de_otro_Tenant_no_lee_ninguna_fila()
    {
        await using var e = await CrearEscenarioAsync();
        (await ExisteComoPropietarioAsync(e, e.FilaAjenaEnX)).Should().BeTrue("control positivo: la fila existe");

        // El Administrador de Y con el Tenant X en el contexto: no es Administrador de X, y Y, aunque es la
        // consultora de la Delegación de Soporte Y→X, no es la plataforma.
        (await VisiblesAsync(e, tenant: e.TenantX, origen: e.TenantY, usuario: e.AdministradorY))
            .Should().BeEmpty("es Administrador de otro Tenant, y su Tenant no es la plataforma de ninguna visita");

        // Desde su propio Tenant ve las suyas y ninguna de X.
        (await VisiblesAsync(e, tenant: e.TenantY, origen: e.TenantY, usuario: e.AdministradorY))
            .Should().Equal([e.FilaY]);
    }

    [Fact]
    public async Task La_plataforma_lee_la_traza_de_su_visita_y_no_la_de_otra()
    {
        await using var e = await CrearEscenarioAsync();
        (await ExisteComoPropietarioAsync(e, e.FilaCruzadaEnX)).Should().BeTrue("control positivo: la fila existe");

        // El camino de ObtenerActividadSoporteQuery para la plataforma: Tenant de origen P y ámbito en el Tenant
        // visitado (FlujoSoporteTests, E2E). Ni la fila de la Delegación de Soporte Y→X (otra consultora) ni la
        // cruzada (cuelga de la visita de P a Y, no a X).
        (await VisiblesAsync(e, tenant: e.TenantX, origen: TenantP, usuario: e.SoporteP))
            .Should().Equal([e.FilaX], "solo la fila de la delegación de soporte de P sobre X");

        // Desde su propio Tenant no ve la traza de ninguna visita: la política se ata al Tenant del contexto.
        (await VisiblesAsync(e, tenant: TenantP, origen: TenantP, usuario: e.SoporteP))
            .Should().BeEmpty("la plataforma lee la traza dentro del ámbito del Tenant visitado, como hasta ahora");

        // La misma cuenta, sin el Tenant de origen plataforma en el contexto, no ve nada.
        (await VisiblesAsync(e, tenant: e.TenantX, origen: e.TenantY, usuario: e.SoporteP))
            .Should().BeEmpty("la rama de la plataforma se ata al Tenant de origen del contexto firmado");
    }

    [Fact]
    public async Task Cae_app_soporte_sigue_leyendo_por_Tenant()
    {
        await using var e = await CrearEscenarioAsync();

        // La Sesión Privilegiada: el interceptor adopta cae_app_soporte con SET ROLE sobre la conexión de runtime.
        (await VisiblesAsync(e, tenant: e.TenantX, origen: TenantP, usuario: e.SoporteP, comoSoporte: true))
            .Should().BeEquivalentTo(e.FilasDeX, "cae_app_soporte conserva la lectura por Tenant que tenía");
        (await VisiblesAsync(e, tenant: e.TenantY, origen: TenantP, usuario: e.SoporteP, comoSoporte: true))
            .Should().Equal([e.FilaY]);
    }

    [Fact]
    public async Task El_runtime_sigue_insertando_la_traza_solo_en_el_Tenant_del_contexto()
    {
        await using var e = await CrearEscenarioAsync();
        var nueva = RegistroActividadSoporte.PorViaHeredada(
            e.SoporteP, Guid.NewGuid(), TipoActividadSoporte.Navegacion, "/documentos");

        // El camino real de TrazaSoporteService: EF con los interceptores de producción, como runtime, sin
        // Administrador en el contexto. El INSERT no lleva RETURNING, así que no necesita política de SELECT.
        using (var ambito = e.Arnes.Servicios.CreateScope())
        {
            var contexto = ambito.ServiceProvider.GetRequiredService<CaeManagerDbContext>();
            using var ambitoTenant = AmbitoTenantExplicito.Establecer(e.TenantX);
            contexto.RegistrosActividadSoporte.Add(nueva);
            await contexto.SaveChangesAsync();
        }

        (await ExisteComoPropietarioAsync(e, nueva.Id)).Should().BeTrue("el INSERT del runtime tiene que seguir funcionando");
        (await VisiblesAsync(e, tenant: e.TenantX, origen: e.TenantX, usuario: e.AdministradorX, ademas: nueva.Id))
            .Should().Contain(nueva.Id, "y el Administrador la lee después");

        // aislamiento_tenant sigue siendo el WITH CHECK del INSERT: una fila de otro Tenant no entra.
        await using var conexion = await AbrirComoRuntimeAsync(e, tenant: e.TenantX, origen: TenantP, usuario: e.SoporteP);
        await using var orden = new NpgsqlCommand(
            """
            INSERT INTO "RegistrosActividadSoporte" ("Id", "OcurridaEnUtc", "TenantId", "UsuarioSoporteId", "Tipo", "DelegacionTenantId")
            VALUES (@id, now(), @tenant, @usuario, 'Navegacion', @delegacion)
            """, conexion);
        var ajena = Guid.NewGuid();
        orden.Parameters.AddWithValue("id", ajena);
        orden.Parameters.AddWithValue("tenant", e.TenantY);
        orden.Parameters.AddWithValue("usuario", e.SoporteP);
        orden.Parameters.AddWithValue("delegacion", e.DelegacionPSobreY);

        var insertar = async () => await orden.ExecuteNonQueryAsync();
        (await insertar.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege, "el WITH CHECK por Tenant rechaza la fila de Y");
        (await ExisteComoPropietarioAsync(e, ajena)).Should().BeFalse();
    }

    // ── Escenario e instrumentos ────────────────────────────────────────

    private static async Task<Escenario> CrearEscenarioAsync()
    {
        var arnes = await ArnesDeArranqueRuntime.CrearAsync(datosDePruebaActivos: false);
        using var ambito = arnes.Servicios.CreateScope();
        await using var contexto = ambito.ServiceProvider.GetRequiredService<FabricaContextoDeBootstrap>().Crear();

        var tenantX = new Tenant($"Tenant X {Guid.NewGuid():N}");
        var tenantY = new Tenant($"Tenant Y {Guid.NewGuid():N}");
        foreach (var tenant in new[] { tenantX, tenantY })
        {
            using (AmbitoTenantExplicito.Establecer(tenant.Id))
            {
                contexto.Tenants.Add(tenant);
                await contexto.SaveChangesAsync();
            }
        }

        var administradorX = await AltaAsync(contexto, tenantX.Id, "admin.x");
        var consultaX = await AltaAsync(contexto, tenantX.Id, "consulta.x");
        var administradorY = await AltaAsync(contexto, tenantY.Id, "admin.y");
        var soporteP = await AltaAsync(contexto, TenantP, "soporte.p");

        var soporteSobreX = DelegacionTenant.ParaSoporte(TenantP, tenantX.Id);
        var soporteSobreY = DelegacionTenant.ParaSoporte(TenantP, tenantY.Id);
        contexto.DelegacionesTenant.AddRange(soporteSobreX, soporteSobreY);
        using (AmbitoTenantExplicito.Establecer(TenantP))
            await contexto.SaveChangesAsync();

        // Una Delegación de Soporte cuya consultora no es la plataforma: su traza no es de ninguna visita de P.
        var soporteDeYSobreX = DelegacionTenant.ParaSoporte(tenantY.Id, tenantX.Id);
        contexto.DelegacionesTenant.Add(soporteDeYSobreX);
        using (AmbitoTenantExplicito.Establecer(tenantY.Id))
            await contexto.SaveChangesAsync();

        var filaX = await TrazaAsync(contexto, tenantX.Id, soporteP, soporteSobreX.Id);
        var filaCruzada = await TrazaAsync(contexto, tenantX.Id, soporteP, soporteSobreY.Id);
        var filaAjena = await TrazaAsync(contexto, tenantX.Id, soporteP, soporteDeYSobreX.Id);
        var filaY = await TrazaAsync(contexto, tenantY.Id, soporteP, soporteSobreY.Id);

        var e = new Escenario(arnes, tenantX.Id, tenantY.Id, administradorX, consultaX, administradorY, soporteP,
            filaX, filaCruzada, filaAjena, filaY, soporteSobreY.Id);
        await AsignarRolAsync(e, administradorX, Roles.Administrador);
        await AsignarRolAsync(e, consultaX, Roles.Consulta);
        await AsignarRolAsync(e, administradorY, Roles.Administrador);

        // Barrera del escenario: la rama de la plataforma exige que P esté marcado como plataforma, e Y no.
        await using var conexion = new NpgsqlConnection(arnes.CadenaPropietario);
        await conexion.OpenAsync();
        await using var orden = new NpgsqlCommand(
            """SELECT count(*) FILTER (WHERE "Id" = @p AND "EsPlataforma"), count(*) FILTER (WHERE "Id" = @y AND NOT "EsPlataforma") FROM "Tenants" """,
            conexion);
        orden.Parameters.AddWithValue("p", TenantP);
        orden.Parameters.AddWithValue("y", tenantY.Id);
        await using (var lector = await orden.ExecuteReaderAsync())
        {
            (await lector.ReadAsync()).Should().BeTrue();
            lector.GetInt64(0).Should().Be(1, "el Tenant por defecto es el de plataforma");
            lector.GetInt64(1).Should().Be(1, "el Tenant Y no es la plataforma");
        }

        return e;
    }

    private static async Task<Guid> TrazaAsync(CaeManagerDbContext contexto, Guid tenantId, Guid usuarioSoporte, Guid delegacion)
    {
        var fila = RegistroActividadSoporte.PorViaHeredada(usuarioSoporte, delegacion, TipoActividadSoporte.AccesoConcedido);
        using (AmbitoTenantExplicito.Establecer(tenantId))
        {
            contexto.RegistrosActividadSoporte.Add(fila);
            await contexto.SaveChangesAsync();
        }

        return fila.Id;
    }

    private static async Task<Guid> AltaAsync(CaeManagerDbContext contexto, Guid tenantId, string alias)
    {
        var correo = $"{alias}.{Guid.NewGuid():N}@rls.local";
        var usuario = new ApplicationUser
        {
            UserName = correo,
            NormalizedUserName = correo.ToUpperInvariant(),
            Email = correo,
            NormalizedEmail = correo.ToUpperInvariant(),
            NombreCompleto = alias,
            SecurityStamp = Guid.NewGuid().ToString("N"),
            TenantId = tenantId,
        };

        using (AmbitoTenantExplicito.Establecer(tenantId))
        {
            contexto.Users.Add(usuario);
            await contexto.SaveChangesAsync();
        }

        return usuario.Id;
    }

    private static async Task AsignarRolAsync(Escenario e, Guid cuenta, string rol)
    {
        await using var conexion = new NpgsqlConnection(e.Arnes.CadenaPropietario);
        await conexion.OpenAsync();
        await using var orden = new NpgsqlCommand(
            """
            INSERT INTO "AspNetUserRoles" ("UserId", "RoleId")
            SELECT @cuenta, "Id" FROM "AspNetRoles" WHERE "Name" = @rol
            """, conexion);
        orden.Parameters.AddWithValue("cuenta", cuenta);
        orden.Parameters.AddWithValue("rol", rol);

        (await orden.ExecuteNonQueryAsync()).Should().Be(1, "barrera: el rol existe y la fila entró");
    }

    private static async Task<NpgsqlConnection> AbrirComoRuntimeAsync(Escenario e, Guid? tenant, Guid? origen, Guid? usuario)
    {
        var conexion = new NpgsqlConnection(BaseDatosPostgresDePruebas.CadenaComoRuntime(e.Arnes.CadenaPropietario));
        await conexion.OpenAsync();

        await using (var identidad = new NpgsqlCommand("SELECT current_user", conexion))
            ((string)(await identidad.ExecuteScalarAsync())!).Should().Be("cae_app_runtime");

        await ContextoRlsDePruebas.FijarAsync(conexion, tenantId: tenant, tenantOrigenId: origen, usuarioId: usuario);
        return conexion;
    }

    private static async Task<List<Guid>> VisiblesAsync(
        Escenario e, Guid? tenant, Guid? origen, Guid? usuario, bool comoSoporte = false, Guid? ademas = null)
    {
        await using var conexion = await AbrirComoRuntimeAsync(e, tenant, origen, usuario);

        if (comoSoporte)
        {
            await using (var adoptar = new NpgsqlCommand("SET ROLE cae_app_soporte", conexion))
                await adoptar.ExecuteNonQueryAsync();
            await using var identidad = new NpgsqlCommand("SELECT current_user", conexion);
            ((string)(await identidad.ExecuteScalarAsync())!).Should().Be("cae_app_soporte");
        }

        // Solo las filas del escenario: la base del arnés puede traer otras de la siembra.
        await using var orden = new NpgsqlCommand(
            """SELECT "Id" FROM "RegistrosActividadSoporte" WHERE "Id" = ANY(@filas) ORDER BY "Id" """, conexion);
        orden.Parameters.AddWithValue("filas", new[] { e.FilaX, e.FilaCruzadaEnX, e.FilaAjenaEnX, e.FilaY, ademas ?? Guid.Empty });
        var visibles = new List<Guid>();
        await using var lector = await orden.ExecuteReaderAsync();
        while (await lector.ReadAsync())
            visibles.Add(lector.GetGuid(0));
        return visibles;
    }

    private static async Task<bool> ExisteComoPropietarioAsync(Escenario e, Guid id)
    {
        await using var conexion = new NpgsqlConnection(e.Arnes.CadenaPropietario);
        await conexion.OpenAsync();
        await using var orden = new NpgsqlCommand("""SELECT EXISTS (SELECT 1 FROM "RegistrosActividadSoporte" WHERE "Id" = @id)""", conexion);
        orden.Parameters.AddWithValue("id", id);
        return (bool)(await orden.ExecuteScalarAsync())!;
    }
}
