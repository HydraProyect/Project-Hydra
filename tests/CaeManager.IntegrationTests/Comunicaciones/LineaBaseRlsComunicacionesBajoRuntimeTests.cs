using System.Security.Claims;
using System.Text.RegularExpressions;
using CaeManager.Application.Common;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Domain.Integraciones;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.ContextoRls;
using CaeManager.Infrastructure.Persistence.Interceptors;
using CaeManager.IntegrationTests.Tenants;
using CaeManager.Web.Services;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace CaeManager.IntegrationTests.Comunicaciones;

/// <summary>
/// <b>Línea base RLS de las tablas de Comunicaciones que leerá la vista unificada D-6 b</b> (pruebas P-1 a P-10 del
/// diseño, PR R-0). Sin cambio de producción: fija lo que el esquema de hoy garantiza para que las PR siguientes no lo
/// rompan. Con la alternativa (a) del diseño, la RLS garantiza el aislamiento por el Tenant del contexto firmado y la
/// ausencia de ensanchamiento; qué Tenants puede operar cada persona lo decide Application y no se prueba aquí.
///
/// <para>
/// Escenario: tres Tenants propietarios ficticios, Construcciones Ebro, Montajes Norte y un Tenant de plataforma
/// (<c>EsPlataforma</c>) con Delegación de Soporte sobre los otros dos. Cada uno tiene una fila en cada tabla de
/// <see cref="Tablas"/>, sembrada con el rol propietario de las tablas (sin RLS). Las lecturas van por
/// <c>cae_app_runtime</c> de login real y solo cuentan filas del escenario (la base puede traer otras). Cada negativo
/// lleva su control positivo: la fila existe y el mismo camino ve la del Tenant del contexto, así que un cero es la
/// política y no una fila ausente. P-5 vive en <see cref="LineaBaseRlsComunicacionesPoolBajoRuntimeTests"/>.
/// </para>
///
/// <para>
/// P-11 no tiene prueba nueva: <c>CoberturaRlsDelModeloTests</c> ya pone en rojo una RESTRICTIVE sin revisar en una
/// tabla del modelo con <c>TenantId</c> (<c>conPoliticasDeMas</c>) y busca <c>OR</c> en las revisadas
/// (<c>restrictivasQueNoDicenLoQueToca</c>). Hueco: no mira tablas fuera del modelo EF; la que importa aquí,
/// <c>ContadoresTicketConversacion</c>, la revisa P-8. Las falsaciones de cada prueba salen de leer el esquema y el
/// código: no se han ejecutado.
/// </para>
/// </summary>
public class LineaBaseRlsComunicacionesBajoRuntimeTests : IAsyncLifetime
{
    private static readonly string[] Tablas =
    [
        "Conversaciones", "Mensajes", "ParticipantesConversacion", "AdjuntosMensaje",
        "NotasInternasConversacion", "EventosConversacion", "ConexionesIntegracion",
    ];

    /// <summary>Lista cerrada de políticas, además de <c>aislamiento_tenant</c>, revisadas en estas tablas. Vacía hoy:
    /// una PERMISSIVE nueva se combina con OR y ensancha la lectura, así que entra aquí con motivo o pone P-8 en rojo.</summary>
    private static readonly Dictionary<string, string> PoliticasAdicionalesRevisadas = new();

    /// <summary>Funciones <c>SECURITY DEFINER</c> ejecutables por los roles de tráfico que leen estas tablas, con
    /// motivo. Vacía hoy (P-9).</summary>
    private static readonly Dictionary<string, string> FuncionesDefinerRevisadas = new();

    private readonly string _cadena = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Dictionary<Guid, Dictionary<string, Guid>> _filas = [];
    private Guid _ebro, _norte, _plataforma;

