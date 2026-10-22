using CaeManager.Domain.Centros;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Proyectos;
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
using NpgsqlTypes;
using Xunit;

namespace CaeManager.IntegrationTests.Migraciones;

/// <summary>
/// La migración <c>RevisaCatalogoTiposDocumentoGruposYArchivado</c> sobre una base que <b>ya tiene</b> copias del catálogo
/// en otros Tenants y configuración que pide los tipos que archiva. El catálogo del Tenant #1 llega por <c>HasData</c>; las
/// copias de cada Tenant, solo por el SQL propio de la migración, que corre con un migrador que ve todos los Tenants
/// (BYPASSRLS): por eso cada caso comprueba también que nada cruza de Tenant.
///
/// <para>
/// La base previa se siembra en el esquema anterior: las copias del catálogo, con el SQL de abajo (copian fila a fila el
/// catálogo del Tenant #1 con Id deterministas); Empresas, Centros y Proyectos, con el modelo actual (sus tablas no
/// cambian en esta migración); la configuración que apunta a tipos, con <c>INSERT</c> directo (columnas leídas del
/// modelo). Se lee siempre con SQL directo, porque en el esquema anterior el modelo actual no mapea.
/// </para>
///
/// <para>
/// Escenario. Tenant A: copia completa, un tipo propio «Reciclaje PRL» (choque de nombre con el renombrado), el Centro A1
/// con las dos variantes de RLC, el Centro A2 con «Formación EPIs» incluido, el sustituto «Entrega de EPI» excluido a
/// propósito y el protocolo de acoso (archivado sin sustituto), el Centro A3 que ya pedía «RLC», un Proyecto que pide las
/// dos variantes y el protocolo, y tolerancias del Cliente empresarial para las dos variantes. Tenant B: copia completa, un
/// tipo propio «RLC (TC1)» (choque con el renombrado de «RLC») y el Centro B1 con el recibo de RLC. Tenant C: sin catálogo
/// propio.
/// </para>
/// </summary>
public class RevisaCatalogoTiposDocumentoGruposYArchivadoTests : IAsyncLifetime
{
    private const string MigracionDelCambio = "20261022180000_RevisaCatalogoTiposDocumentoGruposYArchivado";
    private const string TenantBase = "00000000-0000-0000-0000-000000000001";

    private const string BaseReciclaje = "10000000-0000-0000-0000-000000000003";
    private const string BaseEntregaEpi = "10000000-0000-0000-0000-000000000002";
    private const string BaseRlc = "20000000-0000-0000-0000-000000000004";
    private const string BaseReciboRlc = "20000000-0000-0000-0000-000000000005";
    private const string BaseRlcCombinado = "20000000-0000-0000-0000-000000000006";
    private const string BaseFormacionEpis = "50000000-0000-0000-0000-000000000004";
    private const string BaseProtocoloAcoso = "60000000-0000-0000-0000-000000000005";

    private static readonly string[] NombresTiposNuevos =
        ["Alta en el RETA", "Plan de emergencia y evacuación", "Documento requerido por el Cliente empresarial"];

    private readonly string _cadena = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Tenant _a = new("Tenant A del catálogo revisado");
    private readonly Tenant _b = new("Tenant B del catálogo revisado");
    private readonly Tenant _c = new("Tenant C sin catálogo propio");

    private readonly Guid _propioReciclajeA = Guid.NewGuid();
    private readonly Guid _propioRlcB = Guid.NewGuid();

    private Guid _centroA1, _centroA2, _centroA3, _centroB1, _proyectoA, _clienteA, _clienteB;

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

        // Se deshace hasta la migración anterior: la base queda como en staging y producción antes de desplegarla.
        await using (var contexto = NuevoContexto(null))
            await contexto.GetService<IMigrator>().MigrateAsync(MigracionAnterior);

