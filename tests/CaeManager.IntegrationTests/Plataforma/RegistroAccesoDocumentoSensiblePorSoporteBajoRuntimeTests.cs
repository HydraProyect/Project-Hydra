using CaeManager.Application.Auditoria;
using CaeManager.Application.Common;
using CaeManager.Application.Plataforma.Commands.AbrirSesionPrivilegiada;
using CaeManager.Domain.Auditoria;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Plataforma;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Interceptors;
using CaeManager.Infrastructure.Persistence.Repositories;
using CaeManager.Web.Services;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;
using PlataformaWriter = CaeManager.Infrastructure.Plataforma.PlataformaWriter;

namespace CaeManager.IntegrationTests.Plataforma;

/// <summary>
/// DEC-36 y REC-208 (revisión extensa 2026-10-23, Z2-2): el acceso de Soporte TALVEG a un
/// documento sensible desde una Sesión Privilegiada queda registrado, contra PostgreSQL real
/// y con la identidad de conexión de producción, <c>cae_app_runtime</c>, que con la sesión
/// adopta <c>cae_app_soporte</c>. El propietario solo siembra y lee lo que quedó escrito.
///
/// <para>
/// Lo que se ataca: la conexión de la sesión sigue sin <c>INSERT</c> y la RLS no se
/// ensancha. La única escritura es la función
/// <c>app_registrar_acceso_documento_sensible_por_soporte</c>, que vuelve a comprobar la
/// sesión contra el contexto RLS firmado; cada precondición tiene aquí un caso que la
/// incumple, no deja fila y hace que el servicio falle en cerrado.
/// </para>
/// </summary>
public class RegistroAccesoDocumentoSensiblePorSoporteBajoRuntimeTests : IAsyncLifetime
{
    private const string Funcion = "app_registrar_acceso_documento_sensible_por_soporte";

    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenantPlataforma = Guid.NewGuid();
    private readonly Guid _tenantA = Guid.NewGuid();
    private readonly Guid _tenantB = Guid.NewGuid();
    private readonly Guid _tecnico = Guid.NewGuid();
    private readonly Guid _otroTecnico = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        await BaseDatosPostgresDePruebas.MigrarAsync(_cadenaConexion);

        // Siembra como propietario: el rol de runtime no crea Tenants ni cuentas.
        await using var contexto = CrearContexto(_tenantPlataforma, cadena: _cadenaConexion);
        contexto.Tenants.Add(CrearTenant(_tenantPlataforma, "Plataforma", esPlataforma: true));
        contexto.Tenants.Add(CrearTenant(_tenantA, "Tenant A S.L."));
        contexto.Tenants.Add(CrearTenant(_tenantB, "Tenant B S.L."));
        SembrarCuenta(contexto, "tecnico", _tecnico);
        SembrarCuenta(contexto, "otro-tecnico", _otroTecnico);
        await contexto.SaveChangesAsync();
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    // ── Camino positivo ──────────────────────────────────────────────────────

    [Fact]
    public async Task En_una_Sesion_Privilegiada_vigente_el_acceso_queda_registrado_con_el_actor_y_la_sesion()
    {
        var sesion = await AbrirSesionDeSoporteLecturaAsync();
        var documentoId = Guid.NewGuid();

        // El servicio entero, como lo llama el endpoint: el Documento no se resuelve desde
        // la sesión (no existe), así que se registra con la categoría más protectora.
        await RegistrarComoSoporteAsync(_tenantA, sesion, documentoId);

        var fila = (await LeerFilasAsync(documentoId)).Should().ContainSingle().Subject;
        fila.TenantId.Should().Be(_tenantA, "el Tenant sale del contexto firmado de la sesión");
        fila.UsuarioId.Should().Be(_tecnico);
        fila.ActorRealUsuarioId.Should().Be(_tecnico, "el Actor real es el técnico de Soporte TALVEG");
        fila.ViaAcceso.Should().Be(nameof(TipoViaAccesoAuditoria.SesionPrivilegiada));
        fila.ViaAccesoId.Should().Be(sesion);
        fila.TipoActor.Should().Be(nameof(TipoActorAuditoria.Persona));
        fila.Sensibilidad.Should().Be("CategoriaEspecialSalud");
        fila.TipoAcceso.Should().Be(nameof(TipoAccesoDocumentoSensible.Apertura));
    }