    public async Task InitializeAsync()
    {
        var ebro = new Tenant($"Construcciones Ebro {Guid.NewGuid():N}");
        var norte = new Tenant($"Montajes Norte {Guid.NewGuid():N}");
        var plataforma = new Tenant($"Plataforma TALVEG {Guid.NewGuid():N}", esPlataforma: true);
        (_ebro, _norte, _plataforma) = (ebro.Id, norte.Id, plataforma.Id);

        await using (var siembra = ContextoPropietario(_plataforma))
        {
            siembra.Tenants.AddRange(ebro, norte, plataforma);
            await siembra.SaveChangesAsync();
            siembra.DelegacionesTenant.AddRange(
                DelegacionTenant.ParaSoporte(_plataforma, _ebro), DelegacionTenant.ParaSoporte(_plataforma, _norte));
            await siembra.SaveChangesAsync();
        }

        foreach (var tenant in new[] { _ebro, _norte, _plataforma })
        {
            await using var siembra = ContextoPropietario(tenant);
            var conexion = new ConexionIntegracion($"buzon.{Guid.NewGuid():N}@d6b.test", "Buzón compartido");
            var conversacion = new Conversacion("Hilo de la línea base");
            var mensaje = conversacion.AgregarMensaje(DireccionMensaje.Entrante, CanalConversacion.Correo, "contacto@d6b.test", "<p>Cuerpo</p>");
            var adjunto = mensaje.AgregarAdjunto("acta.pdf", "application/pdf", 1024, "blob://acta");
            var participante = conversacion.AgregarParticipante("contacto@d6b.test", RolParticipante.Para, TipoParticipanteOrigen.Desconocido);
            siembra.ConexionesIntegracion.Add(conexion);
            siembra.Conversaciones.Add(conversacion);
            await siembra.SaveChangesAsync();

            var nota = new NotaInternaConversacion(conversacion.Id, Guid.NewGuid(), "Nota del equipo.", DateTime.UtcNow);
            var evento = new EventoConversacion(conversacion.Id, TipoEventoConversacion.VisitaCreada, Guid.NewGuid(), DateTime.UtcNow);
            siembra.NotasInternasConversacion.Add(nota);
            siembra.EventosConversacion.Add(evento);
            await siembra.SaveChangesAsync();

            _filas[tenant] = new()
            {
                ["Conversaciones"] = conversacion.Id,
                ["Mensajes"] = mensaje.Id,
                ["ParticipantesConversacion"] = participante.Id,
                ["AdjuntosMensaje"] = adjunto.Id,
                ["NotasInternasConversacion"] = nota.Id,
                ["EventosConversacion"] = evento.Id,
                ["ConexionesIntegracion"] = conexion.Id,
            };
        }

        // Barrera del escenario: cada fila existe para el rol propietario de las tablas.
        await using var propietaria = await AbrirAsync(_cadena);
        foreach (var tabla in Tablas)
            (await IdsAsync(propietaria, tabla, TodasLasFilas(tabla))).Should().HaveCount(3, $"barrera: {tabla} tiene las tres filas");
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadena);

    /// <summary>P-1. Falsación: una PERMISSIVE de prueba <c>USING (true)</c> en cualquiera de las tablas, o el USING de
    /// <c>aislamiento_tenant</c> reescrito a <c>true</c>, deja ver la fila de Montajes Norte.</summary>
    [Fact]
    public async Task P1_con_el_contexto_de_un_Tenant_propietario_cada_tabla_devuelve_solo_sus_filas()
    {
        await using var conexion = await AbrirComoRuntimeAsync(tenant: _ebro);
        using var _ = new AssertionScope();
        foreach (var tabla in Tablas)
        {
            (await IdsAsync(conexion, tabla, TodasLasFilas(tabla))).Should().Equal([_filas[_ebro][tabla]], $"{tabla}: solo la fila de Ebro");
            (await IdsAsync(conexion, tabla, [_filas[_norte][tabla]])).Should().BeEmpty($"{tabla}: ni pidiendo el Id de Norte");
        }
    }

    /// <summary>P-2. Falsación: el <c>WITH CHECK</c> de <c>aislamiento_tenant</c> a <c>true</c> deja insertar y mover
    /// filas a Montajes Norte. (Quitarlo sin más no basta: PostgreSQL usa el USING como WITH CHECK.) La inserción en
    /// <c>Conversaciones</c> la para antes el contador de tickets, cuya política también es por Tenant; por eso su
    /// <c>WITH CHECK</c> propio se demuestra con el UPDATE. El runtime tiene INSERT y UPDATE (barrera de catálogo), así
    /// que el 42501 es de la RLS y no de un privilegio, sin depender del idioma del mensaje.</summary>
    [Fact]
    public async Task P2_con_el_contexto_de_un_Tenant_propietario_no_se_escribe_una_fila_de_otro()
    {
        await using var conexion = await AbrirComoRuntimeAsync(tenant: _ebro);
        foreach (var tabla in Tablas)
        {
            (await EscalarAsync(conexion, "SELECT has_table_privilege(current_user, @t, 'INSERT') AND has_table_privilege(current_user, @t, 'UPDATE')",
                ("t", $"public.\"{tabla}\""))).Should().Be(true, $"barrera: el runtime escribe en {tabla}");
            var propia = _filas[_ebro][tabla];
            await DebeRechazarAsync(() => CopiarAsync(conexion, tabla, propia, _norte), null);
            await DebeRechazarAsync(() => EjecutarAsync(conexion, $"""UPDATE "{tabla}" SET "TenantId" = @t WHERE "Id" = @id""", ("t", _norte), ("id", propia)), null);

            // Controles positivos: con su propio Tenant la copia pasa la RLS (entra, o la para después un índice
            // único, 23505), y el runtime ve y puede actualizar su fila.
            await using (var transaccion = await conexion.BeginTransactionAsync())
            {
                var error = await Record.ExceptionAsync(() => CopiarAsync(conexion, tabla, propia, _ebro));
                (error is null || error is PostgresException { SqlState: "23505" }).Should().BeTrue($"{tabla}: {error?.Message}");
                await transaccion.RollbackAsync();
            }
            (await EjecutarAsync(conexion, $"""UPDATE "{tabla}" SET "TenantId" = "TenantId" WHERE "Id" = @id""", ("id", propia))).Should().Be(1);
        }
    }