        await SembrarBasePreviaAsync();
    }

    public async Task DisposeAsync() => await BaseDatosPostgresDePruebas.EliminarAsync(_cadena);

    [Fact]
    public void La_migracion_del_cambio_existe_en_el_ensamblado()
    {
        using var contexto = NuevoContexto(null);
        contexto.Database.GetMigrations().Should().Contain(MigracionDelCambio);
    }

    [Fact]
    public async Task La_base_previa_tiene_las_copias_con_los_nombres_antiguos_y_sin_columnas_nuevas()
    {
        (await ExisteColumnaAsync("TiposDocumento", "Archivado")).Should().BeFalse("control: la base está en el esquema anterior");
        (await NombreAsync(await CopiaAsync(_a, BaseReciclaje))).Should().Be("Reciclaje 4h");
        (await NombreAsync(await CopiaAsync(_b, BaseRlc))).Should().Be("RLC");
        (await NombreAsync(_propioRlcB)).Should().Be("RLC (TC1)");
        (await ContarAsync("SELECT count(*) FROM \"TiposDocumento\" WHERE \"TenantId\" = @t", P("t", _a.Id)))
            .Should().Be(await ContarAsync($"SELECT count(*) FROM \"TiposDocumento\" WHERE \"TenantId\" = '{TenantBase}'") + 1,
                "el Tenant A tiene una copia de cada tipo base más su tipo propio");
        (await ContarAsync("SELECT count(*) FROM \"TiposDocumentoCentros\"")).Should().Be(8);
    }

    [Fact]
    public async Task Las_copias_se_renombran_salvo_choque_de_nombre_y_reciben_grupo_y_datos_adicionales()
    {
        await MigrarArribaAsync();

        var reciclajeA = await TipoAsync(await CopiaAsync(_a, BaseReciclaje));
        reciclajeA.Nombre.Should().Be("Reciclaje 4h", "el Tenant A ya tenía un tipo propio «Reciclaje PRL»: la copia conserva el nombre");
        reciclajeA.Grupo.Should().Be("FormacionPrl", "pero recibe igualmente el grupo");
        reciclajeA.DatosAdicionales.Should().Be(1);
        (await TipoAsync(_propioReciclajeA)).Should().BeEquivalentTo(
            new { Nombre = "Reciclaje PRL", Grupo = (string?)null, Archivado = false }, o => o.ExcludingMissingMembers(),
            "el tipo propio del Tenant no es una copia: la migración no lo toca");

        (await TipoAsync(await CopiaAsync(_b, BaseReciclaje))).Nombre.Should().Be("Reciclaje PRL", "sin choque, la copia se renombra");
        (await TipoAsync(await CopiaAsync(_a, BaseRlc))).Nombre.Should().Be("RLC (TC1)");
        var rlcB = await TipoAsync(await CopiaAsync(_b, BaseRlc));
        rlcB.Nombre.Should().Be("RLC", "el Tenant B ya tenía un tipo propio «RLC (TC1)»");
        rlcB.Grupo.Should().Be("SeguridadSocialYHacienda");
        rlcB.DatosAdicionales.Should().Be(4);

        (await TipoAsync(Guid.Parse(BaseReciclaje))).Nombre.Should().Be("Reciclaje PRL", "el Tenant #1 llega por HasData");

        foreach (var tenant in new[] { _a, _b })
            (await LeerAsync("""
                SELECT "Requerido" FROM "TiposDocumento"
                WHERE "TenantId" = @t AND "Nombre" = 'Procedimiento de Coordinación de Actividades Empresariales'
                """, l => l.GetString(0), P("t", tenant.Id)))
                .Should().Equal(["No"], "el procedimiento CAE deja de ser obligatorio también en las copias");
    }

    [Fact]
    public async Task Los_archivados_de_cada_copia_apuntan_a_un_sustituto_de_su_propio_Tenant()
    {
        await MigrarArribaAsync();

        foreach (var (tenant, sustitutoRlc) in new[] { (_a, await CopiaAsync(_a, BaseRlc)), (_b, await CopiaAsync(_b, BaseRlc)) })
        {
            foreach (var variante in new[] { BaseReciboRlc, BaseRlcCombinado })
            {
                var archivado = await TipoAsync(await CopiaAsync(tenant, variante));
                archivado.Archivado.Should().BeTrue();
                archivado.Requerido.Should().Be("No", "un archivado no se requiere de serie");
                archivado.SustitutoId.Should().Be(sustitutoRlc,
                    "el sustituto es la copia que desciende de «RLC», también cuando el choque de nombre le impidió renombrarse");
            }
        }

        (await TipoAsync(await CopiaAsync(_a, BaseFormacionEpis))).SustitutoId.Should().Be(await CopiaAsync(_a, BaseEntregaEpi));
        var protocolo = await TipoAsync(await CopiaAsync(_a, BaseProtocoloAcoso));
        protocolo.Archivado.Should().BeTrue();
        protocolo.SustitutoId.Should().BeNull("el protocolo de acoso se archiva sin sustituto");
        (await TipoAsync(_propioRlcB)).Archivado.Should().BeFalse();

        (await TipoAsync(Guid.Parse(BaseReciboRlc))).SustitutoId.Should().Be(Guid.Parse(BaseRlc), "Tenant #1, por HasData");
    }

    [Fact]
    public async Task Las_dos_variantes_de_RLC_del_mismo_Centro_se_agrupan_en_una_sola_fila_del_sustituto()
    {
        await MigrarArribaAsync();

        var rlc = await CopiaAsync(_a, BaseRlc);
        var recibo = await CopiaAsync(_a, BaseReciboRlc);
        var combinado = await CopiaAsync(_a, BaseRlcCombinado);

        var filas = await FilasCentroAsync(_centroA1);
        filas.Should().HaveCount(3, "las dos variantes y una sola fila nueva del sustituto");
        filas.Single(f => f.Tipo == rlc).Should().Be(
            new FilaCentro(rlc, true, true, 3, 5, "/plantillas/rlc.pdf", "rlc.pdf"),
            "se crea con los valores de la variante que bloquea el acceso, y bloquea si alguna bloqueaba");
        filas.Single(f => f.Tipo == recibo).Should().Be(new FilaCentro(recibo, false, false, 6, 10, null, null));
        filas.Single(f => f.Tipo == combinado).Should().Be(
            new FilaCentro(combinado, false, false, 3, 5, "/plantillas/rlc.pdf", "rlc.pdf"),
            "el archivado deja de incluirse y de bloquear; el resto de su fila se conserva");
    }

    [Fact]
    public async Task Un_Centro_que_ya_pedia_el_sustituto_conserva_su_periodicidad_y_suma_el_bloqueo_y_la_plantilla()
    {
        await MigrarArribaAsync();

        var rlc = await CopiaAsync(_a, BaseRlc);

        var filas = await FilasCentroAsync(_centroA3);
        filas.Should().HaveCount(2);
        filas.Single(f => f.Tipo == rlc).Should().Be(
            new FilaCentro(rlc, true, true, 12, 0, "/plantillas/recibo.pdf", "recibo.pdf"));
    }

    [Fact]
    public async Task La_exclusion_explicita_del_sustituto_se_respeta_y_el_archivado_sin_sustituto_solo_deja_de_pedirse()
    {
        await MigrarArribaAsync();

        var entregaEpi = await CopiaAsync(_a, BaseEntregaEpi);

        var filas = await FilasCentroAsync(_centroA2);
        filas.Should().HaveCount(3, "no se crea ninguna fila: el sustituto ya estaba excluido y el protocolo no tiene sustituto");
        filas.Single(f => f.Tipo == entregaEpi).Should().Be(new FilaCentro(entregaEpi, false, false, null, null, null, null),
            "el Centro excluyó «Entrega de EPI» a propósito: el traslado no la vuelve a incluir ni le pasa bloqueo ni plantilla");
        filas.Should().OnlyContain(f => !f.Incluido && !f.BloqueaAcceso, "ningún archivado se sigue pidiendo ni bloquea");
    }

    [Fact]
    public async Task Requisitos_de_Proyecto_y_tolerancias_pasan_al_sustituto_sin_duplicar()
    {
        await MigrarArribaAsync();

        (await LeerAsync("SELECT \"TipoDocumentoId\" FROM \"ProyectosRequisitos\" WHERE \"ProyectoId\" = @p",
                l => l.GetGuid(0), P("p", _proyectoA)))
            .Should().BeEquivalentTo([await CopiaAsync(_a, BaseRlc), await CopiaAsync(_a, BaseProtocoloAcoso)],
                "las dos variantes pasan a un solo requisito del sustituto; el archivado sin sustituto se queda");

        (await ToleranciasAsync(_clienteA)).Should().BeEquivalentTo(new Dictionary<Guid, int>
        {
            [await CopiaAsync(_a, BaseRlc)] = 10,
            [await CopiaAsync(_a, BaseReciboRlc)] = 20,
            [await CopiaAsync(_a, BaseRlcCombinado)] = 10,
        }, "el sustituto recibe la menor de las de sus archivados, que se conservan");
        (await ToleranciasAsync(_clienteB)).Should().BeEquivalentTo(new Dictionary<Guid, int>
        {
            [await CopiaAsync(_b, BaseRlc)] = 7,
            [await CopiaAsync(_b, BaseReciboRlc)] = 7,
        });
    }

    [Fact]
    public async Task Nada_cruza_de_Tenant()
    {
        await MigrarArribaAsync();

        // B1: la fila nueva es del sustituto del Tenant B (su copia de «RLC»), no de A ni del tipo propio homónimo.
        (await FilasCentroAsync(_centroB1)).Should().BeEquivalentTo([
            new FilaCentro(await CopiaAsync(_b, BaseReciboRlc), false, false, 6, 10, null, null),
            new FilaCentro(await CopiaAsync(_b, BaseRlc), true, false, 6, 10, null, null),
        ]);

        foreach (var tenant in new[] { _a, _b })
            (await LeerAsync("SELECT \"Nombre\" FROM \"TiposDocumento\" WHERE \"TenantId\" = @t AND \"Nombre\" = ANY(@n)",
                    l => l.GetString(0), P("t", tenant.Id), new NpgsqlParameter("n", NombresTiposNuevos)))
                .Should().BeEquivalentTo(NombresTiposNuevos, "cada Tenant con catálogo propio recibe los tipos nuevos una vez");
        (await ContarAsync("SELECT count(*) FROM \"TiposDocumento\" WHERE \"TenantId\" = @t", P("t", _c.Id)))
            .Should().Be(0, "un Tenant sin catálogo propio no recibe nada");

        // Toda referencia a un tipo apunta a un tipo de su mismo Tenant (Tolerancias no tiene FK que lo garantice).
        foreach (var (tabla, columna) in new[]
                 {
                     ("TiposDocumentoCentros", "TipoDocumentoId"), ("ProyectosRequisitos", "TipoDocumentoId"),
                     ("ToleranciasDocumentoClienteEmpresarial", "TipoDocumentoId"), ("TiposDocumentoAlias", "TipoDocumentoId"),
                     ("TiposDocumento", "SustitutoId"),
                 })
        {
            (await ContarAsync($"SELECT count(*) FROM \"{tabla}\" x WHERE x.\"{columna}\" IS NOT NULL"))
                .Should().BeGreaterThan(0, $"control: {tabla} tiene filas que mirar");
            (await ContarAsync($"""
                SELECT count(*) FROM "{tabla}" x JOIN "TiposDocumento" t ON t."Id" = x."{columna}"
                WHERE t."TenantId" <> x."TenantId"
                """)).Should().Be(0, $"{tabla}.{columna} no apunta a tipos de otro Tenant");
        }

        (await ContarAsync("""
            SELECT count(*) FROM "TiposDocumentoAlias" al
            WHERE al."TenantId" <> @t AND al."Id" = md5('alias-2026-10-17:' || al."TipoDocumentoId"::text || ':' || al."Texto")::uuid
            """, P("t", Guid.Parse(TenantBase)))).Should().BeGreaterThan(0, "control: la migración insertó alias en las copias");
    }

    [Fact]
    public async Task El_Down_restaura_catalogo_y_alias_y_conserva_la_configuracion_trasladada()
    {
        var tiposAntes = await CatalogoAsync();
        var aliasAntes = await AliasAsync();

        await using (var contexto = NuevoContexto(null))
        {
            var migrador = contexto.GetService<IMigrator>();
            await migrador.MigrateAsync();
            await migrador.MigrateAsync(MigracionAnterior);
        }

        (await ExisteColumnaAsync("TiposDocumento", "SustitutoId")).Should().BeFalse();
        (await ExisteColumnaAsync("Documentos", "EsRenuncia")).Should().BeFalse();
        (await CatalogoAsync()).Should().BeEquivalentTo(tiposAntes,
            "nombres, obligatoriedad y número de tipos vuelven a como estaban, en el Tenant #1 y en las copias");
        (await AliasAsync()).Should().BeEquivalentTo(aliasAntes, "solo se borran los alias que insertó el Up");

        // B': el traslado al sustituto no se deshace (el sustituto ya existía antes de la migración).
        var rlc = await CopiaAsync(_a, BaseRlc);
        (await FilasCentroAsync(_centroA1)).Should().Contain(f => f.Tipo == rlc && f.Incluido);
    }

    // ── Arnés ──────────────────────────────────────────────────────────────

    private sealed record FilaCentro(
        Guid Tipo, bool Incluido, bool BloqueaAcceso, int? Periodicidad, int? Tolerancia, string? Archivo, string? NombreArchivo);

    private sealed record FilaTipo(string Nombre, string Requerido, string? Grupo, int DatosAdicionales, bool Archivado, Guid? SustitutoId);

    private readonly Dictionary<(Guid, string), Guid> _copias = [];

    private async Task SembrarBasePreviaAsync()
    {
        await using (var contexto = NuevoContexto(null))
        {
            contexto.Tenants.AddRange(_a, _b, _c);
            await contexto.SaveChangesAsync();
        }

        foreach (var tenant in new[] { _a, _b })
        {
            await EjecutarAsync($"""
                INSERT INTO "TiposDocumento"
                SELECT (jsonb_populate_record(NULL::"TiposDocumento", to_jsonb(b) || jsonb_build_object(
                    'Id', md5('copia-test:' || @t::text || ':' || b."Id"::text)::uuid, 'TenantId', @t))).*
                FROM "TiposDocumento" b WHERE b."TenantId" = '{TenantBase}';

                INSERT INTO "TiposDocumentoAlias" ("Id", "TenantId", "Texto", "TipoDocumentoId")
                SELECT md5('copia-alias-test:' || @t::text || ':' || a."Id"::text)::uuid, @t, a."Texto",
                       md5('copia-test:' || @t::text || ':' || a."TipoDocumentoId"::text)::uuid
                FROM "TiposDocumentoAlias" a WHERE a."TenantId" = '{TenantBase}';
                """, P("t", tenant.Id));
        }

        await InsertarTipoPropioAsync(_a, _propioReciclajeA, BaseReciclaje, "Reciclaje PRL");
        await InsertarTipoPropioAsync(_b, _propioRlcB, BaseRlc, "RLC (TC1)");

        (_clienteA, _centroA1, _centroA2, _centroA3, _proyectoA) = await SembrarEmpresasYCentrosAsync(_a, 1);
        (_clienteB, _centroB1, _, _, _) = await SembrarEmpresasYCentrosAsync(_b, 2);

        await InsertarFilaCentroAsync(_a, _centroA1, BaseReciboRlc, true, false, 6, 10, null, null);
        await InsertarFilaCentroAsync(_a, _centroA1, BaseRlcCombinado, true, true, 3, 5, "/plantillas/rlc.pdf", "rlc.pdf");
        await InsertarFilaCentroAsync(_a, _centroA2, BaseFormacionEpis, true, true, null, null, null, null);
        await InsertarFilaCentroAsync(_a, _centroA2, BaseEntregaEpi, false, false, null, null, null, null);
        await InsertarFilaCentroAsync(_a, _centroA2, BaseProtocoloAcoso, true, true, null, null, null, null);
        await InsertarFilaCentroAsync(_a, _centroA3, BaseRlc, true, false, 12, 0, null, null);
        await InsertarFilaCentroAsync(_a, _centroA3, BaseReciboRlc, true, true, 1, 30, "/plantillas/recibo.pdf", "recibo.pdf");
        await InsertarFilaCentroAsync(_b, _centroB1, BaseReciboRlc, true, false, 6, 10, null, null);

        foreach (var variante in new[] { BaseReciboRlc, BaseRlcCombinado, BaseProtocoloAcoso })
            await EjecutarAsync("""
                INSERT INTO "ProyectosRequisitos" ("Id", "TenantId", "ProyectoId", "TipoDocumentoId") VALUES (@id, @t, @p, @tipo)
                """, P("id", Guid.NewGuid()), P("t", _a.Id), P("p", _proyectoA), P("tipo", await CopiaAsync(_a, variante)));

        await InsertarToleranciaAsync(_a, _clienteA, BaseReciboRlc, 20);
        await InsertarToleranciaAsync(_a, _clienteA, BaseRlcCombinado, 10);
        await InsertarToleranciaAsync(_b, _clienteB, BaseReciboRlc, 7);
    }

    private async Task<(Guid Cliente, Guid Centro1, Guid Centro2, Guid Centro3, Guid Proyecto)> SembrarEmpresasYCentrosAsync(Tenant tenant, int n)
    {
        await using var contexto = NuevoContexto(tenant.Id);
        var cliente = Empresa.CrearComoCliente($"Cliente del catálogo {n}", DatosPruebaSeeder.GenerarCifValido(8_810_000 + n), false, null, null);
        var propia = new Empresa($"Empresa propia del catálogo {n}", DatosPruebaSeeder.GenerarCifValido(8_820_000 + n));
        contexto.Empresas.AddRange(cliente, propia);
        await contexto.SaveChangesAsync();

        var centros = Enumerable.Range(1, 3).Select(i => new Centro(cliente.Id, propia.Id, $"Centro {n}.{i}")).ToList();
        contexto.Centros.AddRange(centros);
        await contexto.SaveChangesAsync();

        var proyecto = Proyecto.Crear(cliente.Id, centros[0].Id, $"Obra del catálogo {n}", new DateOnly(2026, 10, 1), null, null);
        contexto.Proyectos.Add(proyecto);
        await contexto.SaveChangesAsync();
        return (cliente.Id, centros[0].Id, centros[1].Id, centros[2].Id, proyecto.Id);
    }

    private Task InsertarTipoPropioAsync(Tenant tenant, Guid id, string baseId, string nombre) =>
        EjecutarAsync("""
            INSERT INTO "TiposDocumento"
            SELECT (jsonb_populate_record(NULL::"TiposDocumento", to_jsonb(b) || jsonb_build_object(
                'Id', @id, 'TenantId', @t, 'Nombre', @nombre, 'Orden', 900))).*
            FROM "TiposDocumento" b WHERE b."Id" = @base
            """, P("id", id), P("t", tenant.Id), new NpgsqlParameter("nombre", nombre), P("base", Guid.Parse(baseId)));

    private async Task InsertarFilaCentroAsync(
        Tenant tenant, Guid centro, string baseId, bool incluido, bool bloquea, int? periodicidad, int? tolerancia, string? archivo, string? nombreArchivo) =>
        await EjecutarAsync("""
            INSERT INTO "TiposDocumentoCentros" ("Id", "TenantId", "TipoDocumentoId", "CentroId", "Incluido", "PeriodicidadEspecialMeses",
                "BloqueaAcceso", "ToleranciaDias", "ArchivoUrl", "NombreArchivoOriginal")
            VALUES (@id, @t, @tipo, @c, @incluido, @periodicidad, @bloquea, @tolerancia, @archivo, @nombreArchivo)
            """,
            P("id", Guid.NewGuid()), P("t", tenant.Id), P("tipo", await CopiaAsync(tenant, baseId)), P("c", centro),
            new NpgsqlParameter("incluido", incluido), new NpgsqlParameter("bloquea", bloquea),
            new NpgsqlParameter("periodicidad", NpgsqlDbType.Integer) { Value = (object?)periodicidad ?? DBNull.Value },
            new NpgsqlParameter("tolerancia", NpgsqlDbType.Integer) { Value = (object?)tolerancia ?? DBNull.Value },
            new NpgsqlParameter("archivo", NpgsqlDbType.Varchar) { Value = (object?)archivo ?? DBNull.Value },
            new NpgsqlParameter("nombreArchivo", NpgsqlDbType.Varchar) { Value = (object?)nombreArchivo ?? DBNull.Value });

    private async Task InsertarToleranciaAsync(Tenant tenant, Guid cliente, string baseId, int dias) =>
        await EjecutarAsync("""
            INSERT INTO "ToleranciasDocumentoClienteEmpresarial" ("Id", "TenantId", "ClienteEmpresarialId", "TipoDocumentoId", "ToleranciaDias")
            VALUES (@id, @t, @cliente, @tipo, @dias)
            """, P("id", Guid.NewGuid()), P("t", tenant.Id), P("cliente", cliente), P("tipo", await CopiaAsync(tenant, baseId)),
            new NpgsqlParameter("dias", dias));

    private async Task MigrarArribaAsync()
    {
        await using var contexto = NuevoContexto(null);
        await contexto.GetService<IMigrator>().MigrateAsync();
    }

    /// <summary>Id de la copia de un tipo base en el Tenant (la misma fórmula que el SQL de siembra).</summary>
    private async Task<Guid> CopiaAsync(Tenant tenant, string baseId)
    {
        if (_copias.TryGetValue((tenant.Id, baseId), out var id))
            return id;
        id = (await LeerAsync("SELECT md5('copia-test:' || @t::text || ':' || @b)::uuid", l => l.GetGuid(0),
            P("t", tenant.Id), new NpgsqlParameter("b", baseId))).Single();
        _copias[(tenant.Id, baseId)] = id;
        return id;
    }

    private async Task<string> NombreAsync(Guid tipo) =>
        (await LeerAsync("SELECT \"Nombre\" FROM \"TiposDocumento\" WHERE \"Id\" = @id", l => l.GetString(0), P("id", tipo))).Single();

    private async Task<FilaTipo> TipoAsync(Guid tipo) =>
        (await LeerAsync("""
            SELECT "Nombre", "Requerido", "Grupo", "DatosAdicionales", "Archivado", "SustitutoId" FROM "TiposDocumento" WHERE "Id" = @id
            """,
            l => new FilaTipo(l.GetString(0), l.GetString(1), l.IsDBNull(2) ? null : l.GetString(2), l.GetInt32(3), l.GetBoolean(4),
                l.IsDBNull(5) ? null : l.GetGuid(5)),
            P("id", tipo))).Single();

    private Task<List<FilaCentro>> FilasCentroAsync(Guid centro) =>
        LeerAsync("""
            SELECT "TipoDocumentoId", "Incluido", "BloqueaAcceso", "PeriodicidadEspecialMeses", "ToleranciaDias", "ArchivoUrl", "NombreArchivoOriginal"
            FROM "TiposDocumentoCentros" WHERE "CentroId" = @c
            """,
            l => new FilaCentro(l.GetGuid(0), l.GetBoolean(1), l.GetBoolean(2), l.IsDBNull(3) ? null : l.GetInt32(3),
                l.IsDBNull(4) ? null : l.GetInt32(4), l.IsDBNull(5) ? null : l.GetString(5), l.IsDBNull(6) ? null : l.GetString(6)),
            P("c", centro));

    private async Task<Dictionary<Guid, int>> ToleranciasAsync(Guid cliente) =>
        (await LeerAsync("""
            SELECT "TipoDocumentoId", "ToleranciaDias" FROM "ToleranciasDocumentoClienteEmpresarial" WHERE "ClienteEmpresarialId" = @c
            """, l => (Tipo: l.GetGuid(0), Dias: l.GetInt32(1)), P("c", cliente)))
        .ToDictionary(x => x.Tipo, x => x.Dias);

    private Task<List<string>> CatalogoAsync() =>
        LeerAsync("""
            SELECT "Id"::text || '|' || "TenantId"::text || '|' || "AmbitoAplicacion" || '|' || "Nombre" || '|' || "Requerido"
            FROM "TiposDocumento"
            """, l => l.GetString(0));

    private Task<List<string>> AliasAsync() =>
        LeerAsync("""
            SELECT "Id"::text || '|' || "TenantId"::text || '|' || "TipoDocumentoId"::text || '|' || "Texto" FROM "TiposDocumentoAlias"
            """, l => l.GetString(0));

    private async Task<bool> ExisteColumnaAsync(string tabla, string columna) =>
        await ContarAsync("SELECT count(*) FROM information_schema.columns WHERE table_name = @tabla AND column_name = @columna",
            new NpgsqlParameter("tabla", tabla), new NpgsqlParameter("columna", columna)) > 0;

    private async Task<long> ContarAsync(string sql, params NpgsqlParameter[] parametros) =>
        (await LeerAsync(sql, l => l.GetInt64(0), parametros)).Single();

    private static NpgsqlParameter P(string nombre, Guid valor) => new(nombre, NpgsqlDbType.Uuid) { Value = valor };

    private async Task EjecutarAsync(string sql, params NpgsqlParameter[] parametros)
    {
        await using var conexion = new NpgsqlConnection(_cadena);
        await conexion.OpenAsync();
        await using var comando = new NpgsqlCommand(sql, conexion);
        comando.Parameters.AddRange(parametros);
        await comando.ExecuteNonQueryAsync();
    }

    private async Task<List<T>> LeerAsync<T>(string sql, Func<NpgsqlDataReader, T> fila, params NpgsqlParameter[] parametros)
    {
        await using var conexion = new NpgsqlConnection(_cadena);
        await conexion.OpenAsync();
        await using var comando = new NpgsqlCommand(sql, conexion);
        comando.Parameters.AddRange(parametros);
        await using var lector = await comando.ExecuteReaderAsync();
        var filas = new List<T>();
        while (await lector.ReadAsync())
            filas.Add(fila(lector));
        return filas;
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
