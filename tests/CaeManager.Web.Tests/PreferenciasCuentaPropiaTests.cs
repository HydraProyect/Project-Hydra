using System.Data.Common;
using CaeManager.Application.Common;
using CaeManager.Domain.Auditoria;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace CaeManager.Web.Tests;

/// <summary>
/// <see cref="PreferenciasCuentaPropia"/> sobre el <c>CaeManagerDbContext</c> real y una tabla
/// <c>AspNetUsers</c> reducida a las columnas que el puerto puede tocar. Sin <c>PasswordHash</c> ni
/// <c>SecurityStamp</c>, cargar la cuenta entera falla igual que fallará en PostgreSQL bajo
/// <c>cae_app_soporte</c> tras el <c>REVOKE</c>: el test
/// <see cref="Cargar_la_cuenta_entera_falla_en_este_arnes"/> demuestra que el instrumento lo detecta.
///
/// <para>
/// Límite del instrumento: SQLite no tiene RLS, roles ni los interceptores de producción. La auditoría y el
/// 42501 de <c>cae_app_soporte</c> se miden sobre PostgreSQL en
/// <c>AuditoriaDeGestionDeUsuariosTests</c> y <c>FronteraCuentaFirmadaTests</c>.
/// </para>
/// </summary>
public sealed class PreferenciasCuentaPropiaTests : IAsyncLifetime
{
    private const string SelloInicial = "sello-inicial";

    private static readonly DateTime Instante = new(2026, 10, 31, 9, 0, 0, DateTimeKind.Utc);

    private readonly Guid _usuarioId = Guid.NewGuid();
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly SqliteConnection _conexion = new("Data Source=:memory:");
    private readonly ComandosAnotados _comandos = new();
    private readonly EscrituraAjena _ajena;
    private CaeManagerDbContext _contexto = null!;
    private PreferenciasCuentaPropia _preferencias = null!;

    public PreferenciasCuentaPropiaTests() => _ajena = new EscrituraAjena(_conexion, _usuarioId);