    /// <summary>P-3. Falsación: <c>aislamiento_tenant</c> reescrita a <c>current_setting('app.tenant_id')</c> devuelve
    /// la fila de Montajes Norte en la primera conexión y la ve también sin token.</summary>
    [Fact]
    public async Task P3_una_GUC_suelta_de_otro_Tenant_no_abre_ninguna_fila()
    {
        await using var conToken = await AbrirComoRuntimeAsync(tenant: _ebro);
        await using var sinToken = await AbrirComoRuntimeAsync(tenant: null);
        foreach (var conexion in new[] { conToken, sinToken })
            foreach (var guc in new[] { "app.tenant_id", "app.tenant_origen_id" })
                await EjecutarAsync(conexion, "SELECT set_config(@g, @v, false)", ("g", guc), ("v", _norte.ToString()));

        using var _ = new AssertionScope();
        foreach (var tabla in Tablas)
        {
            (await IdsAsync(conToken, tabla, TodasLasFilas(tabla))).Should().Equal([_filas[_ebro][tabla]], $"{tabla}: manda el token firmado");
            (await IdsAsync(sinToken, tabla, TodasLasFilas(tabla))).Should().BeEmpty($"{tabla}: sin token la GUC no es autoridad");
        }
    }

    /// <summary>P-4. Falsación: un <c>app_contexto_validado</c> que no comprobara la firma o la caducidad devolvería
    /// filas de Montajes Norte (token alterado) o de Ebro (token caducado) en vez de 42501.</summary>
    [Fact]
    public async Task P4_un_token_alterado_o_caducado_falla_con_42501_en_cada_tabla()
    {
        await using var conexion = await AbrirComoRuntimeAsync(tenant: null);
        var valido = await ContextoRlsDePruebas.FirmarAsync(conexion, tenantId: _ebro);
        var alterado = valido.Replace(_ebro.ToString("D"), _norte.ToString("D"), StringComparison.Ordinal);
        alterado.Should().NotBe(valido, "barrera: el token lleva el Tenant en claro");
        var atrasado = new FirmanteContextoRls(BaseDatosPostgresDePruebas.CadenaDeMantenimientoSinPool(), BaseDatosPostgresDePruebas.ProteccionDePruebas,
            new RelojFijo(DateTimeOffset.UtcNow.AddHours(-2)), FirmanteContextoRls.TtlPorDefecto, FirmanteContextoRls.RotacionPorDefecto);
        var caducado = (await atrasado.FirmarAsync(conexion, new ContextoSesionRls(_ebro, _ebro, null, OrigenContextoRls.Ambito), CancellationToken.None)).Token;

        foreach (var (token, motivo) in new[] { (alterado, "firma inválida"), (caducado, "caducado") })
        {
            await FijarTokenAsync(conexion, token);
            foreach (var tabla in Tablas)
                await DebeRechazarAsync(() => IdsAsync(conexion, tabla, TodasLasFilas(tabla)), motivo);
        }

        await FijarTokenAsync(conexion, valido);
        foreach (var tabla in Tablas)
            (await IdsAsync(conexion, tabla, TodasLasFilas(tabla))).Should().Equal([_filas[_ebro][tabla]], "control positivo: el token válido sí lee");
    }

    /// <summary>P-5. Recorrido de Tenants en una conexión física retenida, con el interceptor de producción y fuera de
    /// transacción. Falsación: <c>AlinearSiCambioAsync</c> que vuelva sin aplicar (o que no recalcule la sesión deseada)
    /// deja la conexión firmada con Ebro y la segunda lectura devuelve filas de Ebro; la cadena con
    /// <c>No Reset On Close</c> deja el contexto de Norte a la apertura siguiente. El TenantId sale de la columna.</summary>
    internal async Task P5Async()
    {
        var cadena = new NpgsqlConnectionStringBuilder(BaseDatosPostgresDePruebas.CadenaComoRuntime(_cadena))
        { ApplicationName = $"d6b-p5-{Guid.NewGuid():N}", MaxPoolSize = 1 }.ConnectionString;
        var tenant = new TenantMutable { TenantId = _ebro };
        int pid;
        await using (var contexto = ContextoRuntime(cadena, tenant, new Seleccion(null, null), _ebro))
        {
            await contexto.Database.OpenConnectionAsync();
            pid = ((NpgsqlConnection)contexto.Database.GetDbConnection()).ProcessID;
            contexto.Database.CurrentTransaction.Should().BeNull("se prueba la realineación fuera de transacción");

            (await TenantsLeidosAsync(contexto, Tablas)).Should().Equal([_ebro], "control positivo con el contexto de Ebro");
            tenant.TenantId = _norte;
            (await TenantsLeidosAsync(contexto, Tablas)).Should().Equal([_norte], "la misma conexión, realineada, solo ve Norte");
            ((NpgsqlConnection)contexto.Database.GetDbConnection()).ProcessID.Should().Be(pid, "barrera: misma conexión física");
            await contexto.Database.CloseConnectionAsync();
        }

        await using var reutilizada = await AbrirAsync(cadena);
        reutilizada.ProcessID.Should().Be(pid, "barrera: el mismo backend sacado del pool");
        foreach (var tabla in Tablas)
            (await IdsAsync(reutilizada, tabla, TodasLasFilas(tabla))).Should().BeEmpty($"{tabla}: la apertura sin Tenant no hereda nada");
    }

