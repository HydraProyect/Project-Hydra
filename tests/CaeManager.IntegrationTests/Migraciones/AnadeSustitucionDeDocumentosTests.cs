using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Interceptors;
using CaeManager.Infrastructure.Persistence.Seed;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;

namespace CaeManager.IntegrationTests.Migraciones;

/// <summary>
/// Documento efectivo, PR 1: la migración <c>AnadeSustitucionDeDocumentos</c> añade a <c>Documentos</c> las columnas
/// <c>SustituidoPorDocumentoId</c>, <c>SustituidoEnUtc</c> y <c>MotivoSustitucion</c>, dos CHECK, el índice de la FK y la
/// FK compuesta con el Tenant sobre la propia tabla. <b>Esta clase prueba esas barreras en PostgreSQL real</b>; la
/// guarda de dominio vive en <c>DocumentoSustitucionTests</c> (Domain.Tests) y no presta evidencia a esta.
///
/// <para>
/// Cada rechazo se prueba con un <c>UPDATE</c> directo (el agregado no permite construir esas filas) y exige el
/// SQLSTATE <b>y el nombre exacto</b> de la restricción: un 23514 de otro CHECK no cuenta. Cada uno tiene su control
/// positivo (la misma operación con datos válidos sí se acepta), para que un esquema que rechazase todo o un arnés que
/// no escribe nada no pasen por verde.
/// </para>
/// </summary>
public class AnadeSustitucionDeDocumentosTests : IAsyncLifetime
{
    private const string MigracionDelCambio = "20261003192624_AnadeSustitucionDeDocumentos";

    private const string CheckCoherente = "CK_Documentos_SustitucionCoherente";
    private const string CheckNoASiMismo = "CK_Documentos_NoSeSustituyeASiMismo";
    private const string ForaneaSustituto = "FK_Documentos_Documentos_TenantId_SustituidoPorDocumentoId";
    private const string IndiceDeLaForanea = "IX_Documentos_TenantId_SustituidoPorDocumentoId";

    private static readonly string[] Columnas = ["SustituidoPorDocumentoId", "SustituidoEnUtc", "MotivoSustitucion"];

    private readonly string _cadena = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Tenant _tenant = new("Tenant de la sustitución");
    private readonly Tenant _otroTenant = new("Otro Tenant de la sustitución");

    private string MigracionAnterior
    {
        get
        {
            using var contexto = NuevoContexto(null);
            var migraciones = contexto.Database.GetMigrations().ToList();
            var indice = migraciones.IndexOf(MigracionDelCambio);
            indice.Should().BeGreaterThan(0, "la migración del cambio existe en el ensamblado y no es la línea base");
            return migraciones[indice - 1];
        }
    }

    public async Task InitializeAsync()
    {
        await BaseDatosPostgresDePruebas.MigrarAsync(_cadena);
        await using var contexto = NuevoContexto(null);
        contexto.Tenants.AddRange(_tenant, _otroTenant);
        await contexto.SaveChangesAsync();
    }

    public async Task DisposeAsync() => await BaseDatosPostgresDePruebas.EliminarAsync(_cadena);

    // ── Esquema final ──────────────────────────────────────────────────────

    [Fact]
    public async Task El_esquema_final_tiene_las_tres_columnas_nulas_los_dos_CHECK_la_FK_y_el_indice()
    {
        await using var contexto = NuevoContexto(null);

        foreach (var columna in Columnas)
            (await Anulable(contexto, columna)).Should().Be(true, $"{columna} existe y admite nulos (migración aditiva)");
        (await Anulable(contexto, "NoExisteEstaColumna")).Should().BeNull("control positivo: el instrumento distingue una columna que no existe");

        (await ExisteRestriccion(contexto, CheckCoherente)).Should().BeTrue();
        (await ExisteRestriccion(contexto, CheckNoASiMismo)).Should().BeTrue();
        (await ExisteRestriccion(contexto, ForaneaSustituto)).Should().BeTrue();
        (await ExisteIndice(contexto, IndiceDeLaForanea)).Should().BeTrue();
        (await ExisteRestriccion(contexto, "CK_Documentos_NoExisteEstaRestriccion")).Should().BeFalse("control: el instrumento no responde sí a todo");
    }