    [Fact]
    public async Task Si_la_sesion_simula_a_un_usuario_el_registro_lo_nombra_y_conserva_al_actor_real()
    {
        var sesion = await AbrirSesionDeSoporteLecturaAsync();
        var simulado = Guid.NewGuid();
        await EjecutarComoPropietarioAsync(
            $@"UPDATE ""SesionesPrivilegiadas"" SET ""UsuarioSimuladoId"" = '{simulado}' WHERE ""Id"" = @u;", sesion);
        var documentoId = Guid.NewGuid();

        await RegistrarComoSoporteAsync(_tenantA, sesion, documentoId);

        var fila = (await LeerFilasAsync(documentoId)).Should().ContainSingle().Subject;
        fila.UsuarioId.Should().Be(simulado);
        fila.ActorRealUsuarioId.Should().Be(_tecnico, "en impersonación, Actor real y Usuario simulado van separados");
    }

    [Fact]
    public async Task Una_concesion_por_Tenant_que_alcanza_el_Tenant_tambien_registra()
    {
        var sesion = await AbrirSesionPorTenantAsync([_tenantA]);
        var documentoId = Guid.NewGuid();

        await RegistrarComoSoporteAsync(_tenantA, sesion, documentoId);

        (await LeerFilasAsync(documentoId)).Should().ContainSingle();
    }

    /// <summary>
    /// Las listas de valores de la función están escritas a mano en SQL: este caso las ata a
    /// los enums. Un valor nuevo de <see cref="TipoAccesoDocumentoSensible"/> o de
    /// <c>SensibilidadDocumental</c> que la función no conozca dejaría a Soporte TALVEG sin
    /// poder abrir nada (falla en cerrado) sin que ningún otro test lo viera.
    /// </summary>
    [Fact]
    public async Task La_funcion_acepta_cada_valor_sensible_de_los_enums()
    {
        var sesion = await AbrirSesionDeSoporteLecturaAsync();
        var sensibilidades = Enum.GetValues<Domain.Documentos.SensibilidadDocumental>()
            .Where(s => s != Domain.Documentos.SensibilidadDocumental.SinDatosPersonales)
            .ToList();
        sensibilidades.Should().NotBeEmpty();

        await using var contexto = CrearContexto(_tenantA, sesionPrivilegiadaId: sesion);
        foreach (var sensibilidad in sensibilidades)
        {
            foreach (var tipoAcceso in Enum.GetValues<TipoAccesoDocumentoSensible>())
            {
                (await LlamarFuncionAsync(contexto, sesion, Guid.NewGuid(), sensibilidad.ToString(), tipoAcceso.ToString()))
                    .Should().Be("registrado", $"{sensibilidad} × {tipoAcceso} es un acceso sensible que se registra");
            }
        }
    }

    // ── Cada precondición de la función, incumplida: no hay fila y no se sirve ─

    [Fact]
    public async Task Una_sesion_que_aun_no_empieza_no_registra()
    {
        var sesion = await AbrirSesionDeSoporteLecturaAsync();
        await EjecutarComoPropietarioAsync(
            @"UPDATE ""SesionesPrivilegiadas"" SET ""InicioEnUtc"" = now() + interval '5 minutes' WHERE ""Id"" = @u;", sesion);

        await NoRegistraYFallaEnCerradoAsync(_tenantA, sesion);
    }