    /// <summary>P-6, control positivo. Una Sesión Privilegiada sobre Ebro, por el interceptor y el <see cref="TenantActual"/>
    /// reales, adopta <c>cae_app_soporte</c>, lee solo Ebro y no escribe: 42501 en INSERT, UPDATE y DELETE, y el catálogo
    /// confirma que es por privilegio, de tabla o de columna, y no por política. El INSERT es una copia válida de Ebro:
    /// con privilegio entraría. Falsación: <c>GRANT INSERT</c> (o UPDATE, de tabla o por columna, o DELETE) a
    /// <c>cae_app_soporte</c> sobre <c>Conversaciones</c> o <c>Mensajes</c>.</summary>
    [Fact]
    public async Task P6_una_Sesion_Privilegiada_sobre_un_Tenant_lee_solo_ese_Tenant_y_no_escribe()
    {
        await using var contexto = ContextoDeSesionPrivilegiada(objetivo: _ebro);
        (await contexto.Database.SqlQueryRaw<string>("""SELECT current_user::text AS "Value" """).ToListAsync()).Should().Equal(["cae_app_soporte"]);
        var legibles = Tablas.Where(t => t != "NotasInternasConversacion").ToArray(); // Soporte TALVEG no tiene privilegio sobre notas
        (await TenantsLeidosAsync(contexto, legibles)).Should().Equal([_ebro]);

        foreach (var tabla in new[] { "Conversaciones", "Mensajes" })
        {
            await DebeRechazarAsync(() => contexto.Database.ExecuteSqlRawAsync(SqlCopia(tabla),
                new NpgsqlParameter("nuevo", Guid.NewGuid()), new NpgsqlParameter("tenant", _ebro), new NpgsqlParameter("origen", _filas[_ebro][tabla])), null);
            foreach (var orden in new[] { "UPDATE \"{0}\" SET \"TenantId\" = \"TenantId\" WHERE \"Id\" = @id", "DELETE FROM \"{0}\" WHERE \"Id\" = @id" })
                await DebeRechazarAsync(() => contexto.Database.ExecuteSqlRawAsync(string.Format(orden, tabla), new NpgsqlParameter("id", _filas[_ebro][tabla])), null);
            (await contexto.Database.SqlQueryRaw<bool>("""
                SELECT has_any_column_privilege(current_user, {0}, 'INSERT') OR has_any_column_privilege(current_user, {0}, 'UPDATE')
                    OR has_table_privilege(current_user, {0}, 'DELETE') AS "Value"
                """, $"public.\"{tabla}\"").ToListAsync()).Should().Equal([false], $"{tabla}: el rechazo es por privilegio");
        }
    }

