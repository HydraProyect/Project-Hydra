using CaeManager.Application.Common;
using CaeManager.Domain.Auditoria;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Operaciones;
using CaeManager.Domain.Soporte;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Seed;
using CaeManager.IntegrationTests.Arranque;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace CaeManager.IntegrationTests.Autenticacion;

/// <summary>
/// <b>P1-M1: la RLS de <c>AspNetUsers</c>, bajo <c>cae_app_runtime</c> real</b> (login
/// del rol, no <c>SET ROLE</c> desde el propietario), migración <c>RlsAspNetUsers</c>.
///
/// <para>
/// Dos instrumentos. Las ramas de la política se miden con SQL directo sobre una
/// conexión de runtime y las variables de sesión fijadas a mano: es la forma de
/// observar cada rama aislada, sin la resolución de contexto de la aplicación por
/// medio. Los caminos de Identity —búsqueda antes del Tenant, unicidad entre Tenants,
/// escritura entre Tenants— se miden con el <c>UserManager</c> real del arnés de
/// arranque, que conecta como runtime con los interceptores de producción.
/// </para>
///
/// <para>
/// Escenario: el Tenant de plataforma P (<see cref="TenantSeedData.IdPorDefecto"/>) y
/// dos Tenants X e Y. Desde X deben verse su propia cuenta y, de Y y P, solo las que
/// guardan una relación con X; nunca <c>ajenaY</c>, que no guarda ninguna.
/// </para>
/// </summary>
public class RlsAspNetUsersBajoRuntimeTests
{
    private static readonly Guid TenantP = TenantSeedData.IdPorDefecto;

    private sealed record Escenario(
        ArnesDeArranqueRuntime Arnes,
        Guid TenantX,
        Guid TenantY,
        ApplicationUser PropiaX,
        ApplicationUser AjenaY,
        ApplicationUser OperadorDelegadoY,
        ApplicationUser GestorCarteraP,
        ApplicationUser ActorAuditoriaY,
        ApplicationUser ActorAccesoSensibleY,
        ApplicationUser SoporteP,
        ApplicationUser GestorCarteraSinOperadorY) : IAsyncDisposable
    {
        public IReadOnlyList<ApplicationUser> Todas =>
        [
            PropiaX, AjenaY, OperadorDelegadoY, GestorCarteraP, ActorAuditoriaY,
            ActorAccesoSensibleY, SoporteP, GestorCarteraSinOperadorY,
        ];

        public ValueTask DisposeAsync() => Arnes.DisposeAsync();
    }

    [Fact]
    public async Task Desde_un_Tenant_se_ven_sus_cuentas_y_las_relacionadas_con_el_y_ninguna_mas()
    {
        await using var e = await CrearEscenarioAsync();

        var visibles = await VisiblesAsync(e, tenant: e.TenantX, origen: null, usuario: null);

        visibles.Should().BeEquivalentTo(new[]
        {
            e.PropiaX.Id,              // A: del Tenant activo
            e.OperadorDelegadoY.Id,    // E1: Operador Delegado sobre X
            e.GestorCarteraP.Id,       // E2: cartera vigente sobre X de su Operador CAE
            e.ActorAuditoriaY.Id,      // G: actor en la auditoría de X
            e.ActorAccesoSensibleY.Id, // G2: actor en los accesos sensibles de X
            e.SoporteP.Id,             // G3: Soporte TALVEG que visitó X
        });
        visibles.Should().NotContain(e.AjenaY.Id, "no guarda ninguna relación con X");
        visibles.Should().NotContain(
            e.GestorCarteraSinOperadorY.Id,
            "su cartera cuelga de una Asignación de Operación de otro Operador CAE: E2 exige el suyo");
    }

    [Fact]
    public async Task Sin_contexto_no_se_ve_ninguna_cuenta()
    {
        await using var e = await CrearEscenarioAsync();

        // Control positivo del instrumento: el propietario ve las ocho.
        (await ContarComoPropietarioAsync(e)).Should().Be(e.Todas.Count);

        (await VisiblesAsync(e, tenant: null, origen: null, usuario: null)).Should().BeEmpty();
    }