    public async Task InitializeAsync()
    {
        await _conexion.OpenAsync();
        _contexto = new CaeManagerDbContext(
            new DbContextOptionsBuilder<CaeManagerDbContext>()
                .UseSqlite(_conexion)
                .AddInterceptors(_comandos, _ajena)
                .Options,
            new EphemeralDataProtectionProvider(),
            new TenantActualAmbiental { TenantId = _tenantId });

        await EjecutarAsync("""
            CREATE TABLE "AspNetUsers" (
                "Id" TEXT NOT NULL PRIMARY KEY,
                "TenantId" TEXT NOT NULL,
                "ConcurrencyStamp" TEXT NULL,
                "Tema" INTEGER NOT NULL,
                "Idioma" INTEGER NOT NULL,
                "UltimaActividadUtc" TEXT NULL
            );
            """);
        await _contexto.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "AspNetUsers" ("Id", "TenantId", "ConcurrencyStamp", "Tema", "Idioma", "UltimaActividadUtc")
            VALUES ({_usuarioId}, {_tenantId}, {SelloInicial}, {(int)TemaPreferido.Claro}, {(int)IdiomaPreferido.Espanol}, {Instante})
            """);

        _preferencias = new PreferenciasCuentaPropia(_contexto, new PuertaAccesoDatos());
        _comandos.Textos.Clear();
    }

    public async Task DisposeAsync()
    {
        await _contexto.DisposeAsync();
        await _conexion.DisposeAsync();
    }

    [Fact]
    public async Task Cargar_la_cuenta_entera_falla_en_este_arnes()
    {
        var acto = () => _contexto.Users.AsNoTracking().FirstAsync(u => u.Id == _usuarioId);

        await acto.Should().ThrowAsync<SqliteException>(
            "sin este control, una lectura que volviera a materializar la cuenta pasaría desapercibida");
    }

    [Fact]
    public async Task La_lectura_proyecta_las_preferencias_sin_rastrear_ni_pedir_credenciales()
    {
        var preferencias = await _preferencias.ObtenerAsync(_usuarioId);

        preferencias.Should().Be(new PreferenciasDeCuenta(TemaPreferido.Claro, IdiomaPreferido.Espanol, Instante));
        _contexto.ChangeTracker.Entries().Should().BeEmpty();
        _comandos.Textos.Should().ContainSingle().Which.Should().NotContainAny("PasswordHash", "SecurityStamp");
    }

    [Fact]
    public async Task Una_cuenta_que_no_se_ve_se_lee_como_nula_y_no_se_escribe()
    {
        var ajena = Guid.NewGuid();

        (await _preferencias.ObtenerAsync(ajena)).Should().BeNull();
        (await _preferencias.GuardarTemaAsync(ajena, TemaPreferido.Oscuro)).Should().Be(ResultadoEscrituraCuenta.CuentaNoVisible);
        _contexto.ChangeTracker.Entries().Should().BeEmpty();
        _comandos.Actualizaciones.Should().BeEmpty();
    }

    [Fact]
    public async Task Guardar_el_tema_escribe_solo_su_columna_y_el_sello_y_no_deja_nada_rastreado()
    {
        (await _preferencias.GuardarTemaAsync(_usuarioId, TemaPreferido.Oscuro)).Should().Be(ResultadoEscrituraCuenta.Guardada);

        var fila = await LeerFilaAsync();
        fila.Tema.Should().Be(TemaPreferido.Oscuro);
        fila.Idioma.Should().Be(IdiomaPreferido.Espanol);
        fila.UltimaActividadUtc.Should().Be(Instante);
        fila.Sello.Should().NotBe(SelloInicial, "quien tenga la cuenta cargada de antes tiene que chocar al guardarla entera");

        var actualizacion = _comandos.Actualizaciones.Should().ContainSingle().Subject;
        actualizacion.Should().Contain("\"Tema\"").And.Contain("\"ConcurrencyStamp\"");
        actualizacion.Should().NotContainAny("\"Idioma\"", "\"UltimaActividadUtc\"", "\"TenantId\"", "PasswordHash", "SecurityStamp");
        actualizacion[actualizacion.IndexOf("WHERE", StringComparison.Ordinal)..].Should().Contain("\"ConcurrencyStamp\"",
            "sin el sello en el WHERE la escritura pisaría a ciegas");
        _contexto.ChangeTracker.Entries().Should().BeEmpty(
            "un esqueleto rastreado devolvería una cuenta vacía a la siguiente lectura del circuito");
    }

    [Fact]
    public async Task Guardar_el_idioma_escribe_solo_su_columna_y_el_sello()
    {
        (await _preferencias.GuardarIdiomaAsync(_usuarioId, IdiomaPreferido.Catalan)).Should().Be(ResultadoEscrituraCuenta.Guardada);

        (await LeerFilaAsync()).Idioma.Should().Be(IdiomaPreferido.Catalan);
        _comandos.Actualizaciones.Should().ContainSingle().Which.Should().NotContain("\"Tema\"");
    }

    /// <summary>Con el valor original a su valor por defecto, un esqueleto mal construido no vería el cambio.</summary>
    [Fact]
    public async Task Guardar_el_valor_por_defecto_sobre_otro_valor_tambien_se_escribe()
    {
        (await _preferencias.GuardarTemaAsync(_usuarioId, TemaPreferido.Sistema)).Should().Be(ResultadoEscrituraCuenta.Guardada);

        (await LeerFilaAsync()).Tema.Should().Be(TemaPreferido.Sistema);
    }

    [Fact]
    public async Task Otra_escritura_entre_lectura_y_guardado_se_reintenta_sobre_la_fila_vigente()
    {
        _ajena.Pendientes = 1;

        (await _preferencias.GuardarTemaAsync(_usuarioId, TemaPreferido.Oscuro)).Should().Be(ResultadoEscrituraCuenta.Guardada);

        var fila = await LeerFilaAsync();
        fila.Tema.Should().Be(TemaPreferido.Oscuro);
        fila.Idioma.Should().Be(IdiomaPreferido.Catalan, "el reintento no devuelve lo que escribió la otra");
        _comandos.Actualizaciones.Should().HaveCount(2);
        _contexto.ChangeTracker.Entries().Should().BeEmpty();
    }

    [Fact]
    public async Task Dos_conflictos_seguidos_se_devuelven_sin_escribir_ni_dejar_rastro()
    {
        _ajena.Pendientes = 2;

        (await _preferencias.GuardarTemaAsync(_usuarioId, TemaPreferido.Oscuro)).Should().Be(ResultadoEscrituraCuenta.Conflicto);

        (await LeerFilaAsync()).Tema.Should().Be(TemaPreferido.Claro);
        _comandos.Actualizaciones.Should().HaveCount(2, "un solo reintento, no más");
        _contexto.ChangeTracker.Entries().Should().BeEmpty("un guardado posterior del circuito no debe arrastrar el lote perdido");
    }

    /// <summary>La cuenta ya cargada por otro camino del mismo circuito sigue siendo guardable y no queda vieja.</summary>
    [Fact]
    public async Task Una_cuenta_rastreada_vigente_recibe_el_cambio_y_no_da_conflictos()
    {
        var rastreada = Rastrear(SelloInicial);

        (await _preferencias.GuardarTemaAsync(_usuarioId, TemaPreferido.Oscuro)).Should().Be(ResultadoEscrituraCuenta.Guardada);

        rastreada.Tema.Should().Be(TemaPreferido.Oscuro);
        var entrada = _contexto.Entry(rastreada);
        entrada.State.Should().Be(EntityState.Unchanged);
        rastreada.ConcurrencyStamp.Should().Be((await LeerFilaAsync()).Sello);

        rastreada.Idioma = IdiomaPreferido.Catalan;
        await _contexto.SaveChangesAsync();
        (await LeerFilaAsync()).Idioma.Should().Be(IdiomaPreferido.Catalan);
    }

    [Fact]
    public async Task Una_cuenta_rastreada_obsoleta_se_desengancha_y_no_produce_conflicto()
    {
        var obsoleta = Rastrear("sello-de-antes");
        obsoleta.Tema = TemaPreferido.Sistema;

        (await _preferencias.GuardarIdiomaAsync(_usuarioId, IdiomaPreferido.Catalan)).Should().Be(ResultadoEscrituraCuenta.Guardada);

        _contexto.Entry(obsoleta).State.Should().Be(EntityState.Detached, "la siguiente lectura tiene que venir de la base");
        var fila = await LeerFilaAsync();
        fila.Idioma.Should().Be(IdiomaPreferido.Catalan);
        fila.Tema.Should().Be(TemaPreferido.Claro, "el cambio pendiente sobre datos viejos no se cuela en esta escritura");
        _contexto.ChangeTracker.Entries().Should().BeEmpty();
    }

    [Fact]
    public async Task La_actividad_avanza_y_escribe_solo_su_columna_y_el_sello()
    {
        var despues = Instante.AddMinutes(5);

        (await _preferencias.RegistrarActividadAsync(_usuarioId, despues)).Should().Be(ResultadoEscrituraCuenta.Guardada);

        (await LeerFilaAsync()).UltimaActividadUtc.Should().Be(despues);
        _comandos.Actualizaciones.Should().ContainSingle().Which.Should().Contain("\"UltimaActividadUtc\"").And.NotContain("\"Tema\"");
    }

    /// <summary>Otro circuito de la misma cuenta pudo registrar una actividad posterior entre medias.</summary>
    [Fact]
    public async Task La_actividad_no_retrocede_una_posterior_ya_guardada()
    {
        var posterior = Instante.AddHours(1);
        _ajena.Pendientes = 1;
        _ajena.ActividadAjena = posterior;

        (await _preferencias.RegistrarActividadAsync(_usuarioId, Instante.AddMinutes(5))).Should().Be(ResultadoEscrituraCuenta.Guardada);

        (await LeerFilaAsync()).UltimaActividadUtc.Should().Be(posterior);
    }

    [Fact]
    public async Task Un_rechazo_que_no_es_de_concurrencia_sube_sin_reintentar_ni_dejar_rastro()
    {
        await EjecutarAsync("""
            CREATE TRIGGER "rechaza_escritura" BEFORE UPDATE ON "AspNetUsers"
            BEGIN SELECT RAISE(ABORT, 'permission denied for table AspNetUsers'); END;
            """);

        var acto = () => _preferencias.GuardarTemaAsync(_usuarioId, TemaPreferido.Oscuro);

        await acto.Should().ThrowAsync<DbUpdateException>("bajo cae_app_soporte el 42501 sigue subiendo como hasta ahora");
        _comandos.Actualizaciones.Should().ContainSingle();
        _contexto.ChangeTracker.Entries().Should().BeEmpty();
    }

    /// <summary>
    /// <c>TenantSelladoInterceptor</c> sella la fila de auditoría de una cuenta con el <c>TenantId</c> de la instancia
    /// rastreada. Sin él en el esqueleto caería al Tenant de la sesión, que en un Workspace operativo derivado es el
    /// Tenant beneficiario y no el Tenant propietario de la cuenta.
    /// </summary>
    [Fact]
    public async Task El_esqueleto_lleva_el_Tenant_propietario_de_la_cuenta()
    {
        Guid? tenantRastreado = null;
        _ajena.AlGuardar = contexto => tenantRastreado = contexto.ChangeTracker.Entries<ApplicationUser>().Single().Entity.TenantId;

        await _preferencias.GuardarTemaAsync(_usuarioId, TemaPreferido.Oscuro);

        tenantRastreado.Should().Be(_tenantId);
    }

    /// <summary>La fila de auditoría pendiente de un rechazo no puede colarse en el siguiente guardado del circuito.</summary>
    [Fact]
    public async Task Un_rechazo_descarta_tambien_la_auditoria_pendiente()
    {
        _ajena.AlGuardar = contexto => contexto.Add(new RegistroAuditoria(
            "Usuario", _usuarioId, "Modificado", null, null, null, TipoActorAuditoria.Desconocido));

        var acto = () => _preferencias.GuardarTemaAsync(_usuarioId, TemaPreferido.Oscuro);

        await acto.Should().ThrowAsync<DbUpdateException>();
        _contexto.ChangeTracker.Entries().Should().BeEmpty();
    }

    [Fact]
    public async Task Cancelar_durante_el_guardado_no_lo_interrumpe()
    {
        using var cancelacion = new CancellationTokenSource();
        _ajena.AlGuardar = _ => cancelacion.Cancel();

        (await _preferencias.GuardarTemaAsync(_usuarioId, TemaPreferido.Oscuro, cancelacion.Token))
            .Should().Be(ResultadoEscrituraCuenta.Guardada);

        (await LeerFilaAsync()).Tema.Should().Be(TemaPreferido.Oscuro);
    }

    private ApplicationUser Rastrear(string sello)
    {
        var cuenta = new ApplicationUser
        {
            Id = _usuarioId,
            TenantId = _tenantId,
            ConcurrencyStamp = sello,
            Tema = TemaPreferido.Claro,
            Idioma = IdiomaPreferido.Espanol,
            UltimaActividadUtc = Instante,
        };
        _contexto.Attach(cuenta);
        return cuenta;
    }

    private Task<FilaDeCuenta> LeerFilaAsync() => _contexto.Users.AsNoTracking()
        .Where(u => u.Id == _usuarioId)
        .Select(u => new FilaDeCuenta(u.ConcurrencyStamp, u.Tema, u.Idioma, u.UltimaActividadUtc))
        .SingleAsync();

    private async Task EjecutarAsync(string sql)
    {
        await using var comando = _conexion.CreateCommand();
        comando.CommandText = sql;
        await comando.ExecuteNonQueryAsync();
    }

    private sealed record FilaDeCuenta(string? Sello, TemaPreferido Tema, IdiomaPreferido Idioma, DateTime? UltimaActividadUtc);

    /// <summary>Anota el texto de cada comando para ver qué columnas salen de verdad en el SQL.</summary>
    private sealed class ComandosAnotados : DbCommandInterceptor
    {
        public List<string> Textos { get; } = [];

        public IEnumerable<string> Actualizaciones => Textos.Where(t => t.Contains("UPDATE", StringComparison.Ordinal));

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Textos.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Textos.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }

    /// <summary>
    /// Otra escritura que gana la carrera justo antes de cada guardado pendiente: renueva el sello por fuera del
    /// contexto, igual que lo haría otro circuito de la misma cuenta.
    /// </summary>
    private sealed class EscrituraAjena(SqliteConnection conexion, Guid usuarioId) : SaveChangesInterceptor
    {
        public int Pendientes { get; set; }
        public DateTime? ActividadAjena { get; set; }
        public Action<DbContext>? AlGuardar { get; set; }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            AlGuardar?.Invoke(eventData.Context!);

            if (Pendientes > 0)
            {
                Pendientes--;
                await using var comando = conexion.CreateCommand();
                comando.CommandText = """
                    UPDATE "AspNetUsers" SET "ConcurrencyStamp" = $sello, "Idioma" = $idioma,
                        "UltimaActividadUtc" = COALESCE($actividad, "UltimaActividadUtc")
                    WHERE "Id" = $id
                    """;
                comando.Parameters.AddWithValue("$sello", Guid.NewGuid().ToString());
                comando.Parameters.AddWithValue("$idioma", (int)IdiomaPreferido.Catalan);
                comando.Parameters.AddWithValue("$actividad", ActividadAjena is { } instante
                    ? instante.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFF", System.Globalization.CultureInfo.InvariantCulture)
                    : DBNull.Value);
                comando.Parameters.AddWithValue("$id", usuarioId.ToString().ToUpperInvariant());
                (await comando.ExecuteNonQueryAsync(cancellationToken)).Should().Be(1, "la escritura ajena tiene que tocar la fila");
            }

            return result;
        }
    }
}