    /// <summary>P-6, negativo real (R-0b, issue #2351). Una Sesión Privilegiada sobre Ebro con un
    /// <see cref="AmbitoTenantExplicito"/> sobre Norte: el ámbito manda sobre la selección en <see cref="TenantActual"/>,
    /// pero la guarda del interceptor conserva <c>cae_app_soporte</c> y deja la conexión sin Tenant. No lee Norte (ni
    /// Ebro: sin Tenant ninguna política casa) y no escribe ni en una tabla sin RLS que el rol de login sí escribe.
    /// Mutaciones: quitar la guarda firma Norte con <c>cae_app_soporte</c> y la lectura devuelve Norte; quitar el
    /// <c>SET ROLE</c> o devolver un rol nulo en la guarda deja <c>cae_app_runtime</c>, y fallan
    /// <c>current_user</c> y el 42501 del INSERT.</summary>
    [Fact]
    public async Task P6_una_Sesion_Privilegiada_sobre_un_Tenant_no_lee_otro_por_un_ambito_explicito()
    {
        string tablaSinRls;
        await using (var propietaria = await AbrirAsync(_cadena))
        {
            tablaSinRls = (string?)await EscalarAsync(propietaria, """
                SELECT c.relname::text FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
                 WHERE n.nspname = 'public' AND c.relkind = 'r' AND NOT c.relrowsecurity
                   AND has_table_privilege('cae_app_runtime', c.oid, 'INSERT')
                 ORDER BY c.relname LIMIT 1
                """) ?? throw new InvalidOperationException("barrera: hace falta una tabla sin RLS que el rol de login escriba");
            (await EscalarAsync(propietaria, "SELECT has_table_privilege('cae_app_soporte', format('public.%I', @t), 'INSERT')",
                ("t", tablaSinRls))).Should().Be(false, $"barrera: {tablaSinRls} no la escribe el rol de soporte");
        }

        await using var contexto = ContextoDeSesionPrivilegiada(objetivo: _ebro);
        (await TenantsLeidosAsync(contexto, ["Conversaciones", "Mensajes"])).Should().Equal([_ebro], "control positivo");

        using (AmbitoTenantExplicito.Establecer(_norte))
        {
            (await contexto.Database.SqlQueryRaw<string>("""SELECT current_user::text AS "Value" """).ToListAsync())
                .Should().Equal(["cae_app_soporte"], "la guarda conserva el rol de soporte; nunca cae al rol de login");
            (await TenantsLeidosAsync(contexto, ["Conversaciones", "Mensajes"])).Should().BeEmpty(
                "la Sesión Privilegiada se concedió sobre Ebro; un ámbito explícito sobre Norte deja la conexión sin Tenant");
            await DebeRechazarAsync(
                () => contexto.Database.ExecuteSqlRawAsync(string.Format("INSERT INTO \"{0}\" DEFAULT VALUES", tablaSinRls)), null);
        }

        (await TenantsLeidosAsync(contexto, ["Conversaciones", "Mensajes"])).Should().Equal([_ebro],
            "al cerrar el ámbito, la misma conexión vuelve a la Sesión Privilegiada sobre Ebro");
    }

    /// <summary>P-7. El Actor de Plataforma TALVEG visita Ebro con ámbito explícito (Tenant del contexto Ebro, Tenant de
    /// origen la plataforma), con Delegación de Soporte sobre Ebro y sobre Norte. Falsación: copiar a
    /// <c>Conversaciones</c> la forma de <c>plataforma_lee_su_visita</c> sin <c>"TenantId" = app_ctx_tenant_id()</c>
    /// abre Norte desde la visita a Ebro, y Ebro y Norte desde su propio Tenant.</summary>
    [Fact]
    public async Task P7_el_Actor_de_Plataforma_TALVEG_que_visita_un_Tenant_no_lee_otro_aunque_tenga_su_Delegacion_de_Soporte()
    {
        await using (var propietaria = await AbrirAsync(_cadena))
            (await EscalarAsync(propietaria, """
                SELECT count(*) FROM "DelegacionesTenant" d JOIN "Tenants" t ON t."Id" = d."TenantConsultoraId"
                 WHERE t."EsPlataforma" AND d."TenantConsultoraId" = @p AND d."TenantClienteId" = ANY(@c)
                """, ("p", _plataforma), ("c", new[] { _ebro, _norte }))).Should().Be(2L, "barrera: las dos visitas son posibles");

        var actor = Guid.NewGuid();
        await using var visita = await AbrirComoRuntimeAsync(tenant: _ebro, origen: _plataforma, usuario: actor);
        await using var propio = await AbrirComoRuntimeAsync(tenant: _plataforma, origen: _plataforma, usuario: actor);
        using var _ = new AssertionScope();
        foreach (var tabla in Tablas)
        {
            (await IdsAsync(visita, tabla, TodasLasFilas(tabla))).Should().Equal([_filas[_ebro][tabla]], $"{tabla}: solo el Tenant visitado");
            (await IdsAsync(propio, tabla, TodasLasFilas(tabla))).Should().Equal([_filas[_plataforma][tabla]], $"{tabla}: solo el suyo");
        }
    }

