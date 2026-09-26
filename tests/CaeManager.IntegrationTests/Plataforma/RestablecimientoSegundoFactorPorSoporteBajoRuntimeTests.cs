using CaeManager.Application.Common;
using CaeManager.Application.Plataforma.Commands.AbrirSesionPrivilegiada;
using CaeManager.Domain.Plataforma;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Interceptors;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;
using PlataformaWriter = CaeManager.Infrastructure.Plataforma.PlataformaWriter;

namespace CaeManager.IntegrationTests.Plataforma;

/// <summary>
/// ADR-011 § 8.7, punto 3: Soporte TALVEG restablece la verificación en dos pasos del
/// Administrador único de un Tenant desde una Sesión Privilegiada con la capacidad
/// <c>RestablecimientoSegundoFactor</c>, contra PostgreSQL real y con la identidad de
/// conexión de producción, <c>cae_app_runtime</c>, que con la sesión adopta
/// <c>cae_app_soporte</c>. Nunca como propietario ni como superusuario: el
/// propietario solo siembra y lee lo que quedó escrito.
///
/// <para>
/// Lo que se ataca: la conexión de la sesión sigue siendo de solo lectura y la RLS
/// no se ensancha. La única escritura posible es la función
/// <c>app_restablecer_segundo_factor_por_soporte</c>, y cada precondición que la
/// función vuelve a comprobar tiene aquí un caso que la incumple y deja la cuenta
/// intacta. Las reglas de Application (capacidad, simulación, Tenant objetivo,
/// Administrador único) están en <c>SegundoFactorCommandsTests</c>; aquí se prueba
/// que la base no depende de ellas.
/// </para>
/// </summary>
public class RestablecimientoSegundoFactorPorSoporteBajoRuntimeTests : IAsyncLifetime
{
    private const string ValorClave = "CLAVE-TOTP-SECRETA-DE-PRUEBA";
    private const string ValorCodigos = "pbkdf2-sha256$codigos-de-prueba";

    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenantPlataforma = Guid.NewGuid();
    private readonly Guid _tenantA = Guid.NewGuid();
    private readonly Guid _tenantB = Guid.NewGuid();
    private readonly Guid _tecnico = Guid.NewGuid();
    private readonly Guid _adminPlataforma = Guid.NewGuid();

    private Guid _rolAdministrador;
    private Guid _administradorDeA;
    private Guid _gestorDeA;
    private Guid _administradorDeB;

