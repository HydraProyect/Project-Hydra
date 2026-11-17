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
/// <c>SET ROLE</c>), migración <c>SoloElAdministradorLeeLaActividadDeSoporte</c>: además de
/// <c>aislamiento_tenant</c>, la política restrictiva <c>solo_el_administrador_o_la_plataforma_de_la_visita</c> solo
/// entrega las filas al Administrador del Tenant propietario y a la plataforma por su propia visita.
///
/// <para>
/// SQL directo sobre una conexión de runtime con el contexto firmado a mano (<see cref="ContextoRlsDePruebas"/>), sin
/// Application por medio: la propiedad es de la base y tiene que valer aunque aparezca un lector nuevo o el handler
/// pierda su comprobación de rol. Cada caso negativo lleva su control positivo: el propietario ve la fila, así que un
/// cero del runtime es la política y no una fila que no existe.
/// </para>
///
/// <para>
/// Sensibilidad: con el <c>USING</c> de la política reescrito a <c>true</c>, o sin la llamada a
/// <c>app_es_administrador_del_tenant</c> en su primera rama, se ponen en rojo
/// <see cref="Otro_usuario_del_Tenant_propietario_no_lee_ninguna_fila"/> y
/// <see cref="Un_usuario_de_otro_Tenant_no_lee_ninguna_fila"/>; sin la rama de la plataforma, se pone en rojo
/// <see cref="La_plataforma_lee_la_traza_de_su_propia_visita"/>.
/// </para>
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
        Guid FilaY) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Arnes.DisposeAsync();
    }

    [Fact]
    public async Task El_Administrador_del_Tenant_propietario_lee_sus_filas_y_solo_las_suyas()
    {
        await using var e = await CrearEscenarioAsync();

        (await VisiblesAsync(e, tenant: e.TenantX, origen: e.TenantX, usuario: e.AdministradorX))
            .Should().Equal([e.FilaX], "es el Administrador del Tenant propietario de la fila");
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
        (await ExisteComoPropietarioAsync(e, e.FilaX)).Should().BeTrue("control positivo: la fila existe");

        // El Administrador de Y con el Tenant X en el contexto: aislamiento_tenant lo dejaría pasar, la
        // restrictiva no (no es Administrador de X, y Y no es la plataforma de la visita).
        (await VisiblesAsync(e, tenant: e.TenantX, origen: e.TenantY, usuario: e.AdministradorY))
            .Should().BeEmpty("es Administrador de otro Tenant, no del propietario de la fila");

        // Desde su propio Tenant ve las suyas y ninguna de X.
        (await VisiblesAsync(e, tenant: e.TenantY, origen: e.TenantY, usuario: e.AdministradorY))
            .Should().Equal([e.FilaY]);
    }

    [Fact]
    public async Task La_plataforma_lee_la_traza_de_su_propia_visita()
    {
        await using var e = await CrearEscenarioAsync();

        // Es el camino de ObtenerActividadSoporteQuery para la plataforma: Tenant de origen P y ámbito en el
        // Tenant visitado (FlujoSoporteTests, E2E).
        (await VisiblesAsync(e, tenant: e.TenantX, origen: TenantP, usuario: e.SoporteP))
            .Should().Equal([e.FilaX], "la fila cuelga de la delegación de soporte de P sobre X");

        // La misma cuenta, sin el Tenant de origen plataforma en el contexto, no ve nada.
        (await VisiblesAsync(e, tenant: e.TenantX, origen: e.TenantY, usuario: e.SoporteP))
            .Should().BeEmpty("la rama de la plataforma se ata al Tenant de origen del contexto firmado");
    }

    [Fact]
    public async Task El_runtime_sigue_insertando_la_traza_sin_poder_leerla()
    {
        await using var e = await CrearEscenarioAsync();
        var nueva = RegistroActividadSoporte.PorViaHeredada(
            e.SoporteP, Guid.NewGuid(), TipoActividadSoporte.Navegacion, "/documentos");

        // El camino real de TrazaSoporteService: EF con los interceptores de producción, como runtime, sin
        // Administrador en el contexto. El INSERT no lleva RETURNING, así que la política de SELECT no lo toca.
        using (var ambito = e.Arnes.Servicios.CreateScope())
        {
            var contexto = ambito.ServiceProvider.GetRequiredService<CaeManagerDbContext>();
            using var ambitoTenant = AmbitoTenantExplicito.Establecer(e.TenantX);
            contexto.RegistrosActividadSoporte.Add(nueva);
            await contexto.SaveChangesAsync();
        }

        (await ExisteComoPropietarioAsync(e, nueva.Id)).Should().BeTrue("el INSERT del runtime tiene que seguir funcionando");
        (await VisiblesAsync(e, tenant: e.TenantX, origen: e.TenantX, usuario: e.AdministradorX))
            .Should().Contain(nueva.Id, "y el Administrador la lee después");
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

        var filaX = RegistroActividadSoporte.PorViaHeredada(soporteP, soporteSobreX.Id, TipoActividadSoporte.AccesoConcedido);
        using (AmbitoTenantExplicito.Establecer(tenantX.Id))
        {
            contexto.RegistrosActividadSoporte.Add(filaX);
            await contexto.SaveChangesAsync();
        }

        var filaY = RegistroActividadSoporte.PorViaHeredada(soporteP, soporteSobreY.Id, TipoActividadSoporte.AccesoConcedido);
        using (AmbitoTenantExplicito.Establecer(tenantY.Id))
        {
            contexto.RegistrosActividadSoporte.Add(filaY);
            await contexto.SaveChangesAsync();
        }

        var e = new Escenario(arnes, tenantX.Id, tenantY.Id, administradorX, consultaX, administradorY, soporteP, filaX.Id, filaY.Id);
        await AsignarRolAsync(e, administradorX, Roles.Administrador);
        await AsignarRolAsync(e, consultaX, Roles.Consulta);
        await AsignarRolAsync(e, administradorY, Roles.Administrador);

        // Barrera del escenario: la rama de la plataforma exige que P esté marcado como plataforma.
        await using var conexion = new NpgsqlConnection(arnes.CadenaPropietario);
        await conexion.OpenAsync();
        await using var orden = new NpgsqlCommand("""SELECT "EsPlataforma" FROM "Tenants" WHERE "Id" = @p""", conexion);
        orden.Parameters.AddWithValue("p", TenantP);
        ((bool)(await orden.ExecuteScalarAsync())!).Should().BeTrue("el Tenant por defecto es el de plataforma");

        return e;
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

    private static async Task<List<Guid>> VisiblesAsync(Escenario e, Guid? tenant, Guid? origen, Guid? usuario)
    {
        await using var conexion = new NpgsqlConnection(BaseDatosPostgresDePruebas.CadenaComoRuntime(e.Arnes.CadenaPropietario));
        await conexion.OpenAsync();

        await using (var identidad = new NpgsqlCommand("SELECT current_user", conexion))
            ((string)(await identidad.ExecuteScalarAsync())!).Should().Be("cae_app_runtime");

        await ContextoRlsDePruebas.FijarAsync(conexion, tenantId: tenant, tenantOrigenId: origen, usuarioId: usuario);

        await using var orden = new NpgsqlCommand("""SELECT "Id" FROM "RegistrosActividadSoporte" ORDER BY "Id" """, conexion);
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
