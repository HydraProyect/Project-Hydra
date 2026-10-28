using CaeManager.Application.Common;
using CaeManager.Application.Plataforma;
using CaeManager.Application.Seguridad.Incidentes;
using CaeManager.Application.Seguridad.Incidentes.Aviso;
using CaeManager.Application.Seguridad.Incidentes.Aviso.Commands.ConfirmarYEnviarPrimerAviso;
using CaeManager.Domain.Common;
using CaeManager.Domain.Plataforma;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Interceptors;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Xunit;
using ContactosSeguridadPlataforma = CaeManager.Infrastructure.Plataforma.ContactosSeguridadPlataforma;
using IncidentesSeguridadPlataforma = CaeManager.Infrastructure.Plataforma.IncidentesSeguridadPlataforma;

namespace CaeManager.IntegrationTests.Plataforma;

/// <summary>
/// Aviso de violaciones de seguridad, incremento 3b, contra PostgreSQL real y con la identidad de conexión de
/// producción, <c>cae_app_runtime</c>. Ejecuta de verdad las funciones SECURITY DEFINER de la migración
/// <c>AnadeEnviosAvisoIncidenteSeguridad</c>: <c>app_decidir_incidente_seguridad</c>,
/// <c>app_reclamar_envio_aviso_seguridad</c> y <c>app_resolver_envio_aviso_seguridad</c>, que empiezan por
/// <c>app_actor_plataforma_aviso_seguridad</c>. El propietario solo siembra y lee lo que quedó escrito.
///
/// <para>
/// Lo que se ataca: sin la concesión AdminPlataforma global vigente, o dentro de otro Tenant, no se decide ni se
/// envía; un destinatario que la base no resuelve para ese Tenant afectado (otro correo, otro Tenant, otra marca
/// de contacto) se rechaza; reclamar y resolver son idempotentes (lo enviado no se repite y un reintento
/// reenvía el texto de la primera vez); y la tabla de envíos no admite escritura directa. El correo es siempre
/// un doble. Las reglas de Application están en <c>AvisoIncidenteSeguridadTests</c>.
/// </para>
/// </summary>
public class AvisoIncidenteSeguridadBajoRuntimeTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenantPlataforma = Guid.NewGuid();
    private readonly Guid _tenantA = Guid.NewGuid();
    private readonly Guid _tenantB = Guid.NewGuid();
    private readonly Guid _tenantAjeno = Guid.NewGuid();
    private readonly Guid _adminPlataforma = Guid.NewGuid();
    private readonly Guid _actorSinConcesion = Guid.NewGuid();
    private readonly Guid _administradorDeA = Guid.NewGuid();
    private readonly Guid _administradorDeB = Guid.NewGuid();
    private readonly Guid _administradorAjeno = Guid.NewGuid();

    private const string CorreoAdminA = "admin-a@aviso-seguridad.test";
    private const string CorreoAdminB = "admin-b@aviso-seguridad.test";
    private const string CorreoAdminAjeno = "admin-ajeno@aviso-seguridad.test";

    public async Task InitializeAsync()
    {
        await BaseDatosPostgresDePruebas.MigrarAsync(_cadenaConexion);

        await using var contexto = CrearContexto(_tenantPlataforma, cadena: _cadenaConexion);
        contexto.Tenants.Add(CrearTenant(_tenantPlataforma, "Plataforma", esPlataforma: true));
        contexto.Tenants.Add(CrearTenant(_tenantA, "Tenant beneficiario A S.L."));
        contexto.Tenants.Add(CrearTenant(_tenantB, "Tenant beneficiario B S.L."));
        contexto.Tenants.Add(CrearTenant(_tenantAjeno, "Tenant no afectado S.L."));
        var roles = await contexto.Roles.ToDictionaryAsync(r => r.Name!, r => r.Id);
        SembrarCuenta(contexto, "admin-plataforma", _tenantPlataforma, _adminPlataforma, null);
        SembrarCuenta(contexto, "sin-concesion", _tenantPlataforma, _actorSinConcesion, null);
        SembrarCuenta(contexto, "admin-a", _tenantA, _administradorDeA, roles[Roles.Administrador]);
        SembrarCuenta(contexto, "admin-b", _tenantB, _administradorDeB, roles[Roles.Administrador]);
        SembrarCuenta(contexto, "admin-ajeno", _tenantAjeno, _administradorAjeno, roles[Roles.Administrador]);
        contexto.ConcesionesPrivilegio.Add(ConcesionPrivilegio.Global(
            _adminPlataforma, vigenciaDesde: DateTime.UtcNow.AddMinutes(-10), vigenciaHasta: null));
        await contexto.SaveChangesAsync();
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    // ── Autoridad ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Sin_concesion_global_no_se_decide_ni_se_reclama_un_envio()
    {
        var incidente = await SembrarIncidenteAsync();
        await using var contexto = CrearContexto(_tenantPlataforma, actor: _actorSinConcesion);
        var plataforma = Adaptador(contexto);

        (await plataforma.DecidirAsync(incidente, DecisionIncidenteSeguridad.Confirmar, null, CancellationToken.None))
            .Error.Should().Be(ErroresIncidenteSeguridad.SoloActorDePlataforma);
        (await plataforma.DecidirAsync(incidente, DecisionIncidenteSeguridad.Descartar, "Falsa alarma", CancellationToken.None))
            .Error.Should().Be(ErroresIncidenteSeguridad.SoloActorDePlataforma);
        (await plataforma.ObtenerAsync(incidente, CancellationToken.None)).Should().BeNull(
            "sin la concesión la política actor_de_plataforma_lee no abre el incidente");

        (await EstadoIncidenteAsync(incidente)).Should().Be("Sospechado");
        (await ContarAsync(@"SELECT count(*) FROM ""TransicionesIncidenteSeguridad"" WHERE ""IncidenteId"" = @u", incidente)).Should().Be(0);

        await ConfirmarComoAdminPlataformaAsync(incidente);
        (await plataforma.ReclamarEnvioAsync(Solicitud(incidente, _tenantA, CorreoAdminA, sinContacto: true), CancellationToken.None))
            .Error.Should().Be(ErroresIncidenteSeguridad.SoloActorDePlataforma);
        (await ContarEnviosAsync(incidente)).Should().Be(0);
    }

    [Fact]
    public async Task El_Administrador_de_un_Tenant_afectado_no_decide_desde_su_Tenant()
    {
        var incidente = await SembrarIncidenteAsync();
        await using var contexto = CrearContexto(_tenantA, actor: _administradorDeA, tenantOrigen: _tenantA);

        (await LlamarDecidirAsync(contexto, incidente, "Confirmar", null)).Should().Be("no_autorizado");
        (await EstadoIncidenteAsync(incidente)).Should().Be("Sospechado");
    }

    [Fact]
    public async Task Con_concesion_global_pero_dentro_de_otro_Tenant_no_se_decide()
    {
        var incidente = await SembrarIncidenteAsync();
        await using var contexto = CrearContexto(_tenantA, actor: _adminPlataforma, tenantOrigen: _tenantPlataforma);

        // Según firme el interceptor ese contexto, la base se para en la firma (42501, que el adaptador
        // traduce) o en la comprobación de que el actor trabaja desde su propio Tenant de plataforma.
        (await Adaptador(contexto).DecidirAsync(incidente, DecisionIncidenteSeguridad.Confirmar, null, CancellationToken.None))
            .Error.Should().Be(ErroresIncidenteSeguridad.SoloActorDePlataforma);
        (await EstadoIncidenteAsync(incidente)).Should().Be("Sospechado");
    }

    // ── Decidir ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Confirmar_fija_la_hora_una_sola_vez_y_deja_la_transicion_con_el_actor()
    {
        var incidente = await SembrarIncidenteAsync();
        await using var contexto = CrearContexto(_tenantPlataforma, actor: _adminPlataforma);

        (await LlamarDecidirAsync(contexto, incidente, "Confirmar", null)).Should().Be("confirmado");
        var primeraHora = await ConfirmadoEnAsync(incidente);
        (await LlamarDecidirAsync(contexto, incidente, "Confirmar", null)).Should().Be("ya_confirmado");
        (await LlamarDecidirAsync(contexto, incidente, "Descartar", "Ya no")).Should().Be("estado_no_valido",
            "un incidente confirmado no se descarta");

        primeraHora.Should().NotBeNull();
        (await ConfirmadoEnAsync(incidente)).Should().Be(primeraHora, "el reintento no mueve el inicio del plazo");
        (await EstadoIncidenteAsync(incidente)).Should().Be("Confirmado");
        (await ContarAsync(@"SELECT count(*) FROM ""TransicionesIncidenteSeguridad"" WHERE ""IncidenteId"" = @u AND ""ActorUsuarioId"" IS NOT NULL", incidente))
            .Should().Be(1);
    }

    [Fact]
    public async Task Descartar_exige_motivo_y_un_Descartado_no_se_confirma()
    {
        var incidente = await SembrarIncidenteAsync();
        await using var contexto = CrearContexto(_tenantPlataforma, actor: _adminPlataforma);

        (await LlamarDecidirAsync(contexto, incidente, "Descartar", "   ")).Should().Be("motivo_no_valido");
        (await LlamarDecidirAsync(contexto, incidente, "Evaluar", null)).Should().Be("en_evaluacion");
        (await LlamarDecidirAsync(contexto, incidente, "Descartar", "Falsa alarma")).Should().Be("descartado");
        (await LlamarDecidirAsync(contexto, incidente, "Confirmar", null)).Should().Be("estado_no_valido");

        (await EstadoIncidenteAsync(incidente)).Should().Be("Descartado");
        (await ConfirmadoEnAsync(incidente)).Should().BeNull();
    }

    // ── Reclamar y resolver ───────────────────────────────────────────────────

    [Fact]
    public async Task Sin_confirmar_no_se_reclama_ningun_envio()
    {
        var incidente = await SembrarIncidenteAsync();
        await using var contexto = CrearContexto(_tenantPlataforma, actor: _adminPlataforma);

        (await Adaptador(contexto).ReclamarEnvioAsync(Solicitud(incidente, _tenantA, CorreoAdminA, sinContacto: true), CancellationToken.None))
            .Error.Should().Be(ErroresIncidenteSeguridad.NoConfirmado);
        (await ContarEnviosAsync(incidente)).Should().Be(0);
    }

    [Theory]
    [InlineData("intruso@ejemplo.test", "A", true)]
    [InlineData(CorreoAdminB, "A", true)]
    [InlineData(CorreoAdminAjeno, "Ajeno", true)]
    [InlineData(CorreoAdminA, "A", false)]
    public async Task Un_destinatario_que_la_base_no_resuelve_para_ese_Tenant_afectado_se_rechaza(
        string correo, string tenant, bool sinContacto)
    {
        var incidente = await SembrarIncidenteAsync();
        await ConfirmarComoAdminPlataformaAsync(incidente);
        await using var contexto = CrearContexto(_tenantPlataforma, actor: _adminPlataforma);
        var tenantId = tenant == "A" ? _tenantA : _tenantAjeno;

        var resultado = await Adaptador(contexto).ReclamarEnvioAsync(
            Solicitud(incidente, tenantId, correo, sinContacto), CancellationToken.None);

        resultado.Error.Should().Be(ErroresIncidenteSeguridad.DestinatarioNoValido,
            "solo un Tenant afectado y un destinatario que app_destinatarios_aviso_seguridad devuelve para él, con su marca");
        (await ContarEnviosAsync(incidente)).Should().Be(0);
    }

    [Fact]
    public async Task Reclamar_y_resolver_son_idempotentes_y_el_envio_deja_auditoria_en_el_Tenant_sin_el_correo()
    {
        var incidente = await SembrarIncidenteAsync();
        await ConfirmarComoAdminPlataformaAsync(incidente);
        await using var contexto = CrearContexto(_tenantPlataforma, actor: _adminPlataforma);
        var plataforma = Adaptador(contexto);

        var primero = (await plataforma.ReclamarEnvioAsync(
            Solicitud(incidente, _tenantA, "  Admin-A@Aviso-Seguridad.test ", sinContacto: true), CancellationToken.None)).Valor;
        var enCurso = (await plataforma.ReclamarEnvioAsync(
            Solicitud(incidente, _tenantA, CorreoAdminA, sinContacto: true), CancellationToken.None)).Valor;

        primero.Reclamado.Should().BeTrue();
        enCurso.EnvioId.Should().Be(primero.EnvioId, "el correo se guarda en minúsculas y sin espacios: es la misma fila");
        enCurso.Reclamado.Should().BeFalse("otro clic la está enviando");
        enCurso.Estado.Should().Be(EstadoEnvioAvisoIncidenteSeguridad.Enviando);

        (await plataforma.ResolverEnvioAsync(primero.EnvioId, enviado: true, null, CancellationToken.None)).EsExitoso.Should().BeTrue();
        (await plataforma.ResolverEnvioAsync(primero.EnvioId, enviado: false, "tarde", CancellationToken.None))
            .Error.Should().Be(ErroresIncidenteSeguridad.EstadoNoValido, "un envío ya Enviado no se vuelve a resolver");
        var yaEnviado = (await plataforma.ReclamarEnvioAsync(
            Solicitud(incidente, _tenantA, CorreoAdminA, sinContacto: true), CancellationToken.None)).Valor;

        yaEnviado.Reclamado.Should().BeFalse();
        yaEnviado.Estado.Should().Be(EstadoEnvioAvisoIncidenteSeguridad.Enviado);
        (await ContarEnviosAsync(incidente)).Should().Be(1);
        var auditoria = await TextoAsync(@"
SELECT string_agg(""DatosDespues"", '|') FROM ""RegistrosAuditoria""
WHERE ""EntidadTipo"" = 'EnvioAvisoIncidenteSeguridad' AND ""EntidadId"" = @u", primero.EnvioId);
        auditoria.Should().Contain("PrimerAviso").And.NotContain("aviso-seguridad.test",
            "la auditoría del envío no copia el correo del destinatario");
        (await ContarAsync(@"SELECT count(*) FROM ""RegistrosAuditoria"" WHERE ""EntidadId"" = @u AND ""TenantId"" = '" + _tenantA + "'", primero.EnvioId))
            .Should().Be(1, "la auditoría va al Tenant afectado");
    }

    [Fact]
    public async Task Un_envio_fallido_se_reclama_otra_vez_con_el_texto_de_la_primera_vez()
    {
        var incidente = await SembrarIncidenteAsync();
        await ConfirmarComoAdminPlataformaAsync(incidente);
        await using var contexto = CrearContexto(_tenantPlataforma, actor: _adminPlataforma);
        var plataforma = Adaptador(contexto);

        var primero = (await plataforma.ReclamarEnvioAsync(
            Solicitud(incidente, _tenantB, CorreoAdminB, sinContacto: true, cuerpo: "<p>Primera versión</p>"), CancellationToken.None)).Valor;
        (await plataforma.ResolverEnvioAsync(primero.EnvioId, enviado: false, "Correo.Fallo", CancellationToken.None)).EsExitoso.Should().BeTrue();
        var reintento = (await plataforma.ReclamarEnvioAsync(
            Solicitud(incidente, _tenantB, CorreoAdminB, sinContacto: true, cuerpo: "<p>Versión editada</p>"), CancellationToken.None)).Valor;

        reintento.EnvioId.Should().Be(primero.EnvioId);
        reintento.Reclamado.Should().BeTrue();
        reintento.Cuerpo.Should().Be("<p>Primera versión</p>");
        (await ContarAsync(@"SELECT ""Intentos""::bigint FROM ""EnviosAvisoIncidenteSeguridad"" WHERE ""Id"" = @u", primero.EnvioId)).Should().Be(2);
    }

    [Fact]
    public async Task Solo_quien_reclamo_el_envio_lo_resuelve()
    {
        var incidente = await SembrarIncidenteAsync();
        await ConfirmarComoAdminPlataformaAsync(incidente);
        Guid envio;
        await using (var contexto = CrearContexto(_tenantPlataforma, actor: _adminPlataforma))
            envio = (await Adaptador(contexto).ReclamarEnvioAsync(
                Solicitud(incidente, _tenantA, CorreoAdminA, sinContacto: true), CancellationToken.None)).Valor.EnvioId;

        await using var sinConcesion = CrearContexto(_tenantPlataforma, actor: _actorSinConcesion);
        (await Adaptador(sinConcesion).ResolverEnvioAsync(envio, enviado: true, null, CancellationToken.None))
            .Error.Should().Be(ErroresIncidenteSeguridad.SoloActorDePlataforma);
        (await TextoAsync(@"SELECT ""Estado"" FROM ""EnviosAvisoIncidenteSeguridad"" WHERE ""Id"" = @u", envio)).Should().Be("Enviando");
    }

    // ── Escritura directa ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("""INSERT INTO "EnviosAvisoIncidenteSeguridad" ("Id", "IncidenteId", "Plantilla", "TenantAfectadoId", "DestinatarioNombre", "DestinatarioCorreo", "SinContactoValidado", "Asunto", "Cuerpo", "ConfirmadoPorUsuarioId", "ConfirmadoEnUtc", "Estado", "Intentos", "UltimoIntentoEnUtc", "UltimoIntentoPorUsuarioId") VALUES (gen_random_uuid(), gen_random_uuid(), 'PrimerAviso', gen_random_uuid(), 'x', 'x@ejemplo.test', true, 'a', 'c', gen_random_uuid(), now(), 'Enviado', 1, now(), gen_random_uuid())""")]
    [InlineData("""UPDATE "EnviosAvisoIncidenteSeguridad" SET "Estado" = 'Enviado', "EnviadoEnUtc" = now()""")]
    [InlineData("""DELETE FROM "EnviosAvisoIncidenteSeguridad" """)]
    [InlineData("""UPDATE "IncidentesSeguridadSospechados" SET "Estado" = 'Confirmado', "ConfirmadoEnUtc" = now()""")]
    public async Task La_tabla_de_envios_y_el_incidente_no_admiten_escritura_directa_ni_con_concesion(string sql)
    {
        await SembrarIncidenteAsync();
        await using var contexto = CrearContexto(_tenantPlataforma, actor: _adminPlataforma);

        var escribir = () => contexto.Database.ExecuteSqlRawAsync(sql);

        (await escribir.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege,
                "cae_app_runtime solo tiene SELECT: las funciones SECURITY DEFINER son la única puerta");
    }

    // ── De punta a punta ──────────────────────────────────────────────────────

    [Fact]
    public async Task El_clic_de_confirmar_y_enviar_avisa_a_los_Administradores_de_cada_Tenant_afectado_y_el_reintento_no_repite()
    {
        var incidente = await SembrarIncidenteAsync();
        var correo = new CorreoEspia();

        await using (var contexto = CrearContexto(_tenantPlataforma, actor: _adminPlataforma))
        {
            var resultado = await Handler(contexto, correo)
                .Handle(new ConfirmarYEnviarPrimerAvisoCommand(incidente, Contenido()), CancellationToken.None);

            resultado.EsExitoso.Should().BeTrue(resultado.EsFallido ? resultado.Error.Codigo : "");
            resultado.Valor.Enviados.Should().Be(2);
        }

        await using (var contexto = CrearContexto(_tenantPlataforma, actor: _adminPlataforma))
        {
            var reintento = await Handler(contexto, correo)
                .Handle(new ConfirmarYEnviarPrimerAvisoCommand(incidente, Contenido()), CancellationToken.None);

            reintento.Valor.Enviados.Should().Be(0);
            reintento.Valor.YaEnviados.Should().Be(2);
        }

        correo.Destinatarios.Should().BeEquivalentTo([CorreoAdminA, CorreoAdminB]);
        correo.Destinatarios.Should().NotContain(CorreoAdminAjeno);
        (await EstadoIncidenteAsync(incidente)).Should().Be("Confirmado");
        (await ContarAsync(@"SELECT count(*) FROM ""EnviosAvisoIncidenteSeguridad"" WHERE ""IncidenteId"" = @u AND ""Estado"" = 'Enviado' AND ""SinContactoValidado""", incidente))
            .Should().Be(2);
    }

    // ── Preparación y lectura ─────────────────────────────────────────────────

    private static IncidentesSeguridadPlataforma Adaptador(CaeManagerDbContext contexto) => new(contexto, new PuertaAccesoDatos());

    private ConfirmarYEnviarPrimerAvisoCommandHandler Handler(CaeManagerDbContext contexto, CorreoEspia correo) =>
        new(new CurrentUserServiceFalso(_adminPlataforma, tenantOrigenId: _tenantPlataforma, tieneDobleFactorActivo: true),
            new SinSesionPrivilegiada(), new ConcesionGlobalDeclarada(), Adaptador(contexto),
            new ContactosSeguridadPlataforma(contexto, new PuertaAccesoDatos()), correo,
            Options.Create(new OpcionesIncidentesSeguridad()), NullLogger<ConfirmarYEnviarPrimerAvisoCommandHandler>.Instance);

    private static ContenidoAvisoIncidente Contenido() =>
        new("Acceso no autorizado sospechado.", "En evaluación.", "", "Sesiones cerradas.", "Responsable de seguridad de TALVEG.");

    private static EnvioAvisoSolicitado Solicitud(
        Guid incidente, Guid tenant, string correo, bool sinContacto, string cuerpo = "<p>Primer aviso</p>") =>
        new(incidente, PlantillaAvisoIncidenteSeguridad.PrimerAviso, tenant, "Destinatario", correo, sinContacto,
            "Primer aviso de violación de seguridad", cuerpo);

    private async Task ConfirmarComoAdminPlataformaAsync(Guid incidente)
    {
        await using var contexto = CrearContexto(_tenantPlataforma, actor: _adminPlataforma);
        (await LlamarDecidirAsync(contexto, incidente, "Confirmar", null)).Should().Be("confirmado");
    }

    // La misma llamada que IncidentesSeguridadPlataforma.DecidirAsync, para leer el código exacto.
    private static Task<string> LlamarDecidirAsync(CaeManagerDbContext contexto, Guid incidente, string decision, string? motivo)
    {
        var texto = motivo ?? string.Empty;
        return contexto.Database
            .SqlQuery<string>($"SELECT app_decidir_incidente_seguridad({incidente}, {decision}, {texto}) AS \"Value\"")
            .SingleAsync();
    }

    /// <summary>Siembra como propietario un incidente Sospechado que afecta a los Tenants A y B.</summary>
    private async Task<Guid> SembrarIncidenteAsync()
    {
        var id = Guid.NewGuid();
        await using var conexion = new NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();
        await using var comando = conexion.CreateCommand();
        comando.CommandText = """
            INSERT INTO "IncidentesSeguridadSospechados"
              ("Id", "Senal", "SujetoClave", "DetectadoEnUtc", "UltimaDeteccionEnUtc", "Detecciones", "TenantsAfectados", "Evidencia", "Estado")
            VALUES (@id, 'InicioSesionNoReconocido', @sujeto, now(), now(), 1, @tenants, '{}'::jsonb, 'Sospechado')
            """;
        comando.Parameters.AddWithValue("id", id);
        comando.Parameters.AddWithValue("sujeto", $"usuario:{Guid.NewGuid():D}");
        comando.Parameters.AddWithValue("tenants", new[] { _tenantA, _tenantB });
        await comando.ExecuteNonQueryAsync();
        return id;
    }

    private Task<string?> EstadoIncidenteAsync(Guid incidente) =>
        TextoAsync(@"SELECT ""Estado"" FROM ""IncidentesSeguridadSospechados"" WHERE ""Id"" = @u", incidente);

    private async Task<DateTime?> ConfirmadoEnAsync(Guid incidente)
    {
        var valor = await EscalarAsync(@"SELECT ""ConfirmadoEnUtc"" FROM ""IncidentesSeguridadSospechados"" WHERE ""Id"" = @u", incidente);
        return valor is DBNull or null ? null : (DateTime)valor;
    }

    private Task<long> ContarEnviosAsync(Guid incidente) =>
        ContarAsync(@"SELECT count(*) FROM ""EnviosAvisoIncidenteSeguridad"" WHERE ""IncidenteId"" = @u", incidente);

    private async Task<long> ContarAsync(string sql, Guid parametro) => (long)(await EscalarAsync(sql, parametro))!;

    private async Task<string?> TextoAsync(string sql, Guid parametro) =>
        await EscalarAsync(sql, parametro) is string texto ? texto : null;

    private async Task<object?> EscalarAsync(string sql, Guid parametro)
    {
        await using var conexion = new NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();
        await using var comando = conexion.CreateCommand();
        comando.CommandText = sql;
        comando.Parameters.AddWithValue("u", parametro);
        return await comando.ExecuteScalarAsync();
    }

    private static void SembrarCuenta(CaeManagerDbContext contexto, string nombre, Guid tenant, Guid id, Guid? rolId)
    {
        var correo = $"{nombre}@aviso-seguridad.test";
        contexto.Users.Add(new ApplicationUser
        {
            Id = id,
            UserName = correo,
            NormalizedUserName = correo.ToUpperInvariant(),
            Email = correo,
            NormalizedEmail = correo.ToUpperInvariant(),
            NombreCompleto = nombre,
            TenantId = tenant,
            TwoFactorEnabled = true,
            SecurityStamp = Guid.NewGuid().ToString("N").ToUpperInvariant(),
        });
        if (rolId is { } rol)
            contexto.UserRoles.Add(new IdentityUserRole<Guid> { UserId = id, RoleId = rol });
    }

    /// <summary>
    /// Por defecto conecta como <c>cae_app_runtime</c>: la identidad de producción. Solo la siembra pasa la
    /// cadena del propietario.
    /// </summary>
    private CaeManagerDbContext CrearContexto(Guid tenantId, string? cadena = null, Guid? actor = null, Guid? tenantOrigen = null)
    {
        var tenantActual = new TenantActualAmbiental { TenantId = tenantId };
        var usuario = new CurrentUserServiceFalso(
            actor ?? _adminPlataforma, rol: null, tenantOrigenId: tenantOrigen ?? _tenantPlataforma, tieneDobleFactorActivo: true);
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(
                cadena ?? BaseDatosPostgresDePruebas.CadenaComoRuntime(_cadenaConexion),
                npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(
                new TenantSelladoInterceptor(tenantActual),
                new TenantRlsConnectionInterceptor(
                    tenantActual, new SinSeleccion(), usuario, BaseDatosPostgresDePruebas.FirmanteContextoRls))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }

    private static Tenant CrearTenant(Guid id, string nombre, bool esPlataforma = false)
    {
        var tenant = new Tenant(nombre);
        typeof(Entity).GetProperty(nameof(Entity.Id))!.SetValue(tenant, id);
        if (esPlataforma) tenant.MarcarComoPlataforma();
        return tenant;
    }

    private sealed class SinSeleccion : IClienteActivoSeleccionado
    {
        public Guid? TenantIdSeleccionado => null;
        public Guid? AsignacionOperacionIdSeleccionada => null;
        public Guid? SesionPrivilegiadaIdSeleccionada => null;
    }

    private sealed class SinSesionPrivilegiada : ISesionPrivilegiadaActual
    {
        public Task<SesionPrivilegiadaActiva?> ObtenerAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<SesionPrivilegiadaActiva?>(null);

        public Task<SesionPrivilegiadaActiva?> RevalidarAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<SesionPrivilegiadaActiva?>(null);
    }

    /// <summary>La barrera de Application se da por superada: lo que se prueba aquí es que la base la repite.</summary>
    private sealed class ConcesionGlobalDeclarada : IAutorizacionAdminPlataforma
    {
        public Task<bool> PuedeSobreTenantAsync(Guid usuarioId, Guid tenantObjetivoId, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<bool> PuedeGlobalmenteAsync(Guid usuarioId, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
    }

    /// <summary>Doble del correo: nunca sale nada; solo apunta a quién iría.</summary>
    private sealed class CorreoEspia : IEmailService
    {
        public List<string> Destinatarios { get; } = [];

        public Task<Result> EnviarAsync(string destinatarioEmail, string asunto, string cuerpoHtml, TipoAvisoCorreo tipo,
            string? responderA = null, CancellationToken cancellationToken = default)
        {
            Destinatarios.Add(destinatarioEmail);
            return Task.FromResult(Result.Exito());
        }
    }
}