    public async Task InitializeAsync()
    {
        await BaseDatosPostgresDePruebas.MigrarAsync(_cadenaConexion);

        // Siembra como propietario: el rol de runtime no crea Tenants ni cuentas.
        await using var contexto = CrearContexto(_tenantPlataforma, cadena: _cadenaConexion);
        contexto.Tenants.Add(CrearTenant(_tenantPlataforma, "Plataforma", esPlataforma: true));
        contexto.Tenants.Add(CrearTenant(_tenantA, "Tenant A S.L."));
        contexto.Tenants.Add(CrearTenant(_tenantB, "Tenant B S.L."));

        // Los roles los siembra la migración base (HasData): se reutilizan.
        _rolAdministrador = (await contexto.Roles.SingleAsync(r => r.Name == Roles.Administrador)).Id;
        var rolGestor = (await contexto.Roles.SingleAsync(r => r.Name == Roles.GestorCae)).Id;

        _administradorDeA = SembrarCuenta(contexto, "admin-a", _tenantA, _rolAdministrador);
        _gestorDeA = SembrarCuenta(contexto, "gestor-a", _tenantA, rolGestor);
        _administradorDeB = SembrarCuenta(contexto, "admin-b", _tenantB, _rolAdministrador);
        // El técnico de Soporte TALVEG es una cuenta del Tenant de plataforma.
        SembrarCuenta(contexto, "tecnico", _tenantPlataforma, rol: null, id: _tecnico);
        SembrarCuenta(contexto, "admin-plataforma", _tenantPlataforma, rol: null, id: _adminPlataforma);
        await contexto.SaveChangesAsync();
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    // ── Camino positivo ──────────────────────────────────────────────────────

    [Fact]
    public async Task Con_la_capacidad_restablece_al_Administrador_unico_y_deja_la_auditoria_de_la_sesion()
    {
        var sesion = await AbrirSesionConCapacidadAsync();
        var selloAntes = await EscalarComoPropietarioAsync<string>(
            @"SELECT ""SecurityStamp"" FROM ""AspNetUsers"" WHERE ""Id"" = @u;", _administradorDeA);

        await using (var contexto = CrearContexto(_tenantA, sesionPrivilegiadaId: sesion))
        {
            var puerto = Puerto(contexto);
            (await puerto.EsAdministradorUnicoActivoAsync(_administradorDeA, _tenantA)).Should().BeTrue();
            // Como hace Identity al leer el estado: los tokens quedan rastreados.
            (await contexto.UserTokens.Where(t => t.UserId == _administradorDeA).ToListAsync())
                .Should().HaveCount(2, "control positivo: hay tokens rastreados que desenganchar");
            var resultado = await puerto.RestablecerPorSesionPrivilegiadaAsync(sesion, _administradorDeA);
            resultado.EsExitoso.Should().BeTrue(resultado.EsFallido ? resultado.Error.Mensaje : "");
            contexto.ChangeTracker.Entries<IdentityUserToken<Guid>>()
                .Should().BeEmpty("la función borró la clave y los códigos: el circuito no puede reutilizarlos");
        }

        (await EscalarComoPropietarioAsync<bool>(
            @"SELECT ""TwoFactorEnabled"" FROM ""AspNetUsers"" WHERE ""Id"" = @u;", _administradorDeA)).Should().BeFalse();
        (await EscalarComoPropietarioAsync<string>(
            @"SELECT ""SecurityStamp"" FROM ""AspNetUsers"" WHERE ""Id"" = @u;", _administradorDeA))
            .Should().NotBe(selloAntes, "el sello nuevo cierra las sesiones abiertas de la cuenta");
        (await ContarTokensAsync(_administradorDeA)).Should().Be(0);

        var filas = await LeerAuditoriaDeSesionAsync(sesion);
        filas.Should().HaveCount(3, "una fila de la cuenta y una por cada token borrado");
        filas.Should().OnlyContain(f =>
            f.TenantId == _tenantA && f.EntidadId == _administradorDeA
            && f.UsuarioId == _tecnico && f.ActorRealUsuarioId == _tecnico && f.TipoActor == "Persona",
            "el Actor real es el técnico de Soporte TALVEG; la cuenta afectada va en EntidadId");
        filas.Where(f => f.EntidadTipo == "Usuario").Should().ContainSingle()
            .Which.Accion.Should().Be("Modificado");
        filas.Where(f => f.EntidadTipo == "TokenDeUsuario").Should().HaveCount(2)
            .And.OnlyContain(f => f.Accion == "Eliminado");
        filas.Should().NotContain(f => (f.DatosAntes + f.DatosDespues).Contains(ValorClave)
                                       || (f.DatosAntes + f.DatosDespues).Contains(ValorCodigos),
            "el valor de los tokens se enmascara como en AuditoriaInterceptor");

        (await EscalarComoPropietarioAsync<long>(
            @"SELECT COUNT(*) FROM ""AspNetUserTokens"" WHERE ""UserId"" = @u;", _administradorDeB)).Should().Be(2,
            "la cuenta de otro Tenant sigue intacta");
    }

    [Fact]
    public async Task Un_Administrador_desactivado_no_cuenta_como_otro_Administrador()
    {
        await using (var contexto = CrearContexto(_tenantPlataforma, cadena: _cadenaConexion))
        {
            SembrarCuenta(contexto, "admin-a-baja", _tenantA, _rolAdministrador, desactivada: true);
            await contexto.SaveChangesAsync();
        }
        var sesion = await AbrirSesionConCapacidadAsync();

        (await RestablecerComoSoporteAsync(_tenantA, sesion, _administradorDeA)).Should().Be("restablecido");
    }

    // ── Cada precondición de la función, incumplida ──────────────────────────

    [Fact]
    public async Task Una_sesion_de_SoporteLectura_no_restablece()
    {
        var sesion = await AbrirSesionDeSoporteLecturaAsync();

        (await RestablecerComoSoporteAsync(_tenantA, sesion, _administradorDeA)).Should().Be("sesion_no_autorizada");
        await CuentaIntactaAsync(_administradorDeA);
    }

    [Fact]
    public async Task Una_sesion_por_Tenant_de_otra_capacidad_no_restablece()
    {
        // Aísla la comprobación de capacidad: la de SoporteLectura es global y
        // ya la para EsAlcanceGlobal. Aprovisionamiento es por Tenant y alcanza A.
        var sesion = await AbrirSesionConCapacidadAsync(capacidad: CapacidadPrivilegio.Aprovisionamiento);

        (await RestablecerComoSoporteAsync(_tenantA, sesion, _administradorDeA)).Should().Be("sesion_no_autorizada");
        await CuentaIntactaAsync(_administradorDeA);
    }

    [Fact]
    public async Task Una_sesion_sobre_A_no_se_ejerce_en_B_aunque_la_concesion_alcance_los_dos()
    {
        // Aísla la comprobación del Tenant objetivo de la sesión: la concesión
        // alcanza A y B, la sesión se abrió sobre A y el contexto es B.
        var sesion = await AbrirSesionConCapacidadAsync(tenantsAlcanzados: [_tenantA, _tenantB]);

        (await RestablecerComoSoporteAsync(_tenantB, sesion, _administradorDeB)).Should().Be("sesion_no_autorizada");
        await CuentaIntactaAsync(_administradorDeB);
    }

    [Fact]
    public async Task Una_sesion_cerrada_no_restablece()
    {
        var sesion = await AbrirSesionConCapacidadAsync();
        await EjecutarComoPropietarioAsync(
            @"UPDATE ""SesionesPrivilegiadas"" SET ""CerradaEnUtc"" = now() WHERE ""Id"" = @u;", sesion);

        (await RestablecerComoSoporteAsync(_tenantA, sesion, _administradorDeA)).Should().Be("sesion_no_autorizada");
        await CuentaIntactaAsync(_administradorDeA);
    }

    [Fact]
    public async Task Una_sesion_caducada_no_restablece()
    {
        var sesion = await AbrirSesionConCapacidadAsync();
        await EjecutarComoPropietarioAsync(
            @"UPDATE ""SesionesPrivilegiadas"" SET ""ExpiraEnUtc"" = now() - interval '1 minute' WHERE ""Id"" = @u;", sesion);

        (await RestablecerComoSoporteAsync(_tenantA, sesion, _administradorDeA)).Should().Be("sesion_no_autorizada");
        await CuentaIntactaAsync(_administradorDeA);
    }

    [Fact]
    public async Task Una_sesion_que_simula_a_un_usuario_no_restablece()
    {
        var sesion = await AbrirSesionConCapacidadAsync();
        await EjecutarComoPropietarioAsync(
            @"UPDATE ""SesionesPrivilegiadas"" SET ""UsuarioSimuladoId"" = gen_random_uuid() WHERE ""Id"" = @u;", sesion);

        (await RestablecerComoSoporteAsync(_tenantA, sesion, _administradorDeA)).Should().Be("sesion_no_autorizada");
        await CuentaIntactaAsync(_administradorDeA);
    }

    [Fact]
    public async Task Una_concesion_revocada_no_restablece_aunque_la_sesion_siga_abierta()
    {
        var sesion = await AbrirSesionConCapacidadAsync();
        await EjecutarComoPropietarioAsync(@"
UPDATE ""ConcesionesPrivilegio"" SET ""Estado"" = 'Revocada'
WHERE ""Id"" = (SELECT ""ConcesionPrivilegioId"" FROM ""SesionesPrivilegiadas"" WHERE ""Id"" = @u);", sesion);

        (await RestablecerComoSoporteAsync(_tenantA, sesion, _administradorDeA)).Should().Be("sesion_no_autorizada");
        await CuentaIntactaAsync(_administradorDeA);
    }

    [Fact]
    public async Task Una_concesion_caducada_no_restablece_aunque_la_sesion_siga_abierta()
    {
        var sesion = await AbrirSesionConCapacidadAsync();
        await EjecutarComoPropietarioAsync(@"
UPDATE ""ConcesionesPrivilegio"" SET ""VigenciaHasta"" = now() - interval '1 minute'
WHERE ""Id"" = (SELECT ""ConcesionPrivilegioId"" FROM ""SesionesPrivilegiadas"" WHERE ""Id"" = @u);", sesion);

        (await RestablecerComoSoporteAsync(_tenantA, sesion, _administradorDeA)).Should().Be("sesion_no_autorizada");
        await CuentaIntactaAsync(_administradorDeA);
    }

    [Fact]
    public async Task Una_concesion_que_el_tecnico_se_dio_a_si_mismo_no_restablece()
    {
        // El WITH CHECK de la política admite, a nivel de base, que un usuario se
        // inserte una concesión propia de cualquier capacidad (preexistente). La
        // función no la acepta: la capacidad exige que la conceda otra persona.
        var sesion = await AbrirSesionConCapacidadAsync(concedidaPor: _tecnico);

        (await RestablecerComoSoporteAsync(_tenantA, sesion, _administradorDeA)).Should().Be("sesion_no_autorizada");
        await CuentaIntactaAsync(_administradorDeA);
    }

    [Fact]
    public async Task Una_concesion_que_no_alcanza_el_Tenant_no_restablece()
    {
        var sesion = await AbrirSesionConCapacidadAsync();
        await EjecutarComoPropietarioAsync(@"
UPDATE ""TenantsAlcanzadosPorConcesion"" SET ""TenantId"" = gen_random_uuid()
WHERE ""ConcesionPrivilegioId"" = (SELECT ""ConcesionPrivilegioId"" FROM ""SesionesPrivilegiadas"" WHERE ""Id"" = @u);", sesion);

        (await RestablecerComoSoporteAsync(_tenantA, sesion, _administradorDeA)).Should().Be("sesion_no_autorizada");
        await CuentaIntactaAsync(_administradorDeA);
    }

    [Fact]
    public async Task La_sesion_sobre_A_no_restablece_con_el_contexto_de_otro_Tenant()
    {
        var sesion = await AbrirSesionConCapacidadAsync();

        (await RestablecerComoSoporteAsync(_tenantB, sesion, _administradorDeB)).Should().Be("sesion_no_autorizada");
        await CuentaIntactaAsync(_administradorDeB);
    }

    [Fact]
    public async Task Una_cuenta_que_no_es_de_un_Tenant_de_plataforma_no_ejerce_la_capacidad()
    {
        // Aunque tenga concesión y sesión abierta (el concedente se equivocó de
        // beneficiario), quien no es Actor de Plataforma TALVEG no restablece.
        var sesion = await AbrirSesionConCapacidadAsync();
        (await EscalarComoPropietarioAsync<int>(
                $@"UPDATE ""AspNetUsers"" SET ""TenantId"" = '{_tenantB}' WHERE ""Id"" = @u RETURNING 1;", _tecnico))
            .Should().Be(1, "control positivo: la cuenta del técnico existía y ahora es de B");

        (await RestablecerComoSoporteAsync(_tenantA, sesion, _administradorDeA)).Should().Be("sesion_no_autorizada");
        await CuentaIntactaAsync(_administradorDeA);
    }

    [Fact]
    public async Task Otra_persona_no_ejerce_la_sesion_del_tecnico_aunque_conozca_su_Id()
    {
        var sesion = await AbrirSesionConCapacidadAsync();

        await using (var contexto = CrearContexto(_tenantA, sesionPrivilegiadaId: sesion, actor: _adminPlataforma))
        {
            (await LlamarFuncionAsync(contexto, sesion, _administradorDeA)).Should().Be("sesion_no_autorizada");
        }
        await CuentaIntactaAsync(_administradorDeA);
    }

    [Fact]
    public async Task Una_cuenta_de_otro_Tenant_no_se_alcanza()
    {
        // La función es SECURITY DEFINER de un propietario que evita la RLS de
        // AspNetUsers (P1-M1): tiene que filtrar ella misma por el Tenant.
        var sesion = await AbrirSesionConCapacidadAsync();

        (await RestablecerComoSoporteAsync(_tenantA, sesion, _administradorDeB)).Should().Be("cuenta_no_encontrada");
        await CuentaIntactaAsync(_administradorDeB);
    }

    [Fact]
    public async Task Dentro_de_la_sesion_sobre_A_la_RLS_de_cuentas_oculta_al_Administrador_de_B()
    {
        // Control negativo del contrato de P1-M1: la lectura de la pantalla
        // corre con el Tenant objetivo como Tenant de contexto, y desde ahí la
        // política cuentas_lectura no deja ver las cuentas de otro Tenant.
        var sesion = await AbrirSesionConCapacidadAsync();
        (await EscalarComoPropietarioAsync<long>(
                @"SELECT count(*) FROM ""AspNetUsers"" WHERE ""Id"" = @u;", _administradorDeB))
            .Should().Be(1, "control positivo: la cuenta de B existe");

        await using var contexto = CrearContexto(_tenantA, sesionPrivilegiadaId: sesion);
        var puerto = Puerto(contexto);

        (await puerto.ObtenerAdministradorUnicoActivoAsync(_tenantA))!.UsuarioId
            .Should().Be(_administradorDeA, "control positivo: el del Tenant objetivo sí se ve");
        (await puerto.ObtenerAdministradorUnicoActivoAsync(_tenantB)).Should().BeNull();
        (await contexto.Users.AsNoTracking().AnyAsync(u => u.Id == _administradorDeB)).Should().BeFalse();
    }

    [Fact]
    public async Task Una_cuenta_que_no_es_Administrador_no_se_restablece()
    {
        var sesion = await AbrirSesionConCapacidadAsync();

        (await RestablecerComoSoporteAsync(_tenantA, sesion, _gestorDeA)).Should().Be("no_es_administrador");
        await CuentaIntactaAsync(_gestorDeA);
    }

    [Fact]
    public async Task Si_hay_otro_Administrador_activo_lo_restablece_el_y_no_Soporte()
    {
        await using (var contexto = CrearContexto(_tenantPlataforma, cadena: _cadenaConexion))
        {
            SembrarCuenta(contexto, "admin-a-2", _tenantA, _rolAdministrador);
            await contexto.SaveChangesAsync();
        }
        var sesion = await AbrirSesionConCapacidadAsync();

        (await RestablecerComoSoporteAsync(_tenantA, sesion, _administradorDeA)).Should().Be("hay_otro_administrador");
        await CuentaIntactaAsync(_administradorDeA);
    }

    [Fact]
    public async Task Una_cuenta_desactivada_no_se_restablece()
    {
        var sesion = await AbrirSesionConCapacidadAsync();
        await EjecutarComoPropietarioAsync(
            @"UPDATE ""AspNetUsers"" SET ""LockoutEnd"" = 'infinity' WHERE ""Id"" = @u;", _administradorDeA);

        (await RestablecerComoSoporteAsync(_tenantA, sesion, _administradorDeA)).Should().Be("cuenta_no_encontrada");
        await CuentaIntactaAsync(_administradorDeA);
    }

    [Fact]
    public async Task Una_cuenta_sin_2FA_activa_no_se_toca()
    {
        var sesion = await AbrirSesionConCapacidadAsync();
        await EjecutarComoPropietarioAsync(
            @"UPDATE ""AspNetUsers"" SET ""TwoFactorEnabled"" = false WHERE ""Id"" = @u;", _administradorDeA);

        (await RestablecerComoSoporteAsync(_tenantA, sesion, _administradorDeA)).Should().Be("sin_segundo_factor");
        (await ContarTokensAsync(_administradorDeA)).Should().Be(2);
        (await LeerAuditoriaDeSesionAsync(sesion)).Should().BeEmpty();
    }

    // ── Alta concurrente de otro Administrador (ADR-011 § 8.7.3) ─────────────
    //
    // El FOR UPDATE de la función solo bloquea la fila de la cuenta que restablece;
    // el alta de OTRO Administrador no la toca. Sin el cerrojo por Tenant, una
    // transacción de alta sin confirmar es invisible para la función, que
    // restablece, y al confirmar quedan dos Administradores activos. El alta se
    // hace como propietario porque lo que se prueba es el trigger, que dispara
    // con cualquier rol.

    [Fact]
    public async Task Una_asignacion_concurrente_del_rol_Administrador_hace_esperar_al_restablecimiento()
    {
        var sesion = await AbrirSesionConCapacidadAsync();
        await using var alta = await AltaSinConfirmarAsync(
            @"INSERT INTO ""AspNetUserRoles"" (""UserId"", ""RoleId"") VALUES (@u, @r);", _gestorDeA);

        var restablecer = Task.Run(() => RestablecerComoSoporteAsync(_tenantA, sesion, _administradorDeA));
        await EsperarBloqueadoPorElCerrojoAsync(restablecer);
        await alta.ConfirmarAsync();

        (await restablecer.WaitAsync(TimeSpan.FromSeconds(30))).Should().Be("hay_otro_administrador",
            "tras esperar al alta, la función ve al segundo Administrador ya confirmado");
        await CuentaIntactaAsync(_administradorDeA);
    }

    [Fact]
    public async Task Una_reactivacion_concurrente_de_otro_Administrador_hace_esperar_al_restablecimiento()
    {
        Guid desactivado;
        await using (var contexto = CrearContexto(_tenantPlataforma, cadena: _cadenaConexion))
        {
            desactivado = SembrarCuenta(contexto, "admin-a-baja", _tenantA, _rolAdministrador, desactivada: true);
            await contexto.SaveChangesAsync();
        }
        var sesion = await AbrirSesionConCapacidadAsync();
        await using var alta = await AltaSinConfirmarAsync(
            @"UPDATE ""AspNetUsers"" SET ""LockoutEnd"" = NULL WHERE ""Id"" = @u;", desactivado);

        var restablecer = Task.Run(() => RestablecerComoSoporteAsync(_tenantA, sesion, _administradorDeA));
        await EsperarBloqueadoPorElCerrojoAsync(restablecer);
        await alta.ConfirmarAsync();

        (await restablecer.WaitAsync(TimeSpan.FromSeconds(30))).Should().Be("hay_otro_administrador");
        await CuentaIntactaAsync(_administradorDeA);
    }

    /// <summary>
    /// Orden de cerrojos (revisión Codex): el trigger corre con la fila de la cuenta
    /// ya bloqueada y después pide el cerrojo del Tenant, así que la función tiene que
    /// pedirlos en el mismo orden. Se fuerza el cruce: un alta retiene el cerrojo, el
    /// restablecimiento espera, y mientras tanto se actualiza la <c>LockoutEnd</c> de la
    /// propia cuenta objetivo (sigue activa). Con el orden inverso, al soltarse el alta
    /// las dos transacciones se esperan mutuamente y Postgres aborta una con 40P01.
    /// </summary>
    [Fact]
    public async Task Tocar_la_propia_cuenta_mientras_se_restablece_espera_sin_interbloqueo()
    {
        var sesion = await AbrirSesionConCapacidadAsync();
        await using var alta = await AltaSinConfirmarAsync(
            @"INSERT INTO ""AspNetUserRoles"" (""UserId"", ""RoleId"") VALUES (@u, @r);", _gestorDeA);

        var restablecer = Task.Run(() => RestablecerComoSoporteAsync(_tenantA, sesion, _administradorDeA));
        await EsperarBloqueadoPorElCerrojoAsync(restablecer);

        var tocarCuenta = Task.Run(async () =>
        {
            await using var conexion = new NpgsqlConnection(_cadenaConexion);
            await conexion.OpenAsync();
            await using var comando = conexion.CreateCommand();
            comando.CommandText =
                @"UPDATE ""AspNetUsers"" SET ""LockoutEnd"" = '2000-01-01T00:00:00Z' WHERE ""Id"" = @u;";
            comando.Parameters.AddWithValue("u", _administradorDeA);
            return await comando.ExecuteNonQueryAsync();
        });
        await EsperarEsperasDeCerrojoAsync(2, restablecer, tocarCuenta);
        await alta.ConfirmarAsync();

        (await restablecer.WaitAsync(TimeSpan.FromSeconds(30))).Should().Be("hay_otro_administrador");
        (await tocarCuenta.WaitAsync(TimeSpan.FromSeconds(30))).Should().Be(1);
    }

    /// <summary>
    /// Control de que el cerrojo es estrecho y de que los dos tests de arriba miden la
    /// espera y no otra cosa: un rol que no es Administrador, o un Administrador de otro
    /// Tenant, no hace esperar. La función restablece con el alta aún sin confirmar.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Un_alta_que_no_es_de_Administrador_de_ese_Tenant_no_hace_esperar(bool enOtroTenant)
    {
        Guid cuenta;
        Guid rol;
        await using (var contexto = CrearContexto(_tenantPlataforma, cadena: _cadenaConexion))
        {
            cuenta = SembrarCuenta(contexto, "sin-rol", enOtroTenant ? _tenantB : _tenantA, rol: null);
            await contexto.SaveChangesAsync();
            rol = enOtroTenant
                ? _rolAdministrador
                : (await contexto.Roles.SingleAsync(r => r.Name == Roles.Consulta)).Id;
        }
        var sesion = await AbrirSesionConCapacidadAsync();
        await using var alta = await AltaSinConfirmarAsync(
            @"INSERT INTO ""AspNetUserRoles"" (""UserId"", ""RoleId"") VALUES (@u, @r);", cuenta, rol);

        (await RestablecerComoSoporteAsync(_tenantA, sesion, _administradorDeA).WaitAsync(TimeSpan.FromSeconds(30)))
            .Should().Be("restablecido");
    }

    // ── Quién puede ejecutarla ───────────────────────────────────────────────

    [Fact]
    public async Task Sin_Sesion_Privilegiada_el_rol_de_runtime_no_puede_ejecutarla()
    {
        var sesion = await AbrirSesionConCapacidadAsync();

        // Mismo técnico, mismo Tenant, pero sin la sesión en la conexión: el rol es
        // cae_app_runtime, que no tiene EXECUTE.
        await using (var contexto = CrearContexto(_tenantA))
        {
            var ejecutar = async () => await LlamarFuncionAsync(contexto, sesion, _administradorDeA);
            (await ejecutar.Should().ThrowAsync<PostgresException>())
                .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
        }

        // Y el adaptador lo traduce a una negativa, nunca a un 500 ni a una escritura.
        await using (var contexto = CrearContexto(_tenantA))
        {
            var resultado = await Puerto(contexto).RestablecerPorSesionPrivilegiadaAsync(sesion, _administradorDeA);
            resultado.EsFallido.Should().BeTrue();
            resultado.Error.Codigo.Should().Be("SegundoFactor.RestablecimientoPorSoporteDenegado");
            resultado.Error.Mensaje.Should().Contain("contexto_no_valido");
        }

        await CuentaIntactaAsync(_administradorDeA);
    }

    [Theory]
    [InlineData("cae_app_runtime", false)]
    [InlineData("cae_app_soporte", true)]
    [InlineData("cae_app_aprovisionamiento", false)]
    public async Task Solo_cae_app_soporte_tiene_EXECUTE(string rol, bool esperado)
    {
        await using var conexion = new NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();
        await using var comando = conexion.CreateCommand();
        comando.CommandText =
            "SELECT has_function_privilege(@rol, 'app_restablecer_segundo_factor_por_soporte(uuid, uuid)', 'EXECUTE');";
        comando.Parameters.AddWithValue("rol", rol);

        ((bool)(await comando.ExecuteScalarAsync())!).Should().Be(esperado);
    }

    [Fact]
    public async Task La_sesion_sigue_sin_poder_escribir_la_cuenta_por_EF()
    {
        // Control de la tesis: la capacidad no da UPDATE a la conexión. Solo la
        // función escribe.
        var sesion = await AbrirSesionConCapacidadAsync();
        await using var contexto = CrearContexto(_tenantA, sesionPrivilegiadaId: sesion);
        var cuenta = await contexto.Users.SingleAsync(u => u.Id == _administradorDeA);
        cuenta.TwoFactorEnabled = false;

        var guardar = async () => await contexto.SaveChangesAsync();

        (await guardar.Should().ThrowAsync<DbUpdateException>())
            .Which.InnerException.Should().BeOfType<PostgresException>()
            .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
        await CuentaIntactaAsync(_administradorDeA);
    }

    // ── Andamiaje ────────────────────────────────────────────────────────────

    private Guid SembrarCuenta(
        CaeManagerDbContext contexto, string nombre, Guid tenant, Guid? rol, bool desactivada = false, Guid? id = null)
    {
        var correo = $"{nombre}@restablecer-soporte.test";
        var cuenta = new ApplicationUser
        {
            UserName = correo,
            NormalizedUserName = correo.ToUpperInvariant(),
            Email = correo,
            NormalizedEmail = correo.ToUpperInvariant(),
            NombreCompleto = nombre,
            TenantId = tenant,
            TwoFactorEnabled = true,
            SecurityStamp = Guid.NewGuid().ToString("N").ToUpperInvariant(),
        };
        if (desactivada) cuenta.LockoutEnd = ApplicationUser.FinDeBloqueoDeCuentaDesactivada;
        if (id is not null) cuenta.Id = id.Value;
        contexto.Users.Add(cuenta);
        if (rol is not null) contexto.UserRoles.Add(new IdentityUserRole<Guid> { UserId = cuenta.Id, RoleId = rol.Value });
        contexto.UserTokens.Add(new IdentityUserToken<Guid>
        {
            UserId = cuenta.Id,
            LoginProvider = "[AspNetUserStore]",
            Name = "AuthenticatorKey",
            Value = ValorClave,
        });
        contexto.UserTokens.Add(new IdentityUserToken<Guid>
        {
            UserId = cuenta.Id,
            LoginProvider = "[AspNetUserStore]",
            Name = "RecoveryCodes",
            Value = ValorCodigos,
        });
        return cuenta.Id;
    }

    /// <summary>
    /// La concesión se siembra como propietario (un AdminPlataforma la concede por
    /// <c>ConcederPrivilegioCommand</c>; su política la prueba
    /// <c>ConcesionPorAdminDePlataformaTests</c>). La sesión la abre el técnico por el
    /// camino real, bajo runtime.
    /// </summary>
    private async Task<Guid> AbrirSesionConCapacidadAsync(
        Guid? concedidaPor = null,
        CapacidadPrivilegio capacidad = CapacidadPrivilegio.RestablecimientoSegundoFactor,
        Guid[]? tenantsAlcanzados = null)
    {
        var ahora = DateTime.UtcNow;
        var concesion = ConcesionPrivilegio.SobreTenants(
            _tecnico, capacidad, tenantsAlcanzados ?? [_tenantA],
            ahora.AddMinutes(-10), ahora.AddDays(1), concedidaPorUsuarioId: concedidaPor ?? _adminPlataforma);
        await using (var contexto = CrearContexto(_tenantPlataforma, cadena: _cadenaConexion))
        {
            contexto.ConcesionesPrivilegio.Add(concesion);
            await contexto.SaveChangesAsync();
        }

        return await AbrirComoRuntimeAsync(concesion.Id, _tenantA);
    }

    private async Task<Guid> AbrirSesionDeSoporteLecturaAsync()
    {
        await using var contexto = CrearContexto(_tenantPlataforma);
        var ahora = DateTime.UtcNow;
        var concesion = ConcesionPrivilegio.SoporteLecturaGlobal(
            _tecnico, ahora.AddMinutes(-10), ahora.AddDays(30), concedidaPorUsuarioId: _tecnico);
        contexto.ConcesionesPrivilegio.Add(concesion);
        await contexto.SaveChangesAsync();
        return await AbrirComoRuntimeAsync(concesion.Id, _tenantA);
    }

    private async Task<Guid> AbrirComoRuntimeAsync(Guid concesionId, Guid tenantObjetivo)
    {
        await using var contexto = CrearContexto(_tenantPlataforma);
        var handler = new AbrirSesionPrivilegiadaCommandHandler(
            contexto, contexto, new PlataformaWriter(contexto), UsuarioTecnico(), contexto);

        var resultado = await handler.Handle(
            new AbrirSesionPrivilegiadaCommand(concesionId, tenantObjetivo, "El Administrador perdió el móvil", HorasDeVentana: 1),
            CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue(resultado.EsFallido ? resultado.Error.Codigo : "");
        return resultado.Valor;
    }

    private async Task<string> RestablecerComoSoporteAsync(Guid tenantDelContexto, Guid sesion, Guid usuario)
    {
        await using var contexto = CrearContexto(tenantDelContexto, sesionPrivilegiadaId: sesion);
        return await LlamarFuncionAsync(contexto, sesion, usuario);
    }

    // La misma llamada que SegundoFactorDeCuentasIdentity, para leer el código exacto.
    private static Task<string> LlamarFuncionAsync(CaeManagerDbContext contexto, Guid sesion, Guid usuario) =>
        contexto.Database
            .SqlQuery<string>($"SELECT app_restablecer_segundo_factor_por_soporte({sesion}, {usuario}) AS \"Value\"")
            .SingleAsync();

    // El camino por sesión no usa Identity: solo el contexto y la puerta.
    private static SegundoFactorDeCuentasIdentity Puerto(CaeManagerDbContext contexto) =>
        new(null!, null!, contexto, new PuertaAccesoDatos());

    private async Task CuentaIntactaAsync(Guid usuario)
    {
        (await ContarTokensAsync(usuario)).Should().Be(2, "la clave y los códigos siguen en su sitio");
        (await EscalarComoPropietarioAsync<long>(
            @"SELECT COUNT(*) FROM ""RegistrosAuditoria"" WHERE ""EntidadId"" = @u AND ""ViaAcceso"" = 'SesionPrivilegiada';",
            usuario)).Should().Be(0, "sin escritura no hay fila de auditoría");
    }

    private Task<long> ContarTokensAsync(Guid usuario) => EscalarComoPropietarioAsync<long>(
        @"SELECT COUNT(*) FROM ""AspNetUserTokens"" WHERE ""UserId"" = @u;", usuario);

    private async Task<T> EscalarComoPropietarioAsync<T>(string sql, Guid parametro)
    {
        await using var conexion = new NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();
        await using var comando = conexion.CreateCommand();
        comando.CommandText = sql;
        comando.Parameters.AddWithValue("u", parametro);
        return (T)(await comando.ExecuteScalarAsync())!;
    }

    private async Task EjecutarComoPropietarioAsync(string sql, Guid parametro)
    {
        await using var conexion = new NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();
        await using var comando = conexion.CreateCommand();
        comando.CommandText = sql;
        comando.Parameters.AddWithValue("u", parametro);
        (await comando.ExecuteNonQueryAsync()).Should().BeGreaterThan(0, "la preparación tiene que tocar alguna fila");
    }

    /// <summary>
    /// Ejecuta el alta como propietario dentro de una transacción que queda abierta
    /// hasta <see cref="AltaSinConfirmar.ConfirmarAsync"/>; al desecharla sin confirmar,
    /// se deshace.
    /// </summary>
    private async Task<AltaSinConfirmar> AltaSinConfirmarAsync(string sql, Guid usuario, Guid? rol = null)
    {
        var conexion = new NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();
        var transaccion = await conexion.BeginTransactionAsync();
        await using var comando = new NpgsqlCommand(sql, conexion, transaccion);
        comando.Parameters.AddWithValue("u", usuario);
        if (sql.Contains("@r")) comando.Parameters.AddWithValue("r", rol ?? _rolAdministrador);
        (await comando.ExecuteNonQueryAsync()).Should().Be(1, "la preparación tiene que tocar una fila");
        return new AltaSinConfirmar(conexion, transaccion);
    }

    private sealed class AltaSinConfirmar(NpgsqlConnection conexion, NpgsqlTransaction transaccion) : IAsyncDisposable
    {
        public Task ConfirmarAsync() => transaccion.CommitAsync();

        public async ValueTask DisposeAsync()
        {
            await transaccion.DisposeAsync();
            await conexion.DisposeAsync();
        }
    }

    /// <summary>
    /// Espera a observar en <c>pg_locks</c> una petición de cerrojo consultivo no
    /// concedida en esta base (única por clase de test). Si el restablecimiento termina
    /// antes, no esperó: el fallo lleva lo que devolvió.
    /// </summary>
    private async Task EsperarBloqueadoPorElCerrojoAsync(Task<string> restablecer)
    {
        await using var conexion = new NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();
        await using var comando = conexion.CreateCommand();
        comando.CommandText = @"
SELECT COUNT(*) FROM pg_locks
WHERE locktype = 'advisory' AND NOT granted
  AND database = (SELECT oid FROM pg_database WHERE datname = current_database());";

        var limite = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < limite)
        {
            if (restablecer.IsCompleted)
                throw new Xunit.Sdk.XunitException(
                    $"El restablecimiento no esperó al alta sin confirmar: devolvió «{await restablecer}».");
            if ((long)(await comando.ExecuteScalarAsync())! > 0)
                return;
            await Task.Delay(50);
        }

        throw new Xunit.Sdk.XunitException("En 30 s no se observó al restablecimiento esperando el cerrojo.");
    }

    /// <summary>
    /// Espera a ver <paramref name="esperadas"/> conexiones de esta base esperando un
    /// cerrojo pesado (consultivo, de tupla o de transacción). Si alguna de las tareas
    /// termina antes, no esperó, y el fallo lo dice.
    /// </summary>
    private async Task EsperarEsperasDeCerrojoAsync(int esperadas, params Task[] tareas)
    {
        await using var conexion = new NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();
        await using var comando = conexion.CreateCommand();
        comando.CommandText = @"
SELECT COUNT(*) FROM pg_stat_activity
WHERE datname = current_database() AND wait_event_type = 'Lock';";

        var limite = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < limite)
        {
            if (tareas.FirstOrDefault(t => t.IsCompleted) is { } terminada)
            {
                await terminada;
                throw new Xunit.Sdk.XunitException("Una de las transacciones terminó sin esperar al alta sin confirmar.");
            }
            if ((long)(await comando.ExecuteScalarAsync())! >= esperadas)
                return;
            await Task.Delay(50);
        }

        throw new Xunit.Sdk.XunitException($"En 30 s no se observaron {esperadas} esperas de cerrojo.");
    }

    private sealed record FilaAuditoria(
        Guid TenantId, string EntidadTipo, Guid EntidadId, string Accion, string? DatosAntes, string? DatosDespues,
        Guid? UsuarioId, Guid? ActorRealUsuarioId, string? TipoActor);

    private async Task<List<FilaAuditoria>> LeerAuditoriaDeSesionAsync(Guid sesion)
    {
        await using var conexion = new NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();
        await using var comando = conexion.CreateCommand();
        comando.CommandText = @"
SELECT ""TenantId"", ""EntidadTipo"", ""EntidadId"", ""Accion"", ""DatosAntes"", ""DatosDespues"",
       ""UsuarioId"", ""ActorRealUsuarioId"", ""TipoActor""
FROM ""RegistrosAuditoria""
WHERE ""ViaAcceso"" = 'SesionPrivilegiada' AND ""ViaAccesoId"" = @u;";
        comando.Parameters.AddWithValue("u", sesion);
        await using var lector = await comando.ExecuteReaderAsync();
        var filas = new List<FilaAuditoria>();
        while (await lector.ReadAsync())
            filas.Add(new FilaAuditoria(
                lector.GetGuid(0), lector.GetString(1), lector.GetGuid(2), lector.GetString(3),
                lector.IsDBNull(4) ? null : lector.GetString(4), lector.IsDBNull(5) ? null : lector.GetString(5),
                lector.IsDBNull(6) ? null : lector.GetGuid(6), lector.IsDBNull(7) ? null : lector.GetGuid(7),
                lector.IsDBNull(8) ? null : lector.GetString(8)));
        return filas;
    }

    private CurrentUserServiceFalso UsuarioTecnico(Guid? actor = null) =>
        new(actor ?? _tecnico, rol: null, tenantOrigenId: _tenantPlataforma, tieneDobleFactorActivo: true);

    /// <summary>
    /// Por defecto conecta como <c>cae_app_runtime</c>: la identidad de producción.
    /// Solo la siembra pasa la cadena del propietario.
    /// </summary>
    private CaeManagerDbContext CrearContexto(
        Guid tenantId, Guid? sesionPrivilegiadaId = null, string? cadena = null, Guid? actor = null)
    {
        var tenantActual = new TenantActualAmbiental { TenantId = tenantId };
        var seleccion = new ClienteActivoSeleccionadoFalso(
            sesionPrivilegiadaId is null ? null : tenantId, sesionPrivilegiadaId);
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(
                cadena ?? BaseDatosPostgresDePruebas.CadenaComoRuntime(_cadenaConexion),
                npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(
                new TenantSelladoInterceptor(tenantActual),
                new TenantRlsConnectionInterceptor(
                    tenantActual, seleccion, UsuarioTecnico(actor), BaseDatosPostgresDePruebas.FirmanteContextoRls))
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

    private sealed class ClienteActivoSeleccionadoFalso(Guid? tenantId, Guid? sesionPrivilegiadaId)
        : IClienteActivoSeleccionado
    {
        public Guid? TenantIdSeleccionado => tenantId;

        public Guid? AsignacionOperacionIdSeleccionada => null;

        public Guid? SesionPrivilegiadaIdSeleccionada => sesionPrivilegiadaId;
    }
}