    [Fact]
    public async Task La_propia_cuenta_y_las_del_Tenant_de_origen_se_ven_desde_otro_Tenant()
    {
        await using var e = await CrearEscenarioAsync();

        // B: AjenaY operando X ve su propia fila aunque no guarde relación con X.
        var soloPropia = await VisiblesAsync(e, tenant: e.TenantX, origen: null, usuario: e.AjenaY.Id);
        soloPropia.Should().Contain(e.AjenaY.Id);

        // D: con origen Y se ven todas las de Y, también desde X.
        var conOrigen = await VisiblesAsync(e, tenant: e.TenantX, origen: e.TenantY, usuario: null);
        conOrigen.Should().Contain(new[] { e.AjenaY.Id, e.GestorCarteraSinOperadorY.Id });
    }

    [Fact]
    public async Task Ver_una_cuenta_de_otro_Tenant_no_da_permiso_para_escribirla()
    {
        await using var e = await CrearEscenarioAsync();
        await using var conexion = await AbrirComoRuntimeAsync(e, tenant: e.TenantX, origen: e.TenantX, usuario: e.PropiaX.Id);

        // Control positivo: la cuenta propia del Tenant activo sí se escribe.
        (await EjecutarAsync(conexion, """UPDATE "AspNetUsers" SET "NombreCompleto" = 'x' WHERE "Id" = @id""", e.PropiaX.Id))
            .Should().Be(1);

        // Visible por E1, pero ni modificable ni borrable.
        (await EjecutarAsync(conexion, """UPDATE "AspNetUsers" SET "TwoFactorEnabled" = false WHERE "Id" = @id""", e.OperadorDelegadoY.Id))
            .Should().Be(0);
        (await EjecutarAsync(conexion, """DELETE FROM "AspNetUsers" WHERE "Id" = @id""", e.OperadorDelegadoY.Id))
            .Should().Be(0);

        // Nadie trae una cuenta de otro Tenant al suyo.
        var traslado = () => EjecutarAsync(
            conexion, """UPDATE "AspNetUsers" SET "TenantId" = @tenant WHERE "Id" = @id""", e.PropiaX.Id, e.TenantY);
        (await traslado.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("42501");
    }

    [Fact]
    public async Task Quien_opera_otro_Tenant_escribe_su_propia_cuenta_pero_no_puede_trasladarse_a_el()
    {
        await using var e = await CrearEscenarioAsync();
        await using var conexion = await AbrirComoRuntimeAsync(
            e, tenant: e.TenantX, origen: e.TenantY, usuario: e.OperadorDelegadoY.Id);

        (await EjecutarAsync(conexion, """UPDATE "AspNetUsers" SET "NombreCompleto" = 'propio' WHERE "Id" = @id""", e.OperadorDelegadoY.Id))
            .Should().Be(1, "la propia cuenta se escribe desde cualquier Tenant operado (nombre, tema, idioma, 2FA)");

        var traslado = () => EjecutarAsync(
            conexion, """UPDATE "AspNetUsers" SET "TenantId" = @tenant WHERE "Id" = @id""", e.OperadorDelegadoY.Id, e.TenantX);
        (await traslado.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("42501");
    }

    [Fact]
    public async Task El_alta_solo_entra_en_el_Tenant_activo()
    {
        await using var e = await CrearEscenarioAsync();
        await using var conexion = await AbrirComoRuntimeAsync(e, tenant: e.TenantX, origen: e.TenantX, usuario: e.PropiaX.Id);

        // Todas las columnas NOT NULL sin valor por defecto (snapshot del modelo):
        // sin ellas, el rechazo sería 23502 y la política no llegaría a decidir.
        const string alta = """
            INSERT INTO "AspNetUsers" ("Id", "TenantId", "NombreCompleto", "EmailConfirmed", "PhoneNumberConfirmed",
                "TwoFactorEnabled", "LockoutEnabled", "AccessFailedCount", "DebeCambiarContrasena",
                "FechaCreacion", "Idioma", "Tema", "PermisoConsultarAccesoDocumentosSensibles")
            VALUES (@id, @tenant, 'alta de prueba', false, false, false, true, 0, false, now(), 0, 0, false)
            """;

        // Control positivo: la misma fila entra en el Tenant activo.
        (await EjecutarAsync(conexion, alta, Guid.NewGuid(), e.TenantX)).Should().Be(1);

        var altaEnOtro = () => EjecutarAsync(conexion, alta, Guid.NewGuid(), e.TenantY);
        (await altaEnOtro.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("42501");
    }

    [Fact]
    public async Task Sin_Tenant_ni_ambito_de_identificacion_Identity_no_encuentra_la_cuenta()
    {
        await using var e = await CrearEscenarioAsync();
        using var ambito = e.Arnes.Servicios.CreateScope();
        var usuarios = ambito.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        (await usuarios.FindByEmailAsync(e.PropiaX.Email!)).Should().BeNull("sin contexto la política falla cerrada");
        (await usuarios.FindByIdAsync(e.PropiaX.Id.ToString())).Should().BeNull();
    }

    [Fact]
    public async Task En_el_ambito_de_identificacion_Identity_encuentra_la_cuenta_y_la_escribe_en_su_Tenant()
    {
        await using var e = await CrearEscenarioAsync();
        using var ambito = e.Arnes.Servicios.CreateScope();
        var usuarios = ambito.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        using (AmbitoIdentificacionSinTenant.Abrir())
        {
            var porCorreo = await usuarios.FindByEmailAsync(e.AjenaY.Email!);
            porCorreo.Should().NotBeNull();
            porCorreo!.Id.Should().Be(e.AjenaY.Id);

            (await usuarios.FindByIdAsync(e.AjenaY.Id.ToString()))!.Id.Should().Be(e.AjenaY.Id);
            (await usuarios.FindByNameAsync(e.AjenaY.UserName!))!.Id.Should().Be(e.AjenaY.Id);

            // Lo que escribe el login: el contador de intentos fallidos.
            (await usuarios.AccessFailedAsync(porCorreo)).Succeeded.Should().BeTrue();
        }

        (await LeerComoPropietarioAsync(e, e.AjenaY.Id)).AccessFailedCount.Should().Be(1);
    }

    [Fact]
    public async Task Un_ambito_de_identificacion_no_abre_nada_cuando_ya_hay_Tenant()
    {
        await using var e = await CrearEscenarioAsync();
        using var ambito = e.Arnes.Servicios.CreateScope();
        var usuarios = ambito.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        using (AmbitoTenantExplicito.Establecer(e.TenantX))
        using (AmbitoIdentificacionSinTenant.Abrir())
        {
            (await usuarios.FindByEmailAsync(e.AjenaY.Email!)).Should().BeNull(
                "con Tenant en el contexto manda la política de ese Tenant, no la resolución previa al login");
        }
    }

    [Fact]
    public async Task El_nombre_de_usuario_sigue_siendo_unico_entre_Tenants_aunque_la_otra_cuenta_no_se_vea()
    {
        await using var e = await CrearEscenarioAsync();
        using var ambito = e.Arnes.Servicios.CreateScope();
        var usuarios = ambito.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        IdentityResult resultado;
        using (AmbitoTenantExplicito.Establecer(e.TenantX))
        {
            resultado = await usuarios.CreateAsync(new ApplicationUser
            {
                UserName = e.AjenaY.UserName,
                Email = e.AjenaY.Email,
                NombreCompleto = "Duplicada",
                TenantId = e.TenantX,
            });
        }

        resultado.Succeeded.Should().BeFalse();
        resultado.Errors.Select(error => error.Code).Should().Contain(nameof(IdentityErrorDescriber.DuplicateUserName));
    }

    [Fact]
    public async Task Una_cuenta_de_otro_Tenant_visible_no_se_puede_modificar_desde_Identity()
    {
        await using var e = await CrearEscenarioAsync();
        using var ambito = e.Arnes.Servicios.CreateScope();
        var usuarios = ambito.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var sello = (await LeerComoPropietarioAsync(e, e.OperadorDelegadoY.Id)).SecurityStamp;

        IdentityResult resultado;
        using (AmbitoTenantExplicito.Establecer(e.TenantX))
        {
            // Visible por E1: es lo que P0-8 midió que solo frenaba la guarda en C#.
            var ajena = await usuarios.FindByIdAsync(e.OperadorDelegadoY.Id.ToString());
            ajena.Should().NotBeNull("sin verla, la prueba no ejercitaría la política de modificación");

            // Lo mismo que hace el restablecimiento de la 2FA.
            resultado = await usuarios.ResetAuthenticatorKeyAsync(ajena!);
        }

        resultado.Succeeded.Should().BeFalse();
        (await LeerComoPropietarioAsync(e, e.OperadorDelegadoY.Id)).SecurityStamp.Should().Be(sello);
    }

    // ── Escenario ───────────────────────────────────────────────────────

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

        var propiaX = await AltaAsync(contexto, tenantX.Id, "propia.x");
        var ajenaY = await AltaAsync(contexto, tenantY.Id, "ajena.y");
        var operadorY = await AltaAsync(contexto, tenantY.Id, "operador.y");
        var gestorP = await AltaAsync(contexto, TenantP, "gestor.p");
        var auditoriaY = await AltaAsync(contexto, tenantY.Id, "auditoria.y");
        var sensibleY = await AltaAsync(contexto, tenantY.Id, "sensible.y");
        var soporteP = await AltaAsync(contexto, TenantP, "soporte.p");
        var gestorSinOperadorY = await AltaAsync(contexto, tenantY.Id, "gestor.sin.operador.y");

        var ahora = DateTime.UtcNow;

        // E1
        var delegacion = new DelegacionTenant(tenantY.Id, tenantX.Id);
        contexto.DelegacionesTenant.Add(delegacion);
        contexto.AsignacionesOperadorDelegadoConRevocadas.Add(
            new AsignacionOperadorDelegado(delegacion.Id, operadorY.Id, "GestorCae"));

        // E2, y su control negativo: una cartera de una cuenta de Y bajo la
        // Asignación de Operación de P.
        var operacion = AsignacionOperacion.Externa(
            tenantX.Id, TenantP, ServicioCae.Outbound, AmbitoAsignacion.Universal, ahora.AddDays(-1), null, ahora);
        contexto.AsignacionesOperacion.Add(operacion);
        contexto.AsignacionesCartera.Add(AsignacionCartera.Externa(
            operacion, gestorP.Id, "GestorCae", AmbitoAsignacion.Universal, ahora.AddDays(-1), null, ahora));
        contexto.AsignacionesCartera.Add(AsignacionCartera.Externa(
            operacion, gestorSinOperadorY.Id, "GestorCae", AmbitoAsignacion.Universal, ahora.AddDays(-1), null, ahora));

        var delegacionSoporte = DelegacionTenant.ParaSoporte(TenantP, tenantX.Id);
        contexto.DelegacionesTenant.Add(delegacionSoporte);

        using (AmbitoTenantExplicito.Establecer(tenantX.Id))
        {
            await contexto.SaveChangesAsync();

            // G, G2 y G3
            contexto.RegistrosAuditoria.Add(new RegistroAuditoria(
                "Empresa", Guid.NewGuid(), "Modificado", null, null, auditoriaY.Id, TipoActorAuditoria.Persona));
            contexto.RegistrosAccesoDocumentoSensible.Add(new RegistroAccesoDocumentoSensible(
                Guid.NewGuid(), SensibilidadDocumental.DatosPersonales, TipoAccesoDocumentoSensible.Apertura,
                sensibleY.Id, null, TipoViaAccesoAuditoria.Normal, null, TipoActorAuditoria.Persona));
            contexto.RegistrosActividadSoporte.Add(RegistroActividadSoporte.PorViaHeredada(
                soporteP.Id, delegacionSoporte.Id, TipoActividadSoporte.AccesoConcedido));
            await contexto.SaveChangesAsync();
        }

        return new Escenario(
            arnes, tenantX.Id, tenantY.Id, propiaX, ajenaY, operadorY, gestorP, auditoriaY, sensibleY, soporteP,
            gestorSinOperadorY);
    }

    private static async Task<ApplicationUser> AltaAsync(CaeManagerDbContext contexto, Guid tenantId, string alias)
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

        return usuario;
    }

    // ── Instrumentos ────────────────────────────────────────────────────

    private static async Task<HashSet<Guid>> VisiblesAsync(Escenario e, Guid? tenant, Guid? origen, Guid? usuario)
    {
        await using var conexion = await AbrirComoRuntimeAsync(e, tenant, origen, usuario);
        await using var orden = new NpgsqlCommand("""SELECT "Id" FROM "AspNetUsers" WHERE "Id" = ANY(@ids)""", conexion);
        orden.Parameters.AddWithValue("ids", e.Todas.Select(u => u.Id).ToArray());

        var visibles = new HashSet<Guid>();
        await using var lector = await orden.ExecuteReaderAsync();
        while (await lector.ReadAsync())
            visibles.Add(lector.GetGuid(0));
        return visibles;
    }

    private static async Task<NpgsqlConnection> AbrirComoRuntimeAsync(Escenario e, Guid? tenant, Guid? origen, Guid? usuario)
    {
        var conexion = new NpgsqlConnection(BaseDatosPostgresDePruebas.CadenaComoRuntime(e.Arnes.CadenaPropietario));
        await conexion.OpenAsync();

        await using (var identidad = new NpgsqlCommand("SELECT current_user", conexion))
            ((string)(await identidad.ExecuteScalarAsync())!).Should().Be("cae_app_runtime");

        await using var fijar = new NpgsqlCommand(
            "SELECT set_config('app.tenant_id', @t, false), set_config('app.tenant_origen_id', @o, false), " +
            "set_config('app.usuario_id', @u, false);",
            conexion);
        fijar.Parameters.AddWithValue("t", tenant?.ToString() ?? string.Empty);
        fijar.Parameters.AddWithValue("o", origen?.ToString() ?? string.Empty);
        fijar.Parameters.AddWithValue("u", usuario?.ToString() ?? string.Empty);
        await fijar.ExecuteNonQueryAsync();
        return conexion;
    }

    private static async Task<int> EjecutarAsync(NpgsqlConnection conexion, string sql, Guid id, Guid? tenant = null)
    {
        await using var orden = new NpgsqlCommand(sql, conexion);
        orden.Parameters.AddWithValue("id", id);
        if (tenant is { } t) orden.Parameters.AddWithValue("tenant", t);
        return await orden.ExecuteNonQueryAsync();
    }

    private static async Task<long> ContarComoPropietarioAsync(Escenario e)
    {
        await using var conexion = new NpgsqlConnection(e.Arnes.CadenaPropietario);
        await conexion.OpenAsync();
        await using var orden = new NpgsqlCommand("""SELECT COUNT(*) FROM "AspNetUsers" WHERE "Id" = ANY(@ids)""", conexion);
        orden.Parameters.AddWithValue("ids", e.Todas.Select(u => u.Id).ToArray());
        return (long)(await orden.ExecuteScalarAsync())!;
    }

    private static async Task<(int AccessFailedCount, string? SecurityStamp)> LeerComoPropietarioAsync(Escenario e, Guid id)
    {
        await using var conexion = new NpgsqlConnection(e.Arnes.CadenaPropietario);
        await conexion.OpenAsync();
        await using var orden = new NpgsqlCommand(
            """SELECT "AccessFailedCount", "SecurityStamp" FROM "AspNetUsers" WHERE "Id" = @id""", conexion);
        orden.Parameters.AddWithValue("id", id);
        await using var lector = await orden.ExecuteReaderAsync();
        (await lector.ReadAsync()).Should().BeTrue();
        return (lector.GetInt32(0), lector.IsDBNull(1) ? null : lector.GetString(1));
    }
}
