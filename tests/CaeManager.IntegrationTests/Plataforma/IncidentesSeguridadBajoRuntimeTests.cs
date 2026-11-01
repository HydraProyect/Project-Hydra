using CaeManager.Application.Seguridad.Incidentes;
using CaeManager.Domain.Plataforma;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Seguridad.Incidentes;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace CaeManager.IntegrationTests.Plataforma;

/// <summary>
/// Incidentes de seguridad sospechados (plano de Plataforma), contra el rol restringido
/// <c>cae_app_runtime</c> con <c>SET ROLE</c>: como superusuario las políticas no se
/// evalúan. Se afirma que cualquier usuario puede disparar el registro por la función,
/// que no puede leer ni escribir la tabla por su cuenta, que solo un AdminPlataforma
/// global la lee, que la función deduplica, y que sin contexto firmado rechaza y con
/// contexto firmado sin usuario deja constancia de la detección sin Actor.
/// </summary>
public class IncidentesSeguridadBajoRuntimeTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenantA = Guid.NewGuid();
    private readonly Guid _tenantB = Guid.NewGuid();
    private readonly Guid _usuarioDelTenant = Guid.NewGuid();
    private readonly Guid _adminPlataformaGlobal = Guid.NewGuid();
    private readonly Guid _adminPlataformaPorTenant = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto();
        await contexto.Database.MigrateAsync();

        var ahora = DateTime.UtcNow;
        contexto.ConcesionesPrivilegio.AddRange(
            ConcesionPrivilegio.Global(_adminPlataformaGlobal, ahora.AddMinutes(-10), ahora.AddHours(4)),
            ConcesionPrivilegio.SobreTenants(_adminPlataformaPorTenant, CapacidadPrivilegio.AdminPlataforma, [_tenantA],
                vigenciaDesde: ahora.AddMinutes(-10), vigenciaHasta: ahora.AddHours(4)));
        await contexto.SaveChangesAsync();
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task Un_usuario_cualquiera_registra_por_la_funcion_pero_no_ve_el_incidente()
    {
        await using var conexion = await AbrirRestringidaComoAsync(_usuarioDelTenant, _tenantA);

        var (id, nuevo) = await RegistrarAsync(conexion, "InicioSesionNoReconocido", $"usuario:{_usuarioDelTenant:D}", [_tenantA]);

        nuevo.Should().BeTrue();
        (await ContarAsync(conexion, "IncidentesSeguridadSospechados")).Should().Be(0,
            "quien dispara la señal no lee los incidentes: el plano de Plataforma no es de su Tenant");
        (await ContarAsync(conexion, "TransicionesIncidenteSeguridad")).Should().Be(0);

        await using var superusuario = await AbrirAsync();
        await using var consulta = superusuario.CreateCommand();
        consulta.CommandText = """
            SELECT i."Estado", i."Detecciones", i."TenantsAfectados", i."ConfirmadoEnUtc" IS NULL,
                   t."EstadoAnterior" IS NULL, t."EstadoNuevo", t."ActorUsuarioId" IS NULL
            FROM "IncidentesSeguridadSospechados" i JOIN "TransicionesIncidenteSeguridad" t ON t."IncidenteId" = i."Id"
            WHERE i."Id" = @id
            """;
        consulta.Parameters.AddWithValue("id", id);
        await using var lector = await consulta.ExecuteReaderAsync();
        (await lector.ReadAsync()).Should().BeTrue();
        lector.GetString(0).Should().Be("Sospechado");
        lector.GetInt32(1).Should().Be(1);
        lector.GetFieldValue<Guid[]>(2).Should().Equal(_tenantA);
        lector.GetBoolean(3).Should().BeTrue("la hora de confirmación nace nula: el plazo no corre hasta confirmar");
        lector.GetBoolean(4).Should().BeTrue();
        lector.GetString(5).Should().Be("Sospechado");
        lector.GetBoolean(6).Should().BeTrue("la primera transición la hace el sistema");
        (await lector.ReadAsync()).Should().BeFalse("una sola transición al nacer");
    }

    [Fact]
    public async Task La_misma_senal_sobre_el_mismo_sujeto_en_la_ventana_suma_una_deteccion_y_une_los_Tenants()
    {
        await using var conexion = await AbrirRestringidaComoAsync(_usuarioDelTenant, _tenantA);
        var sujeto = $"usuario:{_usuarioDelTenant:D}";

        var (primero, _) = await RegistrarAsync(conexion, "AccesoRepetidoAOtroTenant", sujeto, [_tenantA]);
        var (segundo, nuevo) = await RegistrarAsync(conexion, "AccesoRepetidoAOtroTenant", sujeto, [_tenantB, _tenantA]);
        var (otraSenal, otraNueva) = await RegistrarAsync(conexion, "ExtraccionMasiva", sujeto, [_tenantA]);
        var (otroSujeto, otroNuevo) = await RegistrarAsync(conexion, "AccesoRepetidoAOtroTenant", $"usuario:{Guid.NewGuid():D}", [_tenantA]);

        segundo.Should().Be(primero);
        nuevo.Should().BeFalse();
        otraNueva.Should().BeTrue();
        otraSenal.Should().NotBe(primero);
        otroNuevo.Should().BeTrue();
        otroSujeto.Should().NotBe(primero);

        await using var superusuario = await AbrirAsync();
        await using var consulta = superusuario.CreateCommand();
        consulta.CommandText = """SELECT "Detecciones", "TenantsAfectados" FROM "IncidentesSeguridadSospechados" WHERE "Id" = @id""";
        consulta.Parameters.AddWithValue("id", primero);
        await using var lector = await consulta.ExecuteReaderAsync();
        await lector.ReadAsync();
        lector.GetInt32(0).Should().Be(2);
        lector.GetFieldValue<Guid[]>(1).Should().BeEquivalentTo([_tenantA, _tenantB]);
    }

    [Fact]
    public async Task Un_incidente_descartado_no_absorbe_la_deteccion_siguiente()
    {
        await using var conexion = await AbrirRestringidaComoAsync(_usuarioDelTenant, _tenantA);
        var sujeto = $"usuario:{_usuarioDelTenant:D}";
        var (primero, _) = await RegistrarAsync(conexion, "ExtraccionMasiva", sujeto, [_tenantA]);

        await using (var superusuario = await AbrirAsync())
        await using (var descartar = superusuario.CreateCommand())
        {
            descartar.CommandText = """UPDATE "IncidentesSeguridadSospechados" SET "Estado" = 'Descartado' WHERE "Id" = @id""";
            descartar.Parameters.AddWithValue("id", primero);
            await descartar.ExecuteNonQueryAsync();
        }

        var (segundo, nuevo) = await RegistrarAsync(conexion, "ExtraccionMasiva", sujeto, [_tenantA]);
        nuevo.Should().BeTrue();
        segundo.Should().NotBe(primero);
    }

    [Fact]
    public async Task Solo_un_AdminPlataforma_global_lee_los_incidentes()
    {
        await using (var disparo = await AbrirRestringidaComoAsync(_usuarioDelTenant, _tenantA))
            await RegistrarAsync(disparo, "InicioSesionNoReconocido", $"usuario:{_usuarioDelTenant:D}", [_tenantA]);

        await using var global = await AbrirRestringidaComoAsync(_adminPlataformaGlobal, tenantId: null);
        (await ContarAsync(global, "IncidentesSeguridadSospechados")).Should().Be(1);
        (await ContarAsync(global, "TransicionesIncidenteSeguridad")).Should().Be(1);

        await using var porTenant = await AbrirRestringidaComoAsync(_adminPlataformaPorTenant, tenantId: null);
        (await ContarAsync(porTenant, "IncidentesSeguridadSospechados")).Should().Be(0,
            "un incidente cruza Tenants: una concesión acotada a un Tenant no lo abre");

        await using var sinUsuario = await AbrirRestringidaComoAsync(usuarioId: null, tenantId: null);
        (await ContarAsync(sinUsuario, "IncidentesSeguridadSospechados")).Should().Be(0);
    }

    [Theory]
    [InlineData("""INSERT INTO "IncidentesSeguridadSospechados" ("Id", "Senal", "SujetoClave", "DetectadoEnUtc", "UltimaDeteccionEnUtc", "Detecciones", "TenantsAfectados", "Evidencia", "Estado") VALUES (gen_random_uuid(), 'ExtraccionMasiva', 'x', now(), now(), 1, '{}', '{}', 'Sospechado')""")]
    [InlineData("""UPDATE "IncidentesSeguridadSospechados" SET "Estado" = 'Descartado'""")]
    [InlineData("""DELETE FROM "TransicionesIncidenteSeguridad" """)]
    public async Task Ni_siquiera_el_AdminPlataforma_global_escribe_las_tablas_directamente(string sql)
    {
        await using var conexion = await AbrirRestringidaComoAsync(_adminPlataformaGlobal, tenantId: null);
        await using var comando = conexion.CreateCommand();
        comando.CommandText = sql;

        var accion = () => comando.ExecuteNonQueryAsync();
        (await accion.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact]
    public async Task Las_dos_tablas_tienen_RLS_forzado_y_solo_la_politica_de_lectura()
    {
        await using var conexion = await AbrirAsync();
        await using var comando = conexion.CreateCommand();
        comando.CommandText = """
            SELECT c.relname, c.relrowsecurity, c.relforcerowsecurity,
                   (SELECT string_agg(p.polname::text || ':' || p.polcmd::text, ',' ORDER BY p.polname) FROM pg_policy p WHERE p.polrelid = c.oid)
            FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'public' AND c.relname IN ('IncidentesSeguridadSospechados', 'TransicionesIncidenteSeguridad')
            """;
        var filas = new List<(string Tabla, bool Rls, bool Force, string? Politicas)>();
        await using (var lector = await comando.ExecuteReaderAsync())
        {
            while (await lector.ReadAsync())
                filas.Add((lector.GetString(0), lector.GetBoolean(1), lector.GetBoolean(2), lector.IsDBNull(3) ? null : lector.GetString(3)));
        }

        filas.Should().HaveCount(2);
        filas.Should().OnlyContain(f => f.Rls && f.Force && f.Politicas == "actor_de_plataforma_lee:r");
    }

    [Fact]
    public async Task Fuera_de_la_ventana_de_deduplicacion_la_misma_senal_abre_otro_incidente()
    {
        await using var conexion = await AbrirRestringidaComoAsync(_usuarioDelTenant, _tenantA);
        var sujeto = $"usuario:{_usuarioDelTenant:D}";
        var (primero, _) = await RegistrarAsync(conexion, "ExtraccionMasiva", sujeto, [_tenantA]);

        await EjecutarComoSuperusuarioAsync(
            """UPDATE "IncidentesSeguridadSospechados" SET "DetectadoEnUtc" = now() - interval '61 minutes' WHERE "Id" = @id""", primero);

        var (segundo, nuevo) = await RegistrarAsync(conexion, "ExtraccionMasiva", sujeto, [_tenantA]);
        nuevo.Should().BeTrue("el incidente abierto nació hace más de los 60 minutos de la ventana");
        segundo.Should().NotBe(primero);
    }

    [Fact]
    public async Task Registros_concurrentes_de_la_misma_senal_y_sujeto_abren_un_solo_incidente()
    {
        const int Concurrentes = 8;
        var sujeto = $"usuario:{_usuarioDelTenant:D}";
        var conexiones = new List<NpgsqlConnection>();
        for (var i = 0; i < Concurrentes; i++)
            conexiones.Add(await AbrirRestringidaComoAsync(_usuarioDelTenant, _tenantA));

        try
        {
            var resultados = await Task.WhenAll(conexiones.Select(c => RegistrarAsync(c, "ExtraccionMasiva", sujeto, [_tenantA])));

            resultados.Count(r => r.Nuevo).Should().Be(1, "el cerrojo por señal y sujeto serializa la deduplicación");
            resultados.Select(r => r.Id).Distinct().Should().ContainSingle();
            (await DeteccionesAsync(resultados[0].Id)).Should().Be(Concurrentes);
        }
        finally
        {
            foreach (var c in conexiones)
                await c.DisposeAsync();
        }
    }

    [Fact]
    public async Task El_adaptador_real_registra_por_la_funcion_bajo_cae_app_runtime_y_deduplica()
    {
        await using var conexion = await AbrirRestringidaComoAsync(_usuarioDelTenant, _tenantA);
        var servicios = new ServiceCollection();
        servicios.AddScoped(_ => new CaeManagerDbContext(
            new DbContextOptionsBuilder<CaeManagerDbContext>().UseNpgsql(conexion).Options,
            new EphemeralDataProtectionProvider(), new TenantActualAmbiental { TenantId = _tenantA }));
        await using var proveedor = servicios.BuildServiceProvider();
        var adaptador = new RegistroIncidentesSeguridadEnBase(proveedor.GetRequiredService<IServiceScopeFactory>());
        var sujeto = $"usuario:{_usuarioDelTenant:D}";
        var evidencia = new EvidenciaIncidenteSeguridad().Con("usuario_id", _usuarioDelTenant).Con("filas", 5_000L);

        var primero = await adaptador.RegistrarAsync(SenalIncidenteSeguridad.ExtraccionMasiva, sujeto, [_tenantA], evidencia, TimeSpan.FromHours(1));
        var segundo = await adaptador.RegistrarAsync(SenalIncidenteSeguridad.ExtraccionMasiva, sujeto, [_tenantB], evidencia, TimeSpan.FromHours(1));

        primero.EsNuevo.Should().BeTrue();
        segundo.Should().Be(new IncidenteRegistrado(primero.IncidenteId, false));
        await using var superusuario = await AbrirAsync();
        await using var consulta = superusuario.CreateCommand();
        consulta.CommandText = """SELECT "Senal", "TenantsAfectados", "Evidencia"->>'filas' FROM "IncidentesSeguridadSospechados" WHERE "Id" = @id""";
        consulta.Parameters.AddWithValue("id", primero.IncidenteId);
        await using var lector = await consulta.ExecuteReaderAsync();
        (await lector.ReadAsync()).Should().BeTrue();
        lector.GetString(0).Should().Be("ExtraccionMasiva");
        lector.GetFieldValue<Guid[]>(1).Should().BeEquivalentTo([_tenantA, _tenantB]);
        lector.GetString(2).Should().Be("5000");
    }

    [Theory]
    [InlineData("cae_app_soporte", "IncidentesSeguridadSospechados")]
    [InlineData("cae_app_soporte", "TransicionesIncidenteSeguridad")]
    [InlineData("cae_app_aprovisionamiento", "IncidentesSeguridadSospechados")]
    [InlineData("cae_app_aprovisionamiento", "TransicionesIncidenteSeguridad")]
    public async Task Los_roles_de_Sesion_Privilegiada_registran_por_la_funcion_pero_no_leen_las_tablas(string rol, string tabla)
    {
        await using var conexion = await AbrirRestringidaComoAsync(_adminPlataformaGlobal, _tenantA, rol);

        var (_, nuevo) = await RegistrarAsync(conexion, "ExtraccionMasiva", $"usuario:{_adminPlataformaGlobal:D}", [_tenantA]);
        nuevo.Should().BeTrue("una señal nacida en una Sesión Privilegiada tiene que quedar registrada");

        var leer = () => ContarAsync(conexion, tabla);
        (await leer.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact]
    public async Task Un_incidente_plantado_sobre_otro_sujeto_no_absorbe_su_senal_real()
    {
        var victima = Guid.NewGuid();
        var sujeto = $"usuario:{victima:D}";
        await using (var otro = await AbrirRestringidaComoAsync(_usuarioDelTenant, _tenantA))
            await RegistrarAsync(otro, "ExtraccionMasiva", sujeto, [_tenantA]);

        await using var propia = await AbrirRestringidaComoAsync(victima, _tenantA);
        var (_, nuevo) = await RegistrarAsync(propia, "ExtraccionMasiva", sujeto, [_tenantA]);

        nuevo.Should().BeTrue("la señal real abre su incidente y su alerta, no se agrupa en el marcado");
    }

    [Fact]
    public async Task Una_concesion_AdminPlataforma_global_caducada_no_lee_los_incidentes()
    {
        var caducado = Guid.NewGuid();
        var ahora = DateTime.UtcNow;
        await using (var contexto = CrearContexto())
        {
            contexto.ConcesionesPrivilegio.Add(ConcesionPrivilegio.Global(caducado, ahora.AddHours(-5), ahora.AddHours(-1)));
            await contexto.SaveChangesAsync();
        }
        await using (var disparo = await AbrirRestringidaComoAsync(_usuarioDelTenant, _tenantA))
            await RegistrarAsync(disparo, "InicioSesionNoReconocido", $"usuario:{_usuarioDelTenant:D}", [_tenantA]);

        await using var conexion = await AbrirRestringidaComoAsync(caducado, tenantId: null);
        (await ContarAsync(conexion, "IncidentesSeguridadSospechados")).Should().Be(0, "la concesión ya no está vigente");
    }

    [Fact]
    public async Task La_funcion_rechaza_mas_de_cincuenta_Tenants_y_una_evidencia_de_mas_de_8_KB()
    {
        await using var conexion = await AbrirRestringidaComoAsync(_usuarioDelTenant, _tenantA);
        var sujeto = $"usuario:{_usuarioDelTenant:D}";

        var muchosTenants = () => RegistrarAsync(conexion, "ExtraccionMasiva", sujeto, Enumerable.Range(0, 51).Select(_ => Guid.NewGuid()).ToArray());
        (await muchosTenants.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.InvalidParameterValue);

        var evidenciaGrande = () => RegistrarAsync(conexion, "ExtraccionMasiva", sujeto, [_tenantA],
            $$"""{"relleno": "{{new string('x', 9_000)}}"}""");
        (await evidenciaGrande.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.StringDataRightTruncation);
    }

    [Fact]
    public async Task Un_sujeto_distinto_del_usuario_del_contexto_queda_marcado_en_la_evidencia()
    {
        await using var conexion = await AbrirRestringidaComoAsync(_usuarioDelTenant, _tenantA);

        var (propio, _) = await RegistrarAsync(conexion, "ExtraccionMasiva", $"usuario:{_usuarioDelTenant:D}", [_tenantA]);
        var otro = Guid.NewGuid();
        var (ajeno, nuevo) = await RegistrarAsync(conexion, "ExtraccionMasiva", $"usuario:{otro:D}", [_tenantA]);

        nuevo.Should().BeTrue("no se rechaza: una Sesión Privilegiada registra al Actor real con el Usuario simulado en el contexto");
        (await EvidenciaAsync(propio, "sujeto_distinto_del_contexto")).Should().BeNull();
        (await EvidenciaAsync(propio, "contexto_usuario_id")).Should().Be(_usuarioDelTenant.ToString("D"));
        (await EvidenciaAsync(ajeno, "sujeto_distinto_del_contexto")).Should().Be("true");
        (await EvidenciaAsync(ajeno, "contexto_usuario_id")).Should().Be(_usuarioDelTenant.ToString("D"));
    }

    [Fact]
    public async Task Sin_contexto_firmado_la_funcion_rechaza_el_registro_y_no_escribe()
    {
        await using var conexion = await AbrirAsync();
        await using (var sinFirma = conexion.CreateCommand())
        {
            sinFirma.CommandText = "SELECT set_config('app.contexto', '', false); SET ROLE cae_app_runtime;";
            await sinFirma.ExecuteNonQueryAsync();
        }

        var registrar = () => RegistrarAsync(conexion, "ExtraccionMasiva", $"usuario:{_usuarioDelTenant:D}", [_tenantA]);

        (await registrar.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
        await using var superusuario = await AbrirAsync();
        (await ContarAsync(superusuario, "IncidentesSeguridadSospechados")).Should().Be(0);
    }

    [Fact]
    public async Task Con_contexto_firmado_sin_usuario_queda_registrado_sin_Actor()
    {
        await using var conexion = await AbrirRestringidaComoAsync(usuarioId: null, _tenantA);

        var (id, nuevo) = await RegistrarAsync(conexion, "InicioSesionNoReconocido", $"usuario:{_usuarioDelTenant:D}", [_tenantA]);

        nuevo.Should().BeTrue("«No he sido yo» llega por un enlace del correo, sin sesión: la detección no se pierde");
        (await EvidenciaAsync(id, "sin_usuario_en_contexto")).Should().Be("true");
        (await EvidenciaAsync(id, "sujeto_distinto_del_contexto")).Should().BeNull();
        (await EvidenciaAsync(id, "contexto_usuario_id")).Should().BeNull("sin usuario en el contexto no se inventa un Actor");

        await using var superusuario = await AbrirAsync();
        await using var consulta = superusuario.CreateCommand();
        consulta.CommandText = """SELECT "ActorUsuarioId" IS NULL, "Motivo" FROM "TransicionesIncidenteSeguridad" WHERE "IncidenteId" = @id""";
        consulta.Parameters.AddWithValue("id", id);
        await using var lector = await consulta.ExecuteReaderAsync();
        (await lector.ReadAsync()).Should().BeTrue();
        lector.GetBoolean(0).Should().BeTrue();
        lector.GetString(1).Should().Be("Detectado por la señal InicioSesionNoReconocido sin usuario en el contexto");
    }

    [Fact]
    public async Task Sin_usuario_cada_registro_abre_su_incidente_y_no_revela_si_ya_habia_otro()
    {
        var sujeto = $"usuario:{_usuarioDelTenant:D}";
        await using var propia = await AbrirRestringidaComoAsync(_usuarioDelTenant, _tenantA);
        var (real, _) = await RegistrarAsync(propia, "ExtraccionMasiva", sujeto, [_tenantA]);

        await using var sinUsuario = await AbrirRestringidaComoAsync(usuarioId: null, tenantId: null);
        var (primero, primeroNuevo) = await RegistrarAsync(sinUsuario, "ExtraccionMasiva", sujeto, [_tenantB]);
        var (segundo, segundoNuevo) = await RegistrarAsync(sinUsuario, "ExtraccionMasiva", sujeto, [_tenantB]);

        primeroNuevo.Should().BeTrue("si agrupara con el incidente real, es_nuevo diría que existe");
        segundoNuevo.Should().BeTrue();
        new[] { real, primero, segundo }.Should().OnlyHaveUniqueItems();
        (await DeteccionesAsync(real)).Should().Be(1);
    }

    [Fact]
    public async Task Un_incidente_sin_usuario_no_absorbe_la_senal_real_del_sujeto()
    {
        var sujeto = $"usuario:{_usuarioDelTenant:D}";
        await using (var sinUsuario = await AbrirRestringidaComoAsync(usuarioId: null, tenantId: null))
            await RegistrarAsync(sinUsuario, "ExtraccionMasiva", sujeto, [_tenantA]);

        await using var propia = await AbrirRestringidaComoAsync(_usuarioDelTenant, _tenantA);
        var (_, nuevo) = await RegistrarAsync(propia, "ExtraccionMasiva", sujeto, [_tenantA]);

        nuevo.Should().BeTrue("la alerta depende de es_nuevo: agrupar aquí suprimiría el aviso de la señal real");
    }

    [Fact]
    public async Task Un_incidente_anterior_sin_marcas_no_absorbe_la_senal_real_y_no_gana_usuario()
    {
        var sujeto = $"usuario:{_usuarioDelTenant:D}";
        await using var sinUsuario = await AbrirRestringidaComoAsync(usuarioId: null, tenantId: null);
        var (heredado, _) = await RegistrarAsync(sinUsuario, "ExtraccionMasiva", sujeto, [_tenantA]);
        await EjecutarComoSuperusuarioAsync(
            """UPDATE "IncidentesSeguridadSospechados" SET "Evidencia" = "Evidencia" - 'sin_usuario_en_contexto' WHERE "Id" = @id""", heredado);

        await using var propia = await AbrirRestringidaComoAsync(_usuarioDelTenant, _tenantA);
        var (real, nuevo) = await RegistrarAsync(propia, "ExtraccionMasiva", sujeto, [_tenantA]);
        var (repetido, repetidoNuevo) = await RegistrarAsync(propia, "ExtraccionMasiva", sujeto, [_tenantA]);

        nuevo.Should().BeTrue("antes del relevo un registro sin usuario nacía sin marca: es de procedencia desconocida y no puede suprimir la alerta");
        real.Should().NotBe(heredado);
        repetidoNuevo.Should().BeFalse("la señal siguiente del mismo usuario agrupa con el incidente nuevo");
        repetido.Should().Be(real);
        (await DeteccionesAsync(heredado)).Should().Be(1);
        (await EvidenciaAsync(heredado, "contexto_usuario_id")).Should().BeNull("la procedencia desconocida no se rellena con un usuario deducido");
    }

    [Fact]
    public async Task Un_incidente_anterior_del_propio_usuario_tampoco_agrupa_porque_no_se_distingue()
    {
        var sujeto = $"usuario:{_usuarioDelTenant:D}";
        await using var propia = await AbrirRestringidaComoAsync(_usuarioDelTenant, _tenantA);
        var (heredado, _) = await RegistrarAsync(propia, "ExtraccionMasiva", sujeto, [_tenantA]);
        await EjecutarComoSuperusuarioAsync(
            """UPDATE "IncidentesSeguridadSospechados" SET "Evidencia" = "Evidencia" - 'contexto_usuario_id' WHERE "Id" = @id""", heredado);

        var (siguiente, nuevo) = await RegistrarAsync(propia, "ExtraccionMasiva", sujeto, [_tenantA]);

        nuevo.Should().BeTrue("sin la marca es igual que uno plantado sin usuario: el coste es un incidente de más, no una alerta perdida");
        siguiente.Should().NotBe(heredado);
    }

    [Fact]
    public async Task Quien_llama_no_fija_las_marcas_que_pone_la_funcion()
    {
        var sujeto = $"usuario:{_usuarioDelTenant:D}";
        var otroUsuario = Guid.NewGuid();
        var forjada = $$"""{"recuento": 1, "sujeto_distinto_del_contexto": true, "contexto_usuario_id": "{{otroUsuario:D}}", "sin_usuario_en_contexto": true, "tenants_afectados_truncados": true}""";

        await using var propia = await AbrirRestringidaComoAsync(_usuarioDelTenant, _tenantA);
        var (conUsuario, _) = await RegistrarAsync(propia, "ExtraccionMasiva", sujeto, [_tenantA], forjada);

        await using var sinUsuario = await AbrirRestringidaComoAsync(usuarioId: null, tenantId: null);
        var (delSinUsuario, _) = await RegistrarAsync(sinUsuario, "InicioSesionNoReconocido", sujeto, [_tenantA], forjada);

        (await EvidenciaAsync(conUsuario, "recuento")).Should().Be("1", "el resto de la evidencia se conserva");
        (await EvidenciaAsync(conUsuario, "sujeto_distinto_del_contexto")).Should().BeNull();
        (await EvidenciaAsync(conUsuario, "contexto_usuario_id")).Should().Be(_usuarioDelTenant.ToString("D"), "manda el contexto, no la evidencia que llega");
        (await EvidenciaAsync(conUsuario, "sin_usuario_en_contexto")).Should().BeNull();
        (await EvidenciaAsync(conUsuario, "tenants_afectados_truncados")).Should().BeNull();
        (await EvidenciaAsync(delSinUsuario, "contexto_usuario_id")).Should().BeNull("la evidencia no puede nombrar un Actor que el contexto no tiene");
        (await EvidenciaAsync(delSinUsuario, "sujeto_distinto_del_contexto")).Should().BeNull();
        (await EvidenciaAsync(delSinUsuario, "sin_usuario_en_contexto")).Should().Be("true");
    }

    [Fact]
    public async Task Un_registro_marcado_solo_agrupa_con_los_del_mismo_usuario_del_contexto()
    {
        var sujeto = $"usuario:{Guid.NewGuid():D}";
        await using var plantador = await AbrirRestringidaComoAsync(_usuarioDelTenant, _tenantA);
        var (plantado, _) = await RegistrarAsync(plantador, "ExtraccionMasiva", sujeto, [_tenantA]);
        var (repetido, repetidoNuevo) = await RegistrarAsync(plantador, "ExtraccionMasiva", sujeto, [_tenantA]);

        await using var otro = await AbrirRestringidaComoAsync(Guid.NewGuid(), _tenantA);
        var (ajeno, ajenoNuevo) = await RegistrarAsync(otro, "ExtraccionMasiva", sujeto, [_tenantA]);

        repetidoNuevo.Should().BeFalse("el mismo usuario del contexto sigue deduplicando");
        repetido.Should().Be(plantado);
        ajenoNuevo.Should().BeTrue("si agrupara, un incidente plantado suprimiría la alerta de otro y revelaría que existe");
        ajeno.Should().NotBe(plantado);
    }

    [Fact]
    public async Task La_reescritura_conserva_definer_search_path_propietario_y_permisos()
    {
        await using var conexion = await AbrirAsync();
        await using var consulta = conexion.CreateCommand();
        consulta.CommandText = """
            SELECT p.prosecdef, array_to_string(p.proconfig, ','),
                   has_function_privilege('cae_app_runtime', p.oid, 'EXECUTE'),
                   has_function_privilege('cae_app_soporte', p.oid, 'EXECUTE'),
                   has_function_privilege('cae_app_aprovisionamiento', p.oid, 'EXECUTE'),
                   EXISTS (SELECT 1 FROM aclexplode(p.proacl) a WHERE a.grantee = 0),
                   r.rolsuper OR r.rolbypassrls
            FROM pg_proc p JOIN pg_roles r ON r.oid = p.proowner
            WHERE p.oid = 'public.app_registrar_incidente_seguridad(uuid,uuid,text,text,uuid[],jsonb,integer)'::regprocedure
            """;
        await using var lector = await consulta.ExecuteReaderAsync();

        (await lector.ReadAsync()).Should().BeTrue("la firma no cambia");
        lector.GetBoolean(0).Should().BeTrue("sigue siendo SECURITY DEFINER");
        lector.GetString(1).Should().Contain("search_path=").And.Contain("pg_catalog").And.Contain("pg_temp");
        lector.GetBoolean(2).Should().BeTrue();
        lector.GetBoolean(3).Should().BeTrue();
        lector.GetBoolean(4).Should().BeTrue();
        lector.GetBoolean(5).Should().BeFalse("PUBLIC sigue sin EXECUTE");
        lector.GetBoolean(6).Should().BeTrue("con FORCE RLS, el propietario tiene que evitar la RLS");
    }

    private static async Task<(Guid Id, bool Nuevo)> RegistrarAsync(
        NpgsqlConnection conexion, string senal, string sujeto, Guid[] tenants, string evidencia = """{"recuento": 1}""")
    {
        await using var comando = conexion.CreateCommand();
        comando.CommandText = """
            SELECT incidente_id, es_nuevo
            FROM app_registrar_incidente_seguridad(@id, @transicion, @senal, @sujeto, @tenants, @evidencia::jsonb, 3600)
            """;
        comando.Parameters.AddWithValue("id", Guid.NewGuid());
        comando.Parameters.AddWithValue("transicion", Guid.NewGuid());
        comando.Parameters.AddWithValue("senal", senal);
        comando.Parameters.AddWithValue("sujeto", sujeto);
        comando.Parameters.AddWithValue("tenants", tenants);
        comando.Parameters.AddWithValue("evidencia", evidencia);
        await using var lector = await comando.ExecuteReaderAsync();
        (await lector.ReadAsync()).Should().BeTrue();
        return (lector.GetGuid(0), lector.GetBoolean(1));
    }

    private async Task EjecutarComoSuperusuarioAsync(string sql, Guid id)
    {
        await using var superusuario = await AbrirAsync();
        await using var comando = superusuario.CreateCommand();
        comando.CommandText = sql;
        comando.Parameters.AddWithValue("id", id);
        await comando.ExecuteNonQueryAsync();
    }

    private async Task<int> DeteccionesAsync(Guid id)
    {
        await using var superusuario = await AbrirAsync();
        await using var consulta = superusuario.CreateCommand();
        consulta.CommandText = """SELECT "Detecciones" FROM "IncidentesSeguridadSospechados" WHERE "Id" = @id""";
        consulta.Parameters.AddWithValue("id", id);
        return (int)(await consulta.ExecuteScalarAsync())!;
    }

    private async Task<string?> EvidenciaAsync(Guid id, string clave)
    {
        await using var superusuario = await AbrirAsync();
        await using var consulta = superusuario.CreateCommand();
        consulta.CommandText = """SELECT "Evidencia"->>@clave FROM "IncidentesSeguridadSospechados" WHERE "Id" = @id""";
        consulta.Parameters.AddWithValue("id", id);
        consulta.Parameters.AddWithValue("clave", clave);
        var valor = await consulta.ExecuteScalarAsync();
        return valor is DBNull or null ? null : (string)valor;
    }

    private async Task<NpgsqlConnection> AbrirAsync()
    {
        var conexion = new NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();
        return conexion;
    }

    private async Task<NpgsqlConnection> AbrirRestringidaComoAsync(Guid? usuarioId, Guid? tenantId, string rol = "cae_app_runtime")
    {
        var conexion = await AbrirAsync();
        await ContextoRlsDePruebas.FijarAsync(conexion, tenantId: tenantId, usuarioId: usuarioId);

        // Sin SET ROLE seguiríamos siendo superusuario y las políticas no se evaluarían.
        await using var setRol = conexion.CreateCommand();
        setRol.CommandText = $"SET ROLE {rol};";
        await setRol.ExecuteNonQueryAsync();
        return conexion;
    }

    private static async Task<long> ContarAsync(NpgsqlConnection conexion, string tabla)
    {
        await using var consulta = conexion.CreateCommand();
        consulta.CommandText = $"SELECT count(*) FROM \"{tabla}\";";
        return (long)(await consulta.ExecuteScalarAsync())!;
    }

    private CaeManagerDbContext CrearContexto()
    {
        var tenantActual = new TenantActualAmbiental { TenantId = _tenantA };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }
}