    // ── Los CHECK, en PostgreSQL ───────────────────────────────────────────

    [Fact]
    public async Task Un_documento_no_se_puede_apuntar_a_si_mismo_como_sustituto()
    {
        var (anterior, _) = await SembrarParAsync(_tenant);

        var error = await IntentarActualizarAsync(_tenant, anterior, sustituto: anterior, enUtc: DateTime.UtcNow, motivo: 1);

        error.SqlState.Should().Be("23514");
        error.ConstraintName.Should().Be(CheckNoASiMismo);
    }

    [Theory]
    [InlineData(false, true, true, "falta el sustituto")]
    [InlineData(true, false, true, "falta el instante")]
    [InlineData(true, true, false, "falta el motivo")]
    [InlineData(true, false, false, "solo el sustituto")]
    [InlineData(false, true, false, "solo el instante")]
    [InlineData(false, false, true, "solo el motivo")]
    public async Task Las_tres_columnas_se_informan_juntas_o_ninguna(bool sustituto, bool instante, bool motivo, string caso)
    {
        var (anterior, nuevo) = await SembrarParAsync(_tenant);

        var error = await IntentarActualizarAsync(
            _tenant, anterior, sustituto: sustituto ? nuevo : null, enUtc: instante ? DateTime.UtcNow : null, motivo: motivo ? 1 : null);

        error.SqlState.Should().Be("23514", caso);
        error.ConstraintName.Should().Be(CheckCoherente, caso);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(-1)]
    public async Task El_motivo_solo_puede_ser_uno_de_los_tres_valores_del_enum(int motivo)
    {
        var (anterior, nuevo) = await SembrarParAsync(_tenant);

        var error = await IntentarActualizarAsync(_tenant, anterior, sustituto: nuevo, enUtc: DateTime.UtcNow, motivo: motivo);

        error.SqlState.Should().Be("23514");
        error.ConstraintName.Should().Be(CheckCoherente);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Control_positivo_la_misma_sustitucion_completa_y_valida_se_acepta(int motivo)
    {
        var (anterior, nuevo) = await SembrarParAsync(_tenant);

        var escritas = await ActualizarAsync(_tenant, anterior, sustituto: nuevo, enUtc: DateTime.UtcNow, motivo: motivo);

        escritas.Should().Be(1, "el CHECK rechaza lo incoherente, no la sustitución: sin esto un esquema que rechazase todo pasaría las pruebas anteriores");
    }

    // ── La FK compuesta con el Tenant ──────────────────────────────────────

    [Fact]
    public async Task El_sustituto_no_puede_ser_un_documento_de_otro_Tenant_propietario()
    {
        var (anterior, _) = await SembrarParAsync(_tenant);
        var (ajeno, _) = await SembrarParAsync(_otroTenant);

        var error = await IntentarActualizarAsync(_tenant, anterior, sustituto: ajeno, enUtc: DateTime.UtcNow, motivo: 1);

        error.SqlState.Should().Be("23503");
        error.ConstraintName.Should().Be(ForaneaSustituto);
    }

    [Fact]
    public async Task El_sustituto_tiene_que_existir()
    {
        var (anterior, _) = await SembrarParAsync(_tenant);

        var error = await IntentarActualizarAsync(_tenant, anterior, sustituto: Guid.NewGuid(), enUtc: DateTime.UtcNow, motivo: 1);

        error.SqlState.Should().Be("23503");
        error.ConstraintName.Should().Be(ForaneaSustituto);
    }

    [Fact]
    public async Task No_se_puede_borrar_el_sustituto_de_un_documento_sustituido()
    {
        var (anterior, nuevo) = await SembrarParAsync(_tenant);
        await ActualizarAsync(_tenant, anterior, sustituto: nuevo, enUtc: DateTime.UtcNow, motivo: 1);

        var error = await IntentarEscribirAsync(
            _tenant, c => c.Database.ExecuteSqlRawAsync("DELETE FROM \"Documentos\" WHERE \"Id\" = {0}", nuevo));

        // ON DELETE RESTRICT informa 23001 (restrict_violation) o 23503 (foreign_key_violation) según la versión de
        // PostgreSQL (medido: el clúster local da 23503 y el de CI 23001); lo que no cambia es la restricción.
        error.SqlState.Should().BeOneOf(["23001", "23503"], "Restrict: el sustituido no puede quedar apuntando a un sustituto inexistente");
        error.ConstraintName.Should().Be(ForaneaSustituto);
    }

    // ── Dominio ↔ base: ida y vuelta y expresión operativa en PostgreSQL ───

    [Fact]
    public async Task La_sustitucion_hecha_por_el_dominio_se_guarda_se_lee_y_la_expresion_operativa_se_traduce_a_SQL()
    {
        var (empresa, tipo) = await SembrarTitularYTipoAsync(_tenant);
        var ahora = new DateTime(2026, 10, 3, 12, 30, 15, DateTimeKind.Utc);
        Guid idAnterior, idNuevo, idEliminado;

        await using (var contexto = NuevoContexto(_tenant.Id))
        {
            var anterior = Documento.DeEmpresa(empresa, tipo, new DateOnly(2025, 1, 1), VigenciaDocumento.NoCaduca, "a/anterior.pdf");
            var nuevo = Documento.DeEmpresa(empresa, tipo, new DateOnly(2026, 1, 1), VigenciaDocumento.NoCaduca, "a/nuevo.pdf");
            var eliminado = Documento.DeEmpresa(empresa, tipo, new DateOnly(2026, 2, 1), VigenciaDocumento.NoCaduca);
            contexto.Documentos.AddRange(anterior, nuevo, eliminado);
            await contexto.SaveChangesAsync();

            anterior.SustituirPor(nuevo, MotivoSustitucionDocumento.Renovacion, ahora);
            eliminado.MarcarComoEliminado(Guid.NewGuid());
            await contexto.SaveChangesAsync();
            (idAnterior, idNuevo, idEliminado) = (anterior.Id, nuevo.Id, eliminado.Id);
        }

        await using var lectura = NuevoContexto(_tenant.Id);
        var leido = await lectura.Documentos.SingleAsync(d => d.Id == idAnterior);
        leido.SustituidoPorDocumentoId.Should().Be(idNuevo);
        leido.SustituidoEnUtc.Should().Be(ahora);
        leido.MotivoSustitucion.Should().Be(MotivoSustitucionDocumento.Renovacion);
        leido.EstaSustituido.Should().BeTrue();
        leido.ArchivoUrl.Should().Be("a/anterior.pdf", "la sustitución no toca el archivo del sustituido");

        var operativos = await lectura.Documentos.Where(DocumentoOperativo.Expresion).Select(d => d.Id).ToListAsync();
        operativos.Should().Equal([idNuevo], "solo el nuevo: el sustituido es historial y el eliminado no cuenta");

        // El filtro global ya excluye al eliminado; la expresión lo excluye por sí misma si el filtro se apaga.
        var sinFiltros = await lectura.Documentos.IgnoreQueryFilters().Where(d => d.EmpresaId == empresa).Select(d => d.Id).ToListAsync();
        sinFiltros.Should().BeEquivalentTo([idAnterior, idNuevo, idEliminado], "control positivo: sin filtros el instrumento ve los tres");
        var operativosSinFiltros = await lectura.Documentos.IgnoreQueryFilters().Where(d => d.EmpresaId == empresa)
            .Where(DocumentoOperativo.Expresion).Select(d => d.Id).ToListAsync();
        operativosSinFiltros.Should().Equal([idNuevo]);
    }

    [Fact]
    public async Task Sustituir_con_un_documento_recien_creado_se_guarda_en_una_sola_operacion_y_el_Tenant_lo_sella_el_interceptor()
    {
        // La ruta que usará el comando de renovar (PR 4): el sustituto aún no está guardado ni tiene Tenant sellado
        // cuando se llama a SustituirPor; la FK compuesta con el Tenant se comprueba al guardar los dos a la vez.
        var (empresa, tipo) = await SembrarTitularYTipoAsync(_tenant);
        Guid idAnterior;
        await using (var contexto = NuevoContexto(_tenant.Id))
        {
            var anterior = Documento.DeEmpresa(empresa, tipo, new DateOnly(2025, 1, 1), VigenciaDocumento.NoCaduca);
            contexto.Documentos.Add(anterior);
            await contexto.SaveChangesAsync();
            idAnterior = anterior.Id;
        }

        Guid idNuevo;
        await using (var contexto = NuevoContexto(_tenant.Id))
        {
            var anterior = await contexto.Documentos.SingleAsync(d => d.Id == idAnterior);
            var nuevo = Documento.DeEmpresa(empresa, tipo, new DateOnly(2026, 1, 1), VigenciaDocumento.NoCaduca);
            contexto.Documentos.Add(nuevo);
            anterior.SustituirPor(nuevo, MotivoSustitucionDocumento.Renovacion, DateTime.UtcNow);
            nuevo.TenantId.Should().BeEmpty("control: todavía no lo ha sellado el interceptor");

            await contexto.Invoking(c => c.SaveChangesAsync()).Should().NotThrowAsync();
            nuevo.TenantId.Should().Be(_tenant.Id);
            idNuevo = nuevo.Id;
        }

        await using var lectura = NuevoContexto(_tenant.Id);
        (await lectura.Documentos.SingleAsync(d => d.Id == idAnterior)).SustituidoPorDocumentoId.Should().Be(idNuevo);
        (await lectura.Documentos.Where(DocumentoOperativo.Expresion).Select(d => d.Id).ToListAsync())
            .Should().Equal([idNuevo]);
    }

    // ── La migración sobre datos, RLS y Down ───────────────────────────────

    [Fact]
    public async Task La_migracion_conserva_los_documentos_existentes_los_deja_operativos_y_Down_deshace_todo_sin_tocar_RLS()
    {
        var (anterior, nuevo) = await SembrarParAsync(_tenant);
        var (otroAnterior, otroNuevo) = await SembrarParAsync(_otroTenant);
        var politicasFinales = await PoliticasDeDocumentosAsync();
        politicasFinales.Should().NotBeEmpty("control positivo: Documentos tiene su política por Tenant y el instrumento la ve");

        await DeshacerElCambioAsync();

        await using (var contexto = NuevoContexto(null))
        {
            foreach (var columna in Columnas)
                (await Anulable(contexto, columna)).Should().BeNull($"Down quita {columna}");
            (await ExisteRestriccion(contexto, CheckCoherente)).Should().BeFalse("Down quita el CHECK de coherencia");
            (await ExisteRestriccion(contexto, CheckNoASiMismo)).Should().BeFalse("Down quita el CHECK de no autosustitución");
            (await ExisteRestriccion(contexto, ForaneaSustituto)).Should().BeFalse("Down quita la FK");
            (await ExisteIndice(contexto, IndiceDeLaForanea)).Should().BeFalse("Down quita el índice");
            (await ContarDocumentosAsync(contexto)).Should().Be(4, "Down no borra documentos");
        }
        (await PoliticasDeDocumentosAsync()).Should().Equal(politicasFinales, "Down no toca RLS");

        await MigrarAsync();

        await using (var contexto = NuevoContexto(null))
        {
            (await ContarDocumentosAsync(contexto)).Should().Be(4, "la migración no transforma ni borra datos");
            var filas = await contexto.Documentos.IgnoreQueryFilters().ToListAsync();
            filas.Select(d => d.Id).Should().BeEquivalentTo([anterior, nuevo, otroAnterior, otroNuevo], "los mismos documentos, con los mismos Ids (D8)");
            filas.Should().OnlyContain(d => d.SustituidoPorDocumentoId == null && d.SustituidoEnUtc == null && d.MotivoSustitucion == null,
                "toda fila existente queda sin sustituir");
            filas.Should().OnlyContain(d => DocumentoOperativo.Es(d), "y por tanto operativa");

            foreach (var columna in Columnas)
                (await Anulable(contexto, columna)).Should().Be(true);
            (await ExisteRestriccion(contexto, CheckCoherente)).Should().BeTrue();
            (await ExisteRestriccion(contexto, CheckNoASiMismo)).Should().BeTrue();
            (await ExisteRestriccion(contexto, ForaneaSustituto)).Should().BeTrue();
            (await ExisteIndice(contexto, IndiceDeLaForanea)).Should().BeTrue();
        }
        (await PoliticasDeDocumentosAsync()).Should().Equal(politicasFinales, "la migración no crea ni cambia políticas RLS: la de Documentos es por fila y por TenantId");
    }

    // ── Arnés ──────────────────────────────────────────────────────────────

    private static int _secuencia;

    /// <summary>Una Empresa y un Tipo de documento del Tenant, de los que cuelgan los documentos de prueba.</summary>
    private async Task<(Guid Empresa, Guid Tipo)> SembrarTitularYTipoAsync(Tenant tenant)
    {
        var n = Interlocked.Increment(ref _secuencia);
        await using var contexto = NuevoContexto(tenant.Id);
        var empresa = new Empresa($"Empresa de la sustitución {n}", DatosPruebaSeeder.GenerarCifValido(8_700_000 + n));
        var tipo = new TipoDocumento($"Certificado de la sustitución {n}", 12, true, 800 + n, AmbitoAplicacion.Empresa);
        contexto.Empresas.Add(empresa);
        contexto.TiposDocumento.Add(tipo);
        await contexto.SaveChangesAsync();
        return (empresa.Id, tipo.Id);
    }

    /// <summary>Dos documentos del mismo titular y tipo, en el Tenant dado (un anterior y un posible sustituto).</summary>
    private async Task<(Guid Anterior, Guid Nuevo)> SembrarParAsync(Tenant tenant)
    {
        var (empresa, tipo) = await SembrarTitularYTipoAsync(tenant);
        await using var contexto = NuevoContexto(tenant.Id);
        var anterior = Documento.DeEmpresa(empresa, tipo, new DateOnly(2025, 1, 1), VigenciaDocumento.NoCaduca);
        var nuevo = Documento.DeEmpresa(empresa, tipo, new DateOnly(2026, 1, 1), VigenciaDocumento.NoCaduca);
        contexto.Documentos.AddRange(anterior, nuevo);
        await contexto.SaveChangesAsync();
        return (anterior.Id, nuevo.Id);
    }

    private async Task<int> ActualizarAsync(Tenant tenant, Guid documento, Guid? sustituto, DateTime? enUtc, int? motivo)
    {
        await using var contexto = NuevoContexto(tenant.Id);
        return await EscribirSustitucionAsync(contexto, documento, sustituto, enUtc, motivo);
    }

    private Task<PostgresException> IntentarActualizarAsync(Tenant tenant, Guid documento, Guid? sustituto, DateTime? enUtc, int? motivo) =>
        IntentarEscribirAsync(tenant, c => EscribirSustitucionAsync(c, documento, sustituto, enUtc, motivo));

    /// <summary>
    /// El UPDATE directo de las tres columnas (el agregado no deja construir las combinaciones inválidas). Parámetros
    /// tipados: un <c>null</c> sin tipo no tiene mapeo de tipo en EF.
    /// </summary>
    private static Task<int> EscribirSustitucionAsync(CaeManagerDbContext contexto, Guid documento, Guid? sustituto, DateTime? enUtc, int? motivo) =>
        contexto.Database.ExecuteSqlRawAsync(
            "UPDATE \"Documentos\" SET \"SustituidoPorDocumentoId\" = @sustituto, \"SustituidoEnUtc\" = @enUtc, \"MotivoSustitucion\" = @motivo WHERE \"Id\" = @id",
            new NpgsqlParameter("sustituto", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = (object?)sustituto ?? DBNull.Value },
            new NpgsqlParameter("enUtc", NpgsqlTypes.NpgsqlDbType.TimestampTz) { Value = (object?)enUtc ?? DBNull.Value },
            new NpgsqlParameter("motivo", NpgsqlTypes.NpgsqlDbType.Integer) { Value = (object?)motivo ?? DBNull.Value },
            new NpgsqlParameter("id", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = documento });

    private async Task<PostgresException> IntentarEscribirAsync(Tenant tenant, Func<CaeManagerDbContext, Task> escritura)
    {
        await using var contexto = NuevoContexto(tenant.Id);
        try
        {
            await escritura(contexto);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException pg)
        {
            return pg;
        }
        catch (PostgresException pg)
        {
            return pg;
        }

        throw new Xunit.Sdk.XunitException("La escritura no falló: la base de datos aceptó lo que tenía que rechazar.");
    }

    private static async Task<int> ContarDocumentosAsync(CaeManagerDbContext contexto) =>
        (await contexto.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM \"Documentos\"").ToListAsync()).Single();