    [Fact]
    public async Task Una_concesion_que_aun_no_es_vigente_no_registra()
    {
        var sesion = await AbrirSesionDeSoporteLecturaAsync();
        await EjecutarComoPropietarioAsync(@"
UPDATE ""ConcesionesPrivilegio"" SET ""VigenciaDesde"" = now() + interval '5 minutes'
WHERE ""Id"" = (SELECT ""ConcesionPrivilegioId"" FROM ""SesionesPrivilegiadas"" WHERE ""Id"" = @u);", sesion);

        await NoRegistraYFallaEnCerradoAsync(_tenantA, sesion);
    }

    [Fact]
    public async Task Una_sesion_cerrada_no_registra_y_el_servicio_falla_en_cerrado()
    {
        var sesion = await AbrirSesionDeSoporteLecturaAsync();
        await EjecutarComoPropietarioAsync(
            @"UPDATE ""SesionesPrivilegiadas"" SET ""CerradaEnUtc"" = now() WHERE ""Id"" = @u;", sesion);

        await NoRegistraYFallaEnCerradoAsync(_tenantA, sesion);
    }

    [Fact]
    public async Task Una_sesion_caducada_no_registra_y_el_servicio_falla_en_cerrado()
    {
        var sesion = await AbrirSesionDeSoporteLecturaAsync();
        await EjecutarComoPropietarioAsync(
            @"UPDATE ""SesionesPrivilegiadas"" SET ""ExpiraEnUtc"" = now() - interval '1 minute' WHERE ""Id"" = @u;", sesion);

        await NoRegistraYFallaEnCerradoAsync(_tenantA, sesion);
    }

    [Fact]
    public async Task Una_sesion_sobre_A_no_registra_con_el_contexto_de_B_aunque_la_concesion_alcance_los_dos()
    {
        var sesion = await AbrirSesionPorTenantAsync([_tenantA, _tenantB]);

        await NoRegistraYFallaEnCerradoAsync(_tenantB, sesion);
    }

    [Fact]
    public async Task Otra_persona_no_registra_con_la_sesion_del_tecnico_aunque_conozca_su_Id()
    {
        var sesion = await AbrirSesionDeSoporteLecturaAsync();

        await NoRegistraYFallaEnCerradoAsync(_tenantA, sesion, actor: _otroTecnico);
    }

    [Fact]
    public async Task Una_concesion_revocada_no_registra_aunque_la_sesion_siga_abierta()
    {
        var sesion = await AbrirSesionDeSoporteLecturaAsync();
        await EjecutarComoPropietarioAsync(@"
UPDATE ""ConcesionesPrivilegio"" SET ""Estado"" = 'Revocada'
WHERE ""Id"" = (SELECT ""ConcesionPrivilegioId"" FROM ""SesionesPrivilegiadas"" WHERE ""Id"" = @u);", sesion);

        await NoRegistraYFallaEnCerradoAsync(_tenantA, sesion);
    }

    [Fact]
    public async Task Una_concesion_caducada_no_registra_aunque_la_sesion_siga_abierta()
    {
        var sesion = await AbrirSesionDeSoporteLecturaAsync();
        await EjecutarComoPropietarioAsync(@"
UPDATE ""ConcesionesPrivilegio"" SET ""VigenciaHasta"" = now() - interval '1 minute'
WHERE ""Id"" = (SELECT ""ConcesionPrivilegioId"" FROM ""SesionesPrivilegiadas"" WHERE ""Id"" = @u);", sesion);

        await NoRegistraYFallaEnCerradoAsync(_tenantA, sesion);
    }

    [Fact]
    public async Task Una_concesion_por_Tenant_que_ya_no_alcanza_el_Tenant_no_registra()
    {
        var sesion = await AbrirSesionPorTenantAsync([_tenantA]);
        await EjecutarComoPropietarioAsync(@"
UPDATE ""TenantsAlcanzadosPorConcesion"" SET ""TenantId"" = gen_random_uuid()
WHERE ""ConcesionPrivilegioId"" = (SELECT ""ConcesionPrivilegioId"" FROM ""SesionesPrivilegiadas"" WHERE ""Id"" = @u);", sesion);

        await NoRegistraYFallaEnCerradoAsync(_tenantA, sesion);
    }

    [Fact]
    public async Task Un_documento_sin_datos_personales_no_se_registra_por_la_funcion()
    {
        // «Nunca registrar de más» (DEC-36) también en la base: el servicio ya no lo
        // pide, y la función no lo acepta si alguien lo pidiera.
        var sesion = await AbrirSesionDeSoporteLecturaAsync();
        var documentoId = Guid.NewGuid();

        await using (var contexto = CrearContexto(_tenantA, sesionPrivilegiadaId: sesion))
        {
            (await LlamarFuncionAsync(contexto, sesion, documentoId, "SinDatosPersonales", "Apertura"))
                .Should().Be("valor_no_valido");
            (await LlamarFuncionAsync(contexto, sesion, documentoId, "CategoriaEspecialSalud", "Descarga"))
                .Should().Be("valor_no_valido");
        }

        (await LeerFilasAsync(documentoId)).Should().BeEmpty();
    }

    // ── Quién puede ejecutarla ───────────────────────────────────────────────

    [Fact]
    public async Task Sin_Sesion_Privilegiada_el_rol_de_runtime_no_puede_ejecutarla()
    {
        var sesion = await AbrirSesionDeSoporteLecturaAsync();
        var documentoId = Guid.NewGuid();

        // Mismo técnico, mismo Tenant, pero sin la sesión en la conexión: el rol es
        // cae_app_runtime, que no tiene EXECUTE.
        await using (var contexto = CrearContexto(_tenantA))
        {
            var ejecutar = async () => await LlamarFuncionAsync(contexto, sesion, documentoId, "CategoriaEspecialSalud", "Apertura");
            (await ejecutar.Should().ThrowAsync<PostgresException>())
                .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
        }

        (await LeerFilasAsync(documentoId)).Should().BeEmpty();
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
            $"SELECT has_function_privilege(@rol, '{Funcion}(uuid, uuid, uuid, text, text)', 'EXECUTE');";
        comando.Parameters.AddWithValue("rol", rol);

        ((bool)(await comando.ExecuteScalarAsync())!).Should().Be(esperado);
    }

    [Fact]
    public async Task La_funcion_es_SECURITY_DEFINER_con_search_path_fijo_y_soporte_sigue_sin_INSERT()
    {
        await using var conexion = new NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();
        await using var comando = conexion.CreateCommand();
        comando.CommandText = $"""
            SELECT p.prosecdef,
                   array_to_string(p.proconfig, ';'),
                   has_table_privilege('cae_app_soporte', '"RegistrosAccesoDocumentoSensible"', 'INSERT'),
                   p.proacl IS NULL OR EXISTS (SELECT 1 FROM aclexplode(p.proacl) a WHERE a.grantee = 0)
              FROM pg_proc p
             WHERE p.proname = '{Funcion}' AND p.pronamespace = 'public'::regnamespace;
            """;
        await using var lector = await comando.ExecuteReaderAsync();
        (await lector.ReadAsync()).Should().BeTrue("la función existe tras migrar");

        lector.GetBoolean(0).Should().BeTrue("SECURITY DEFINER");
        lector.GetString(1).Should().Be("search_path=pg_catalog, pg_temp",
            "sin search_path fijo, una tabla temporal podría suplantar a las de la sesión");
        lector.GetBoolean(2).Should().BeFalse("la función es la única puerta: la conexión de soporte no gana INSERT");
        lector.GetBoolean(3).Should().BeFalse("PUBLIC no conserva EXECUTE");
    }

    [Fact]
    public async Task Un_contexto_firmado_invalidado_no_registra_y_el_servicio_falla_en_cerrado()
    {
        var sesion = await AbrirSesionDeSoporteLecturaAsync();
        var documentoId = Guid.NewGuid();
        await using var contexto = CrearContexto(_tenantA, sesionPrivilegiadaId: sesion);
        await contexto.Database.OpenConnectionAsync();

        // Control positivo: la misma conexión y la misma sesión registran antes de invalidar el contexto.
        (await LlamarFuncionAsync(contexto, sesion, Guid.NewGuid(), "CategoriaEspecialSalud", "Apertura"))
            .Should().Be("registrado");
        await contexto.Database.ExecuteSqlRawAsync("SELECT set_config('app.contexto', '', false);");

        (await LlamarFuncionAsync(contexto, sesion, documentoId, "CategoriaEspecialSalud", "Apertura"))
            .Should().Be("contexto_no_valido");

        var servicio = new RegistroAccesoDocumentoSensibleService(
            contexto, contexto,
            new ActorAuditoriaFalso(new ActorAuditoria(_tecnico, null, TipoViaAcceso.SesionPrivilegiada, sesion)),
            new RegistroAccesoDocumentoSensibleRepository(contexto, NullLogger<RegistroAccesoDocumentoSensibleRepository>.Instance));
        var registrar = () => servicio.RegistrarSiSensibleAsync(documentoId, TipoAccesoDocumentoSensible.Apertura);
        (await registrar.Should().ThrowAsync<AccesoDocumentoSensibleNoRegistradoException>())
            .Which.RecursoId.Should().Be(documentoId);
        (await LeerFilasAsync(documentoId)).Should().BeEmpty();
    }

    /// <summary>
    /// Un contexto firmado corrupto (o caducado) no devuelve un código: app_contexto_validado lanza
    /// 42501 en cuanto se evalúa, y la función de registro lo evalúa siempre. El repositorio lo
    /// traduce a false, y con eso el servicio falla en cerrado con la excepción que
    /// DocumentosEndpoints convierte en 403 sin servir el fichero.
    ///
    /// <para>
    /// Desde P6 «contraer» (ContraeContextoRlsFirmado) la política de Documentos también lee el
    /// contexto firmado, así que la consulta previa del servicio puede lanzar el 42501 antes de
    /// llegar al repositorio (lo observado en CI con PostgreSQL 18); antes la política leía la GUC
    /// suelta y no lo evaluaba. Las dos salidas fallan en cerrado y el endpoint traduce las dos a
    /// 403 (ErroresLecturaSensible.EsContextoNoAutorizado): el servicio se comprueba por esa
    /// propiedad, y la traducción del repositorio, aparte, con su llamada directa.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Un_contexto_firmado_corrupto_hace_lanzar_42501_a_la_funcion_y_el_servicio_falla_en_cerrado()
    {
        var sesion = await AbrirSesionDeSoporteLecturaAsync();
        var documentoId = Guid.NewGuid();
        await using var contexto = CrearContexto(_tenantA, sesionPrivilegiadaId: sesion);
        await contexto.Database.OpenConnectionAsync();

        // Control positivo: la misma conexión registra antes de corromper el contexto.
        (await LlamarFuncionAsync(contexto, sesion, Guid.NewGuid(), "CategoriaEspecialSalud", "Apertura"))
            .Should().Be("registrado");
        await contexto.Database.ExecuteSqlRawAsync("SELECT set_config('app.contexto', 'v1|corrupto', false);");

        var llamar = () => LlamarFuncionAsync(contexto, sesion, documentoId, "CategoriaEspecialSalud", "Apertura");
        (await llamar.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);

        var servicio = new RegistroAccesoDocumentoSensibleService(
            contexto, contexto,
            new ActorAuditoriaFalso(new ActorAuditoria(_tecnico, null, TipoViaAcceso.SesionPrivilegiada, sesion)),
            new RegistroAccesoDocumentoSensibleRepository(contexto, NullLogger<RegistroAccesoDocumentoSensibleRepository>.Instance));
        var registrar = () => servicio.RegistrarSiSensibleAsync(documentoId, TipoAccesoDocumentoSensible.Apertura);
        // Se exige la propiedad y no la salida concreta: que la consulta previa evalúe el
        // contexto sobre un Documento inexistente depende del plan (InitPlan como clave de
        // índice o como filtro), y cualquiera de las dos salidas falla en cerrado.
        var falloDelServicio = await Record.ExceptionAsync(registrar);
        falloDelServicio.Should().NotBeNull("con el contexto corrupto el servicio no puede terminar sin error");
        ErroresLecturaSensible.EsContextoNoAutorizado(falloDelServicio!).Should().BeTrue(
            $"el endpoint tiene que responder 403 sin servir el fichero, y llegó {falloDelServicio!.GetType().Name}: {falloDelServicio.Message}");
        if (falloDelServicio is AccesoDocumentoSensibleNoRegistradoException noRegistrado)
            noRegistrado.RecursoId.Should().Be(documentoId);
        else
            falloDelServicio.Should().BeOfType<PostgresException>()
                .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);

        var actor = new ActorAuditoria(_tecnico, null, TipoViaAcceso.SesionPrivilegiada, sesion);
        var registro = new RegistroAccesoDocumentoSensible(
            documentoId, SensibilidadDocumental.CategoriaEspecialSalud, TipoAccesoDocumentoSensible.Apertura,
            actor.UsuarioSimuladoId ?? actor.ActorRealUsuarioId, actor.ActorRealUsuarioId,
            (TipoViaAccesoAuditoria)actor.Via, actor.ViaAccesoId, (TipoActorAuditoria)actor.ResolverTipoActor());
        (await new RegistroAccesoDocumentoSensibleRepository(contexto, NullLogger<RegistroAccesoDocumentoSensibleRepository>.Instance)
                .GuardarAsync(registro))
            .Should().BeFalse("el repositorio traduce el 42501 de la función a false, que el servicio convierte en el fallo en cerrado");

        (await LeerFilasAsync(documentoId)).Should().BeEmpty();
    }

    // ── Andamiaje ────────────────────────────────────────────────────────────

    private async Task NoRegistraYFallaEnCerradoAsync(Guid tenantDelContexto, Guid sesion, Guid? actor = null)
    {
        var documentoId = Guid.NewGuid();

        await using (var contexto = CrearContexto(tenantDelContexto, sesionPrivilegiadaId: sesion, actor: actor))
        {
            (await LlamarFuncionAsync(contexto, sesion, documentoId, "CategoriaEspecialSalud", "Apertura"))
                .Should().Be("sesion_no_autorizada");
        }

        var registrar = () => RegistrarComoSoporteAsync(tenantDelContexto, sesion, documentoId, actor);
        (await registrar.Should().ThrowAsync<AccesoDocumentoSensibleNoRegistradoException>())
            .Which.RecursoId.Should().Be(documentoId);

        (await LeerFilasAsync(documentoId)).Should().BeEmpty("sin sesión válida la función no escribe");
    }

    private async Task RegistrarComoSoporteAsync(Guid tenantDelContexto, Guid sesion, Guid documentoId, Guid? actor = null)
    {
        await using var contexto = CrearContexto(tenantDelContexto, sesionPrivilegiadaId: sesion, actor: actor);
        var servicio = new RegistroAccesoDocumentoSensibleService(
            contexto, contexto,
            new ActorAuditoriaFalso(new ActorAuditoria(actor ?? _tecnico, null, TipoViaAcceso.SesionPrivilegiada, sesion)),
            new RegistroAccesoDocumentoSensibleRepository(contexto, NullLogger<RegistroAccesoDocumentoSensibleRepository>.Instance));

        await servicio.RegistrarSiSensibleAsync(documentoId, TipoAccesoDocumentoSensible.Apertura);
    }

    private static Task<string> LlamarFuncionAsync(
        CaeManagerDbContext contexto, Guid sesion, Guid documentoId, string sensibilidad, string tipoAcceso) =>
        contexto.Database
            .SqlQuery<string>(
                $"SELECT app_registrar_acceso_documento_sensible_por_soporte({sesion}, {Guid.NewGuid()}, {documentoId}, {sensibilidad}, {tipoAcceso}) AS \"Value\"")
            .SingleAsync();

    private void SembrarCuenta(CaeManagerDbContext contexto, string nombre, Guid id)
    {
        // El técnico de Soporte TALVEG es una cuenta del Tenant de plataforma.
        var correo = $"{nombre}@registro-acceso-soporte.test";
        contexto.Users.Add(new ApplicationUser
        {
            Id = id,
            UserName = correo,
            NormalizedUserName = correo.ToUpperInvariant(),
            Email = correo,
            NormalizedEmail = correo.ToUpperInvariant(),
            NombreCompleto = nombre,
            TenantId = _tenantPlataforma,
            SecurityStamp = Guid.NewGuid().ToString("N").ToUpperInvariant(),
        });
    }

    private async Task<Guid> AbrirSesionDeSoporteLecturaAsync()
    {
        var ahora = DateTime.UtcNow;
        var concesion = ConcesionPrivilegio.SoporteLecturaGlobal(
            _tecnico, ahora.AddMinutes(-10), ahora.AddDays(30), concedidaPorUsuarioId: _tecnico);

        // Sembrada como propietario: bajo runtime la auto-concesión exige AdminPlataforma vigente
        // (privilegio_autoconcesion), y aquí solo hace falta la sesión de SoporteLectura.
        await using (var contexto = CrearContexto(_tenantPlataforma, cadena: _cadenaConexion))
        {
            contexto.ConcesionesPrivilegio.Add(concesion);
            await contexto.SaveChangesAsync();
        }

        return await AbrirComoRuntimeAsync(concesion.Id, _tenantA);
    }

    /// <summary>
    /// Concesión por Tenant, sembrada como propietario (la política de quién concede la
    /// prueba <c>ConcesionPorAdminDePlataformaTests</c>). Aprovisionamiento es una capacidad
    /// por Tenant: el registro no filtra por capacidad, solo por sesión y alcance.
    /// </summary>
    private async Task<Guid> AbrirSesionPorTenantAsync(Guid[] tenantsAlcanzados)
    {
        var ahora = DateTime.UtcNow;
        var concesion = ConcesionPrivilegio.SobreTenants(
            _tecnico, CapacidadPrivilegio.Aprovisionamiento, tenantsAlcanzados,
            ahora.AddMinutes(-10), ahora.AddDays(1), concedidaPorUsuarioId: _otroTecnico);
        await using (var contexto = CrearContexto(_tenantPlataforma, cadena: _cadenaConexion))
        {
            contexto.ConcesionesPrivilegio.Add(concesion);
            await contexto.SaveChangesAsync();
        }

        return await AbrirComoRuntimeAsync(concesion.Id, _tenantA);
    }

    private async Task<Guid> AbrirComoRuntimeAsync(Guid concesionId, Guid tenantObjetivo)
    {
        await using var contexto = CrearContexto(_tenantPlataforma);
        var handler = new AbrirSesionPrivilegiadaCommandHandler(
            contexto, contexto, new PlataformaWriter(contexto), UsuarioTecnico(), contexto);

        var resultado = await handler.Handle(
            new AbrirSesionPrivilegiadaCommand(concesionId, tenantObjetivo, "Incidencia con un documento", HorasDeVentana: 1),
            CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue(resultado.EsFallido ? resultado.Error.Codigo : "");
        return resultado.Valor;
    }

    private sealed record FilaRegistro(
        Guid TenantId, Guid? UsuarioId, Guid? ActorRealUsuarioId, string ViaAcceso, Guid? ViaAccesoId,
        string TipoActor, string Sensibilidad, string TipoAcceso);

    private async Task<List<FilaRegistro>> LeerFilasAsync(Guid documentoId)
    {
        await using var conexion = new NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();
        await using var comando = conexion.CreateCommand();
        comando.CommandText = """
            SELECT "TenantId", "UsuarioId", "ActorRealUsuarioId", "ViaAcceso", "ViaAccesoId",
                   "TipoActor", "Sensibilidad", "TipoAcceso"
              FROM "RegistrosAccesoDocumentoSensible"
             WHERE "DocumentoId" = @d;
            """;
        comando.Parameters.AddWithValue("d", documentoId);
        await using var lector = await comando.ExecuteReaderAsync();
        var filas = new List<FilaRegistro>();
        while (await lector.ReadAsync())
        {
            filas.Add(new FilaRegistro(
                lector.GetGuid(0),
                lector.IsDBNull(1) ? null : lector.GetGuid(1),
                lector.IsDBNull(2) ? null : lector.GetGuid(2),
                lector.GetString(3),
                lector.IsDBNull(4) ? null : lector.GetGuid(4),
                lector.GetString(5),
                lector.GetString(6),
                lector.GetString(7)));
        }

        return filas;
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
