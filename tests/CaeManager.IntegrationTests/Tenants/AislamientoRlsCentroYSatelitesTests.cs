using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Trabajadores;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace CaeManager.IntegrationTests.Tenants;

/// <summary>
/// Cierra un hueco de instrumento, no de producto: la RLS de <c>Centros</c>,
/// <c>Asignaciones</c> y <c>TiposDocumentoCentros</c> estaba probada solo de
/// forma <b>estructural</b>. <see cref="PoliticasRlsCubrenModeloTests"/>
/// confirma que la política existe y <see cref="CoberturaRlsDelModeloTests"/>
/// que su texto menciona las columnas correctas; <see cref="AislamientoPorAgregadoTests"/>
/// sí las ejercita con datos, pero a través del <c>HasQueryFilter</c> de EF —
/// la primera línea de defensa, no la última. Ninguno las ataca bajo
/// <c>cae_app_runtime</c>, que es el rol con el que corre producción.
///
/// La diferencia importa: que la política esté escrita con las columnas
/// correctas no demuestra que PostgreSQL la aplique donde hace falta, ni que
/// un <c>GRANT</c> no la haya dejado inerte por otra vía. Son dos afirmaciones
/// distintas y hacen falta las dos — mismo razonamiento que ya documenta
/// <see cref="AislamientoRlsPostgresTests"/> para <c>Clientes</c>.
///
/// Se escribe <b>antes</b> de F5 a propósito: F5 reescribe estas tres tablas
/// (partiendo <c>Centro</c> en <c>CentroTrabajo</c> + participación), y un
/// instrumento creado después del cambio no puede observar una regresión
/// introducida por el cambio.
/// </summary>
public class AislamientoRlsCentroYSatelitesTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenantA = Guid.NewGuid();
    private readonly Guid _tenantB = Guid.NewGuid();
    private Guid _centroDeA;

    /// <summary>Las tres tablas bajo prueba, para las comprobaciones que aplican por igual a todas.</summary>
    public static TheoryData<string> TablasBajoPrueba => ["Centros", "Asignaciones", "TiposDocumentoCentros"];

    public async Task InitializeAsync()
    {
        // Se siembra como el rol propietario (postgres) a propósito: RLS no
        // debe restringirlo, y eso es justo lo que confirma el control
        // negativo del final.
        await using var contexto = CrearContexto(_tenantA);
        await contexto.Database.MigrateAsync();
        _centroDeA = await SembrarJuegoCompletoAsync(contexto, "Planta Sevilla", "12345678Z", "B12345674", "B87654323");

        await using var contextoB = CrearContexto(_tenantB);
        await SembrarJuegoCompletoAsync(contextoB, "Planta Bilbao", "77189989B", "B10380186", "B10380194");
    }

    public async Task DisposeAsync() => await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    /// <summary>
    /// Un Centro con su Asignación y su requisito documental — una fila en
    /// cada una de las tres tablas bajo prueba, que es lo que necesitan los
    /// recuentos de abajo.
    /// </summary>
    private static async Task<Guid> SembrarJuegoCompletoAsync(
        CaeManagerDbContext contexto, string nombreCentro, string dni, string cifTitular, string cifEjecutora)
    {
        var titular = Empresa.CrearComoCliente($"Titular de {nombreCentro} S.A.", cifTitular, false, null, null);
        var ejecutora = new Empresa($"Ejecutora de {nombreCentro} S.L.", cifEjecutora);
        contexto.Empresas.AddRange(titular, ejecutora);
        await contexto.SaveChangesAsync();

        var centro = new Centro(titular.Id, ejecutora.Id, nombreCentro);
        var trabajador = Trabajador.DeEmpresa(ejecutora.Id, "Nombre", "Apellidos", dni);
        var tipoDocumento = new TipoDocumento($"Apto médico de {nombreCentro}", 12, true, 1, AmbitoAplicacion.Trabajador, esObligatorio: true);
        contexto.Centros.Add(centro);
        contexto.Trabajadores.Add(trabajador);
        contexto.TiposDocumento.Add(tipoDocumento);
        await contexto.SaveChangesAsync();

        contexto.Asignaciones.Add(new Asignacion(trabajador.Id, centro.Id, DateOnly.FromDateTime(DateTime.UtcNow)));
        contexto.TiposDocumentoCentros.Add(new TipoDocumentoCentro(tipoDocumento.Id, centro.Id, incluido: true, bloqueaAcceso: false));
        await contexto.SaveChangesAsync();

        return centro.Id;
    }

    [Theory]
    [MemberData(nameof(TablasBajoPrueba))]
    public async Task El_rol_restringido_no_ve_ninguna_fila_sin_tenant_de_sesion_fijado(string tabla)
    {
        await using var conexion = await AbrirComoRolRestringidoAsync();

        (await ContarAsync(conexion, tabla)).Should().Be(0,
            "sin app.tenant_id fijado la política debe fallar cerrado y ocultar TODAS las filas, no solo las ajenas");
    }

    [Theory]
    [MemberData(nameof(TablasBajoPrueba))]
    public async Task El_rol_restringido_solo_ve_las_filas_del_tenant_fijado(string tabla)
    {
        await using var conexion = await AbrirComoRolRestringidoAsync();

        await FijarTenantDeSesionAsync(conexion, _tenantA);
        (await ContarAsync(conexion, tabla)).Should().Be(1);

        await FijarTenantDeSesionAsync(conexion, _tenantB);
        (await ContarAsync(conexion, tabla)).Should().Be(1, "el tenant B tiene su propio juego completo");
    }

    /// <summary>
    /// La fuga que no necesita leer nada ajeno: un operador legítimo del
    /// tenant A ve sus propias filas, y sin <c>WITH CHECK</c> podría
    /// empujarlas al tenant B cambiándoles el <c>TenantId</c>. Ninguna
    /// comprobación de lectura lo detendría.
    /// </summary>
    [Theory]
    [MemberData(nameof(TablasBajoPrueba))]
    public async Task Un_update_que_mueve_la_fila_a_otro_tenant_lo_rechaza_el_with_check(string tabla)
    {
        await using var conexion = await AbrirComoRolRestringidoAsync();
        await FijarTenantDeSesionAsync(conexion, _tenantA);

        await using var comando = conexion.CreateCommand();
        comando.CommandText = $"UPDATE \"{tabla}\" SET \"TenantId\" = @destino;";
        comando.Parameters.AddWithValue("destino", _tenantB);

        var accion = async () => await comando.ExecuteNonQueryAsync();

        (await accion.Should().ThrowAsync<PostgresException>(
            "quien ve una fila no debe poder regalársela a otro tenant"))
            .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
    }

    /// <summary>
    /// Control positivo del anterior, obligatorio: lo que rechaza el
    /// <c>WITH CHECK</c> es el cambio de dueño, no la escritura. Sin esto,
    /// aquel test pasaría igual si la política prohibiera todo <c>UPDATE</c>,
    /// que sería otra cosa — y una sobre-restricción es tan defecto como una
    /// fuga.
    /// </summary>
    [Theory]
    [InlineData("Centros", "\"Nombre\" = 'Planta Sevilla (editada)'")]
    [InlineData("Asignaciones", "\"FechaBaja\" = CURRENT_DATE")]
    [InlineData("TiposDocumentoCentros", "\"BloqueaAcceso\" = true")]
    public async Task Un_update_que_deja_la_fila_en_su_tenant_si_se_permite(string tabla, string asignacion)
    {
        await using var conexion = await AbrirComoRolRestringidoAsync();
        await FijarTenantDeSesionAsync(conexion, _tenantA);

        await using var comando = conexion.CreateCommand();
        comando.CommandText = $"UPDATE \"{tabla}\" SET {asignacion};";

        (await comando.ExecuteNonQueryAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Un_insert_de_un_centro_de_otro_tenant_lo_rechaza_el_with_check()
    {
        await using var conexion = await AbrirComoRolRestringidoAsync();
        await FijarTenantDeSesionAsync(conexion, _tenantA);

        await using var comando = conexion.CreateCommand();
        comando.CommandText =
            "INSERT INTO \"Centros\" (\"Id\", \"TenantId\", \"ClienteId\", \"EmpresaId\", \"Nombre\", \"CreadoEnUtc\", \"Version\", \"EstaEliminado\") " +
            "SELECT gen_random_uuid(), @destino, c.\"ClienteId\", c.\"EmpresaId\", 'Sembrado en ajeno', now(), gen_random_uuid(), false " +
            "FROM \"Centros\" c LIMIT 1;";
        comando.Parameters.AddWithValue("destino", _tenantB);

        var accion = async () => await comando.ExecuteNonQueryAsync();

        (await accion.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
    }

    /// <summary>
    /// Propiedad que ninguna suite afirma hoy para ninguna tabla, y cuyo modo
    /// de fallo es distinto al de INSERT/UPDATE: bajo RLS, un <c>DELETE</c>
    /// sobre filas ajenas <b>no lanza</b> — simplemente no las ve y afecta a
    /// cero filas. Un código que interprete "0 filas borradas" como éxito
    /// silencioso se comporta mal sin que nada falle.
    /// </summary>
    [Theory]
    [MemberData(nameof(TablasBajoPrueba))]
    public async Task Un_delete_bajo_otro_tenant_no_borra_la_fila_ajena_y_no_falla(string tabla)
    {
        await using (var conexion = await AbrirComoRolRestringidoAsync())
        {
            await FijarTenantDeSesionAsync(conexion, _tenantB);

            await using var comando = conexion.CreateCommand();
            comando.CommandText = $"DELETE FROM \"{tabla}\" WHERE \"TenantId\" = @ajeno;";
            comando.Parameters.AddWithValue("ajeno", _tenantA);

            (await comando.ExecuteNonQueryAsync()).Should().Be(0, "la fila del tenant A no es visible desde una sesión del tenant B");
        }

        // Comprobado desde el propietario, que sí ve las dos: la fila sigue ahí.
        await using var comoPropietario = new NpgsqlConnection(_cadenaConexion);
        await comoPropietario.OpenAsync();
        (await ContarAsync(comoPropietario, tabla)).Should().Be(2, "ambos tenants conservan su fila");
    }

    /// <summary>
    /// La FK compuesta <c>(TenantId, CentroId)</c> —no RLS— es lo que impide
    /// que una Asignación de un tenant apunte a un Centro de otro. Se afirma
    /// aquí porque F5 va a re-anclar esa FK: si al hacerlo se perdiera la
    /// composición con el tenant, nada más lo detectaría. Por eso el código
    /// esperado es <c>23503</c> y no <c>42501</c>.
    /// </summary>
    [Fact]
    public async Task Una_asignacion_no_puede_apuntar_a_un_centro_de_otro_tenant()
    {
        await using var conexion = await AbrirComoRolRestringidoAsync();
        await FijarTenantDeSesionAsync(conexion, _tenantB);

        await using var comando = conexion.CreateCommand();
        comando.CommandText =
            "INSERT INTO \"Asignaciones\" (\"Id\", \"TenantId\", \"TrabajadorId\", \"CentroId\", \"FechaAlta\") " +
            "SELECT gen_random_uuid(), @tenantB, a.\"TrabajadorId\", @centroAjeno, CURRENT_DATE " +
            "FROM \"Asignaciones\" a LIMIT 1;";
        comando.Parameters.AddWithValue("tenantB", _tenantB);
        comando.Parameters.AddWithValue("centroAjeno", _centroDeA);

        var accion = async () => await comando.ExecuteNonQueryAsync();

        (await accion.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.ForeignKeyViolation);
    }

    [Theory]
    [MemberData(nameof(TablasBajoPrueba))]
    public async Task El_rol_propietario_de_las_tablas_no_esta_restringido_por_rls(string tabla)
    {
        // Control negativo: sin SET ROLE, la misma consulta ve las filas de
        // los dos tenants — confirma que RLS es inerte para el propietario y
        // que los recuentos de arriba miden la política, no otra cosa.
        await using var conexion = new NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();

        (await ContarAsync(conexion, tabla)).Should().Be(2);
    }

    private async Task<NpgsqlConnection> AbrirComoRolRestringidoAsync()
    {
        var conexion = new NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();

        await using var setRol = conexion.CreateCommand();
        setRol.CommandText = "SET ROLE cae_app_runtime;";
        await setRol.ExecuteNonQueryAsync();

        return conexion;
    }

    private static async Task FijarTenantDeSesionAsync(NpgsqlConnection conexion, Guid tenantId)
    {
        await using var comando = conexion.CreateCommand();
        comando.CommandText = "SELECT set_config('app.tenant_id', @valor, false);";
        comando.Parameters.AddWithValue("valor", tenantId.ToString());
        await comando.ExecuteNonQueryAsync();
    }

    private static async Task<long> ContarAsync(NpgsqlConnection conexion, string tabla)
    {
        await using var consulta = conexion.CreateCommand();
        consulta.CommandText = $"SELECT count(*) FROM \"{tabla}\";";
        return (long)(await consulta.ExecuteScalarAsync())!;
    }

    private CaeManagerDbContext CrearContexto(Guid tenantId)
    {
        var tenantActual = new TenantActualAmbiental { TenantId = tenantId };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }
}