    /// <summary>P-8. Falsación: una segunda PERMISSIVE en una de las tablas, <c>OR true</c> en <c>aislamiento_tenant</c>
    /// (que <c>MencionaElAislamiento</c> no detecta), limitarla a un comando o a un rol, o quitar FORCE. Incluye
    /// <c>ContadoresTicketConversacion</c>, fuera del modelo EF, cuya política es la que para el INSERT de P-2.</summary>
    [Fact]
    public async Task P8_cada_tabla_tiene_solo_aislamiento_tenant_permisiva_de_todas_las_ordenes_y_sin_disyuncion()
    {
        string[] tablas = [.. Tablas, "ContadoresTicketConversacion"];
        await using var conexion = await AbrirAsync(_cadena);
        var filas = await LeerAsync(conexion, """
            SELECT c.relname, c.relrowsecurity, c.relforcerowsecurity, p.polname, p.polpermissive, p.polcmd::text,
                   pg_get_expr(p.polqual, p.polrelid), pg_get_expr(p.polwithcheck, p.polrelid),
                   ARRAY(SELECT CASE WHEN r = 0 THEN 'public' ELSE pg_get_userbyid(r)::text END FROM unnest(p.polroles) r)
              FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace LEFT JOIN pg_policy p ON p.polrelid = c.oid
             WHERE n.nspname = 'public' AND c.relname = ANY(@tablas)
            """, ("tablas", tablas));
        filas.Select(f => (string)f[0]).Distinct().Should().BeEquivalentTo(tablas, "control positivo: el catálogo ve todas las tablas");

        using var _ = new AssertionScope();
        foreach (var f in filas)
        {
            var donde = $"{f[0]}.{f[3]}";
            ((bool)f[1] && (bool)f[2]).Should().BeTrue($"{f[0]}: RLS habilitada y forzada");
            if ((string)f[3] != "aislamiento_tenant")
            {
                PoliticasAdicionalesRevisadas.Should().ContainKey(donde, "una política más en estas tablas se revisa con motivo");
                continue;
            }
            ((bool)f[4]).Should().BeTrue($"{donde}: PERMISSIVE");
            ((string)f[5]).Should().Be("*", $"{donde}: de todas las órdenes");
            ((string[])f[8]).Should().Equal(["public"], $"{donde}: para todos los roles");
            foreach (var expresion in new[] { f[6] as string, f[7] as string })
            {
                InspeccionTextoPoliticaRls.MencionaElAislamiento(expresion).Should().BeTrue($"{donde}: {expresion}");
                InspeccionTextoPoliticaRls.TieneDisyuncion(expresion).Should().BeFalse($"{donde}: {expresion}");
            }
        }
    }

    /// <summary>P-9. Falsación: una función <c>SECURITY DEFINER</c> de prueba (EXECUTE a PUBLIC por defecto) que lea
    /// <c>"Mensajes"</c>. El control positivo demuestra que el detector lee el cuerpo (<c>pg_get_functiondef</c>, que
    /// cubre también los cuerpos SQL estándar): encuentra las que leen <c>"AspNetUsers"</c>. Lo que no observa: SQL
    /// dinámico que construya el nombre de la tabla, y una función sin SECURITY DEFINER que lea estas tablas llamada
    /// desde una que sí lo es (corre como el propietario de la llamante).</summary>
    [Fact]
    public async Task P9_ninguna_funcion_SECURITY_DEFINER_de_los_roles_de_trafico_lee_estas_tablas()
    {
        await using var conexion = await AbrirAsync(_cadena);
        var funciones = (await LeerAsync(conexion, """
            SELECT p.oid::regprocedure::text, pg_get_functiondef(p.oid)
              FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
             WHERE p.prosecdef AND p.prokind IN ('f', 'p') AND n.nspname NOT IN ('pg_catalog', 'information_schema')
               AND EXISTS (SELECT 1 FROM unnest(ARRAY['cae_app_runtime', 'cae_app_soporte', 'cae_app_aprovisionamiento']) r
                            WHERE has_function_privilege(r, p.oid, 'EXECUTE'))
            """)).Select(f => (Nombre: (string)f[0], Cuerpo: (string)f[1])).ToList();
        IEnumerable<string> QueLeen(params string[] tablas) => funciones
            .Where(f => Regex.IsMatch(f.Cuerpo, $"[\"'](?:{string.Join('|', tablas)})[\"']")).Select(f => f.Nombre);

        QueLeen("AspNetUsers").Should().NotBeEmpty("control positivo: el detector ve el cuerpo de las funciones");
        QueLeen(Tablas).Where(f => !FuncionesDefinerRevisadas.ContainsKey(f)).Should().BeEmpty(
            "una función SECURITY DEFINER lee como su propietario, sin RLS: sería una lectura entre Tenants no revisada");
    }

    /// <summary>P-10. Falsación: <c>ALTER ROLE cae_app_runtime BYPASSRLS</c>, hacerlo miembro de un rol con BYPASSRLS
    /// o de superusuario, o <c>ALTER TABLE "Mensajes" OWNER TO cae_app_soporte</c>.</summary>
    [Theory]
    [InlineData("cae_app_runtime")]
    [InlineData("cae_app_soporte")]
    public async Task P10_los_roles_de_trafico_no_saltan_la_RLS_ni_son_propietarios_de_estas_tablas(string rol)
    {
        await using var conexion = await AbrirAsync(_cadena);
        var roles = await LeerAsync(conexion, "SELECT rolname::text, rolsuper, rolbypassrls FROM pg_roles r WHERE pg_has_role(@rol, r.oid, 'MEMBER')", ("rol", rol));
        roles.Select(r => (string)r[0]).Should().Contain(rol, "control positivo: la consulta ve el propio rol");
        roles.Where(r => (bool)r[1] || (bool)r[2]).Select(r => (string)r[0]).Should().BeEmpty("ni el rol ni ninguno que herede salta la RLS");

        var propietarios = await LeerAsync(conexion, """
            SELECT c.relname::text, pg_has_role(@rol, c.relowner, 'MEMBER') FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
             WHERE n.nspname = 'public' AND c.relname = ANY(@tablas)
            """, ("rol", rol), ("tablas", Tablas));
        propietarios.Should().HaveCount(Tablas.Length, "control positivo: el catálogo ve las siete tablas");
        propietarios.Where(p => (bool)p[1]).Select(p => (string)p[0]).Should().BeEmpty("el propietario no está sujeto a la RLS sin FORCE ni a los privilegios");
    }