    private async Task<List<string>> PoliticasDeDocumentosAsync()
    {
        await using var contexto = NuevoContexto(null);
        return await contexto.Database
            .SqlQuery<string>($"SELECT policyname || '|' || cmd || '|' || coalesce(qual, '') || '|' || coalesce(with_check, '') AS \"Value\" FROM pg_policies WHERE tablename = 'Documentos' ORDER BY 1")
            .ToListAsync();
    }

    /// <summary>null si la columna no existe; si existe, si admite nulos.</summary>
    private static async Task<bool?> Anulable(CaeManagerDbContext contexto, string columna)
    {
        var filas = await contexto.Database
            .SqlQuery<string>($"SELECT is_nullable AS \"Value\" FROM information_schema.columns WHERE table_name = 'Documentos' AND column_name = {columna}")
            .ToListAsync();
        return filas.Count == 0 ? null : filas.Single() == "YES";
    }

    private static async Task<bool> ExisteIndice(CaeManagerDbContext contexto, string nombre) =>
        (await contexto.Database
            .SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM pg_indexes WHERE indexname = {nombre}")
            .ToListAsync()).Single() > 0;

    private static async Task<bool> ExisteRestriccion(CaeManagerDbContext contexto, string nombre) =>
        (await contexto.Database
            .SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM pg_constraint WHERE conname = {nombre}")
            .ToListAsync()).Single() > 0;

    private async Task DeshacerElCambioAsync()
    {
        await using var contexto = NuevoContexto(null);
        await contexto.GetService<IMigrator>().MigrateAsync(MigracionAnterior);
    }

    private async Task MigrarAsync()
    {
        await using var contexto = NuevoContexto(null);
        await contexto.GetService<IMigrator>().MigrateAsync();
    }

    private CaeManagerDbContext NuevoContexto(Guid? tenantId)
    {
        var tenantActual = new TenantActualAmbiental { TenantId = tenantId };
        var opciones = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadena, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual), new ConcurrenciaOptimistaInterceptor())
            .Options;
        return new CaeManagerDbContext(opciones, new EphemeralDataProtectionProvider(), tenantActual);
    }
}