    // ── Instrumentos ────────────────────────────────────────────────────

    private Guid[] TodasLasFilas(string tabla) => _filas.Values.Select(f => f[tabla]).ToArray();

    private static async Task<NpgsqlConnection> AbrirAsync(string cadena)
    {
        var conexion = new NpgsqlConnection(cadena);
        await conexion.OpenAsync();
        return conexion;
    }

    private async Task<NpgsqlConnection> AbrirComoRuntimeAsync(Guid? tenant, Guid? origen = null, Guid? usuario = null)
    {
        var conexion = await AbrirAsync(new NpgsqlConnectionStringBuilder(BaseDatosPostgresDePruebas.CadenaComoRuntime(_cadena)) { Pooling = false }.ConnectionString);
        (await EscalarAsync(conexion, "SELECT current_user::text")).Should().Be("cae_app_runtime");
        if (tenant is not null)
            await ContextoRlsDePruebas.FijarAsync(conexion, tenantId: tenant, tenantOrigenId: origen, usuarioId: usuario);
        return conexion;
    }

    private static Task FijarTokenAsync(NpgsqlConnection conexion, string token) =>
        EjecutarAsync(conexion, "SELECT set_config('app.contexto', @t, false)", ("t", token));

    private static async Task<List<Guid>> IdsAsync(NpgsqlConnection conexion, string tabla, Guid[] ids) =>
        (await LeerAsync(conexion, $"""SELECT "Id" FROM "{tabla}" WHERE "Id" = ANY(@ids) ORDER BY "Id" """, ("ids", ids))).Select(f => (Guid)f[0]).ToList();

    /// <summary>Los TenantId que el contexto lee de estas filas, de la columna y por el interceptor del contexto.</summary>
    private async Task<List<Guid>> TenantsLeidosAsync(CaeManagerDbContext contexto, IEnumerable<string> tablas)
    {
        var leidos = new List<Guid>();
        foreach (var tabla in tablas)
            leidos.AddRange(await contexto.Database.SqlQueryRaw<Guid>(
                SqlTenantsDe(tabla), new NpgsqlParameter("ids", TodasLasFilas(tabla))).ToListAsync());
        return leidos.Distinct().ToList();
    }

    // Un nombre de tabla es un identificador y no se puede parametrizar: se construye aquí, fuera de la llamada a
    // SqlQueryRaw, y solo desde la lista cerrada de tablas de la prueba. Los valores (@ids) sí van como parámetro.
    private static string SqlTenantsDe(string tabla)
    {
        if (!Tablas.Contains(tabla)) throw new ArgumentException($"Tabla fuera de la lista cerrada: {tabla}", nameof(tabla));
        return $"""SELECT "TenantId" AS "Value" FROM "{tabla}" WHERE "Id" = ANY(@ids)""";
    }

    /// <summary>Inserta una copia de la fila <paramref name="origen"/> con Id nuevo y el Tenant indicado.</summary>
    private static Task<int> CopiarAsync(NpgsqlConnection conexion, string tabla, Guid origen, Guid tenant) =>
        EjecutarAsync(conexion, SqlCopia(tabla), ("nuevo", Guid.NewGuid()), ("tenant", tenant), ("origen", origen));

    private static string SqlCopia(string tabla) => $"""
        INSERT INTO "{tabla}" SELECT (jsonb_populate_record(NULL::"{tabla}", to_jsonb(f) || jsonb_build_object('Id', @nuevo, 'TenantId', @tenant))).*
          FROM "{tabla}" f WHERE f."Id" = @origen
        """;

    /// <summary>42501; <paramref name="motivo"/> solo para los RAISE propios, cuyo texto no depende de <c>lc_messages</c>.</summary>
    private static async Task DebeRechazarAsync(Func<Task> accion, string? motivo)
    {
        var error = (await accion.Should().ThrowAsync<PostgresException>()).Which;
        error.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege, error.MessageText);
        if (motivo is not null)
            error.MessageText.Should().Contain(motivo, "el rechazo tiene que ser por la comprobación esperada");
    }

    private static async Task<int> EjecutarAsync(NpgsqlConnection conexion, string sql, params (string, object)[] parametros)
    {
        await using var orden = Orden(conexion, sql, parametros);
        return await orden.ExecuteNonQueryAsync();
    }

    private static async Task<object?> EscalarAsync(NpgsqlConnection conexion, string sql, params (string, object)[] parametros)
    {
        await using var orden = Orden(conexion, sql, parametros);
        return await orden.ExecuteScalarAsync();
    }

    private static async Task<List<object[]>> LeerAsync(NpgsqlConnection conexion, string sql, params (string, object)[] parametros)
    {
        await using var orden = Orden(conexion, sql, parametros);
        await using var lector = await orden.ExecuteReaderAsync();
        var filas = new List<object[]>();
        while (await lector.ReadAsync())
        {
            var fila = new object[lector.FieldCount];
            lector.GetValues(fila);
            filas.Add(fila.Select(v => v is DBNull ? null! : v).ToArray());
        }
        return filas;
    }

    private static NpgsqlCommand Orden(NpgsqlConnection conexion, string sql, (string Nombre, object Valor)[] parametros)
    {
        var orden = new NpgsqlCommand(sql, conexion);
        foreach (var (nombre, valor) in parametros)
            orden.Parameters.AddWithValue(nombre, valor);
        return orden;
    }

    private CaeManagerDbContext ContextoPropietario(Guid tenant)
    {
        var tenantActual = new TenantActualAmbiental { TenantId = tenant };
        var opciones = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadena).AddInterceptors(new TenantSelladoInterceptor(tenantActual)).Options;
        return new CaeManagerDbContext(opciones, new EphemeralDataProtectionProvider(), tenantActual);
    }

    private static CaeManagerDbContext ContextoRuntime(string cadena, ITenantActual tenant, IClienteActivoSeleccionado seleccion, Guid origen)
    {
        var usuario = new CurrentUserServiceFalso(Guid.NewGuid(), Roles.Administrador, tenantOrigenId: origen);
        var opciones = new DbContextOptionsBuilder<CaeManagerDbContext>().UseNpgsql(cadena)
            .AddInterceptors(new TenantRlsConnectionInterceptor(tenant, seleccion, usuario, BaseDatosPostgresDePruebas.FirmanteContextoRls))
            .Options;
        return new CaeManagerDbContext(opciones, new EphemeralDataProtectionProvider(), tenant);
    }

    /// <summary>Actor de Plataforma TALVEG con claim de su Tenant de plataforma y Sesión Privilegiada sobre
    /// <paramref name="objetivo"/>, con el <see cref="TenantActual"/> de producción encima.</summary>
    private CaeManagerDbContext ContextoDeSesionPrivilegiada(Guid objetivo)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(TenantClaimsPrincipalFactory.TipoClaimTenantId, _plataforma.ToString())], "prueba"));
        var seleccion = new Seleccion(objetivo, Guid.NewGuid());
        var tenant = new TenantActual(new Principal(principal), new HttpContextAccessor(), seleccion);
        tenant.TenantId.Should().Be(objetivo, "barrera: sin ámbito, la Sesión Privilegiada fija el Tenant objetivo");
        return ContextoRuntime(
            new NpgsqlConnectionStringBuilder(BaseDatosPostgresDePruebas.CadenaComoRuntime(_cadena)) { Pooling = false }.ConnectionString,
            tenant, seleccion, _plataforma);
    }

    private sealed class TenantMutable : ITenantActual
    {
        public Guid? TenantId { get; set; }
    }

    private sealed class Seleccion(Guid? tenant, Guid? sesionPrivilegiada) : IClienteActivoSeleccionado
    {
        public Guid? TenantIdSeleccionado => tenant;
        public Guid? AsignacionOperacionIdSeleccionada => null;
        public Guid? SesionPrivilegiadaIdSeleccionada => sesionPrivilegiada;
    }

    private sealed class Principal(ClaimsPrincipal principal) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(principal));
    }

    private sealed class RelojFijo(DateTimeOffset ahora) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => ahora;
    }
}

/// <summary>P-5 necesita que el pool devuelva el mismo backend, así que corre en
/// <see cref="ColeccionPoolNpgsqlSinVaciadosConcurrentes"/> y reutiliza el escenario por composición, como
/// <see cref="ContextoRlsFirmadoPoolTests"/>.</summary>
[Collection(ColeccionPoolNpgsqlSinVaciadosConcurrentes.Nombre)]
public sealed class LineaBaseRlsComunicacionesPoolBajoRuntimeTests : IAsyncLifetime
{
    private readonly LineaBaseRlsComunicacionesBajoRuntimeTests _clase = new();

    public Task InitializeAsync() => _clase.InitializeAsync();

    public Task DisposeAsync() => _clase.DisposeAsync();

    [Fact]
    public Task P5_cambiar_de_Tenant_en_la_misma_conexion_no_deja_ver_el_anterior_y_el_pool_no_lo_hereda() => _clase.P5Async();
}
