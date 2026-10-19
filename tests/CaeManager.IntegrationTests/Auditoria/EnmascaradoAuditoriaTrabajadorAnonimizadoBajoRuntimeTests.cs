using System.Text.Json.Nodes;
using CaeManager.Application.Common;
using CaeManager.Application.Retencion;
using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Retencion;
using CaeManager.Domain.Trabajadores;
using CaeManager.Infrastructure.Auditing;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Repositories;
using CaeManager.IntegrationTests.Arranque;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace CaeManager.IntegrationTests.Auditoria;

/// <summary>
/// <b>Al anonimizar un Trabajador, sus datos personales se enmascaran en el historial de
/// auditoría</b> (decisión de Chris, 2026-10-15; migración
/// <c>EnmascaraAuditoriaDeTrabajadorAnonimizado</c>).
///
/// <para>
/// Todo bajo <c>cae_app_runtime</c> real, sobre el arnés de arranque con los interceptores
/// de producción: la aplicación no tiene UPDATE sobre <c>RegistrosAuditoria</c>, y la única
/// reescritura es la función SECURITY DEFINER
/// <c>app_enmascarar_auditoria_de_trabajador_anonimizado</c>. Se comprueba que enmascara
/// solo los campos personales del Trabajador anonimizado del Tenant de la sesión; que no
/// toca a otro Trabajador, a uno sin anonimizar ni nada de otro Tenant; que conserva actor,
/// fecha y acción; y que el UPDATE directo sigue rechazado con 42501. Las lecturas de
/// verificación van por la conexión del propietario, que ve todos los Tenants.
/// </para>
/// </summary>
public class EnmascaradoAuditoriaTrabajadorAnonimizadoBajoRuntimeTests
{
    private const string Funcion = "app_enmascarar_auditoria_de_trabajador_anonimizado";

    private readonly Guid _tenantA = Guid.NewGuid();
    private readonly Guid _tenantB = Guid.NewGuid();
    private readonly Guid _usuario = Guid.NewGuid();
    private readonly DateOnly _hoy = new(2031, 6, 1);

    /// <summary>Lo que el historial no puede conservar de Manuela tras anonimizarla.</summary>
    private static readonly string[] RastrosDeManuela =
    [
        "Manuela", "Quintanilla", "11111111H", "manuela.quintanilla", "600111222",
        "Alergia", "Soldadora", "\"Manu\"", "1985-03-04",
    ];

    [Fact]
    public async Task La_purga_enmascara_el_historial_del_trabajador_anonimizado_y_nada_mas()
    {
        await using var arnes = await CrearArnesAsync();

        var (manuela, bartolome, centroId) = await SembrarTenantAAsync(arnes);
        await AsignarConBajaAsync(arnes, manuela, centroId, _hoy.AddYears(-6));
        var casilda = await SembrarTrabajadorAnonimizadoEnTenantBAsync(arnes);

        // Una fila del Tenant B que nombra al Trabajador del Tenant A: anonimizar a
        // Manuela en A no puede reescribirla.
        await InsertarFilaComoRuntimeAsync(arnes, _tenantB, manuela, """{"Nombre":"Fabricada en B"}""");

        var antes = await LeerFilasDeTrabajadorAsync(arnes);
        antes.Where(f => f.TenantId == _tenantA && f.EntidadId == manuela)
            .Should().Contain(f => ContieneAlguno(f, RastrosDeManuela),
                "control positivo del instrumento: antes de anonimizar, el historial sí tiene sus datos");

        (await EjecutarPurgaDeTrabajadoresAsync(arnes)).Should().Be(1);

        var despues = await LeerFilasDeTrabajadorAsync(arnes);

        var deManuela = despues.Where(f => f.TenantId == _tenantA && f.EntidadId == manuela).ToList();
        deManuela.Should().HaveCount(antes.Count(f => f.TenantId == _tenantA && f.EntidadId == manuela) + 1,
            "la anonimización deja su propia entrada y no borra ninguna");
        deManuela.Should().NotContain(f => ContieneAlguno(f, RastrosDeManuela),
            "ni las entradas anteriores ni la que deja Anonimizar pueden conservar sus datos personales");

        var entradaDeAnonimizacion = deManuela.Single(f => antes.All(a => a.Id != f.Id));
        entradaDeAnonimizacion.Accion.Should().Be("Modificado");
        entradaDeAnonimizacion.UsuarioId.Should().Be(_usuario);
        JsonNode.Parse(entradaDeAnonimizacion.DatosAntes!)!["Dni"]!.GetValue<string>().Should().Be("***");

        foreach (var previa in antes.Where(f => f.TenantId == _tenantA && f.EntidadId == manuela))
            DebeSerLaMismaConDatosPersonalesEnmascarados(previa, despues.Single(f => f.Id == previa.Id));

        // Otro Trabajador del mismo Tenant, y todo el Tenant B (su Trabajador y la fila que
        // nombra a Manuela), quedan exactamente como estaban.
        foreach (var ajena in antes.Where(f => f.EntidadId != manuela || f.TenantId != _tenantA))
            despues.Single(f => f.Id == ajena.Id).Should().Be(ajena);
        antes.Should().Contain(f => f.EntidadId == bartolome && ContieneAlguno(f, new[] { "Bartolom" }));
        antes.Should().Contain(f => f.EntidadId == casilda && ContieneAlguno(f, new[] { "Casilda" }));
    }

    /// <summary>
    /// La «Nota interna» de Trabajador 360 (<c>Trabajador.Notas</c>) es dato personal: tras anonimizar, ninguna
    /// entrada del historial de ese Trabajador conserva su texto, y la clave <c>Notas</c> queda en "***"
    /// (migración <c>AnadeNotaInternaATrabajadorYCentro</c>, que la añade a la función).
    /// </summary>
    [Fact]
    public async Task La_purga_enmascara_la_nota_interna_del_trabajador_anonimizado()
    {
        const string nota = "Prefiere turno de tarde por cuidado de su madre";
        await using var arnes = await CrearArnesAsync();

        var (manuela, _, centroId) = await SembrarTenantAAsync(arnes);
        await FijarNotaInternaAsync(arnes, manuela, nota);
        await AsignarConBajaAsync(arnes, manuela, centroId, _hoy.AddYears(-6));

        var antes = (await LeerFilasDeTrabajadorAsync(arnes)).Where(f => f.TenantId == _tenantA && f.EntidadId == manuela).ToList();
        antes.Should().Contain(f => ContieneAlguno(f, new[] { nota }),
            "control positivo del instrumento: antes de anonimizar, el historial sí tiene la nota");

        (await EjecutarPurgaDeTrabajadoresAsync(arnes)).Should().Be(1);

        var despues = (await LeerFilasDeTrabajadorAsync(arnes)).Where(f => f.TenantId == _tenantA && f.EntidadId == manuela).ToList();
        despues.Should().NotContain(f => ContieneAlguno(f, new[] { nota }), "la nota interna es dato personal del Trabajador");

        var conNota = despues
            .SelectMany(f => new[] { f.DatosAntes, f.DatosDespues })
            .Where(d => d is not null && JsonNode.Parse(d)!.AsObject().ContainsKey(nameof(Trabajador.Notas)))
            .Select(d => JsonNode.Parse(d!)![nameof(Trabajador.Notas)])
            .ToList();
        conNota.Should().NotBeEmpty("la entrada que fijó la nota la nombra");
        conNota.Where(v => v is not null).Should().OnlyContain(v => v!.GetValue<string>() == "***");
        conNota.Should().Contain(v => v != null && v.GetValue<string>() == "***");
    }

    /// <summary>
    /// El contacto de emergencia (<c>Trabajador.ContactoEmergenciaNombre</c> y <c>ContactoEmergenciaTelefono</c>) es
    /// dato personal: tras anonimizar, ninguna entrada del historial de ese Trabajador conserva el nombre ni el teléfono,
    /// y las dos claves quedan en "***" (migración <c>AnadeContactoEmergenciaATrabajador</c>, que las añade a la función).
    /// </summary>
    [Fact]
    public async Task La_purga_enmascara_el_contacto_de_emergencia_del_trabajador_anonimizado()
    {
        const string nombre = "Remedios Quintanilla (madre)";
        const string telefono = "+34611223344";
        // El historial guarda el JSON de System.Text.Json, que escapa el «+» como +: se busca por los dígitos, que
        // salen igual escapados o no.
        const string rastroTelefono = "34611223344";
        await using var arnes = await CrearArnesAsync();

        var (manuela, _, centroId) = await SembrarTenantAAsync(arnes);
        await FijarContactoEmergenciaAsync(arnes, manuela, nombre, telefono);
        await AsignarConBajaAsync(arnes, manuela, centroId, _hoy.AddYears(-6));

        var antes = (await LeerFilasDeTrabajadorAsync(arnes)).Where(f => f.TenantId == _tenantA && f.EntidadId == manuela).ToList();
        antes.Should().Contain(f => ContieneAlguno(f, new[] { nombre }) && ContieneAlguno(f, new[] { rastroTelefono }),
            "control positivo del instrumento: antes de anonimizar, el historial sí tiene el contacto");

        (await EjecutarPurgaDeTrabajadoresAsync(arnes)).Should().Be(1);

        var despues = (await LeerFilasDeTrabajadorAsync(arnes)).Where(f => f.TenantId == _tenantA && f.EntidadId == manuela).ToList();
        despues.Should().NotContain(f => ContieneAlguno(f, new[] { nombre, rastroTelefono }),
            "el contacto de emergencia es dato personal");

        foreach (var clave in new[] { nameof(Trabajador.ContactoEmergenciaNombre), nameof(Trabajador.ContactoEmergenciaTelefono) })
        {
            var valores = despues
                .SelectMany(f => new[] { f.DatosAntes, f.DatosDespues })
                .Where(d => d is not null && JsonNode.Parse(d)!.AsObject().ContainsKey(clave))
                .Select(d => JsonNode.Parse(d!)![clave])
                .ToList();
            valores.Should().NotBeEmpty($"la entrada que fijó el contacto nombra {clave}");
            valores.Where(v => v is not null).Should().OnlyContain(v => v!.GetValue<string>() == "***", clave);
            valores.Should().Contain(v => v != null && v.GetValue<string>() == "***", clave);
        }
    }

    [Fact]
    public async Task La_funcion_bajo_runtime_solo_alcanza_al_trabajador_anonimizado_del_tenant_de_la_sesion()
    {
        await using var arnes = await CrearArnesAsync();

        var (manuela, bartolome, _) = await SembrarTenantAAsync(arnes);
        var casilda = await SembrarTrabajadorAnonimizadoEnTenantBAsync(arnes);
        // Una fila del Tenant A que nombra al Trabajador anonimizado del Tenant B: que Casilda
        // esté anonimizada en B no autoriza a reescribir nada en A.
        await InsertarFilaComoRuntimeAsync(arnes, _tenantA, casilda, """{"Nombre":"Casilda en A"}""");

        await AnonimizarSinEnmascararAsync(arnes, manuela);

        var antes = await LeerFilasDeTrabajadorAsync(arnes);

        // La entrada que deja Anonimizar no guarda los valores anteriores aunque nadie llame
        // a la función: lo impide AuditoriaInterceptor al escribirla.
        var entradaDeAnonimizacion = antes
            .Where(f => f.EntidadId == manuela && f.Accion == "Modificado")
            .Single(f => JsonNode.Parse(f.DatosDespues!)!["AnonimizadoEnUtc"] is not null);
        ContieneAlguno(entradaDeAnonimizacion, RastrosDeManuela).Should().BeFalse();
        JsonNode.Parse(entradaDeAnonimizacion.DatosAntes!)!["Nombre"]!.GetValue<string>().Should().Be("***");
        // Las anteriores siguen en claro hasta que la función las enmascare.
        antes.Should().Contain(f => f.EntidadId == manuela && ContieneAlguno(f, RastrosDeManuela));

        await using var runtime = await AbrirComoRuntimeAsync(arnes, _tenantA);

        (await EnmascararAsync(runtime, casilda)).Should().Be(0, "Casilda es de otro Tenant");
        (await EnmascararAsync(runtime, bartolome)).Should().Be(0, "Bartolomé no está anonimizado");
        (await EnmascararAsync(runtime, Guid.NewGuid())).Should().Be(0, "no existe");
        (await LeerFilasDeTrabajadorAsync(arnes)).Should().BeEquivalentTo(antes, o => o.WithStrictOrdering(),
            "ninguna de las tres llamadas puede reescribir nada");

        (await EnmascararAsync(runtime, manuela)).Should().BeGreaterThan(0);

        var despues = await LeerFilasDeTrabajadorAsync(arnes);
        despues.Where(f => f.TenantId == _tenantA && f.EntidadId == manuela)
            .Should().NotContain(f => ContieneAlguno(f, RastrosDeManuela));
        foreach (var previa in antes.Where(f => f.TenantId == _tenantA && f.EntidadId == manuela))
            DebeSerLaMismaConDatosPersonalesEnmascarados(previa, despues.Single(f => f.Id == previa.Id));
        foreach (var ajena in antes.Where(f => f.EntidadId != manuela || f.TenantId != _tenantA))
            despues.Single(f => f.Id == ajena.Id).Should().Be(ajena);

        // Repetirla no cambia nada más.
        await EnmascararAsync(runtime, manuela);
        (await LeerFilasDeTrabajadorAsync(arnes)).Should().BeEquivalentTo(despues, o => o.WithStrictOrdering());

        // La tabla sigue sin UPDATE directo para la aplicación: el rechazo es el REVOKE (42501).
        await using var update = runtime.CreateCommand();
        update.CommandText = """UPDATE "RegistrosAuditoria" SET "DatosAntes" = NULL WHERE "EntidadTipo" = 'Trabajador';""";
        (await FluentActions.Awaiting(() => update.ExecuteNonQueryAsync())
            .Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("42501");
    }

    [Fact]
    public async Task Sin_tenant_en_la_sesion_la_funcion_falla_con_42501_sin_tocar_nada()
    {
        await using var arnes = await CrearArnesAsync();

        var (manuela, _, _) = await SembrarTenantAAsync(arnes);
        await AnonimizarSinEnmascararAsync(arnes, manuela);
        var antes = await LeerFilasDeTrabajadorAsync(arnes);

        await using var runtime = await AbrirComoRuntimeAsync(arnes, tenant: null);

        (await FluentActions.Awaiting(() => EnmascararAsync(runtime, manuela))
            .Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("42501");
        (await LeerFilasDeTrabajadorAsync(arnes)).Should().BeEquivalentTo(antes, o => o.WithStrictOrdering());
    }

    /// <summary>
    /// El contrato de la función, leído del catálogo: SECURITY DEFINER con search_path fijo,
    /// propietario distinto del runtime, un único parámetro uuid, EXECUTE solo para
    /// cae_app_runtime y nada para PUBLIC, y la misma lista de campos que el dominio.
    /// </summary>
    [Fact]
    public async Task La_funcion_cumple_su_contrato_de_privilegios()
    {
        await using var arnes = await CrearArnesAsync();
        await using var propietario = new NpgsqlConnection(arnes.CadenaPropietario);
        await propietario.OpenAsync();

        await using var orden = new NpgsqlCommand(
            """
            SELECT p.prosecdef,
                   pg_get_userbyid(p.proowner),
                   array_to_string(p.proconfig, ';'),
                   pg_get_function_identity_arguments(p.oid),
                   has_function_privilege('cae_app_runtime', p.oid, 'EXECUTE'),
                   has_function_privilege('cae_app_soporte', p.oid, 'EXECUTE'),
                   has_function_privilege('cae_app_aprovisionamiento', p.oid, 'EXECUTE'),
                   p.proacl IS NULL OR EXISTS (SELECT 1 FROM aclexplode(p.proacl) a WHERE a.grantee = 0),
                   pg_get_functiondef(p.oid)
            FROM pg_proc p
            WHERE p.proname = @nombre AND p.pronamespace = 'public'::regnamespace;
            """, propietario);
        orden.Parameters.AddWithValue("nombre", Funcion);
        await using var lector = await orden.ExecuteReaderAsync();
        (await lector.ReadAsync()).Should().BeTrue("la migración crea la función");

        lector.GetBoolean(0).Should().BeTrue("SECURITY DEFINER");
        lector.GetString(1).Should().NotBe("cae_app_runtime");
        lector.GetString(2).Should().Be("search_path=pg_catalog, pg_temp");
        lector.GetString(3).Should().Be("p_trabajador_id uuid", "ningún parámetro elige Tenant, entidad ni campos");
        lector.GetBoolean(4).Should().BeTrue();
        lector.GetBoolean(5).Should().BeFalse();
        lector.GetBoolean(6).Should().BeFalse();
        lector.GetBoolean(7).Should().BeFalse("REVOKE ALL ... FROM PUBLIC");

        var definicion = lector.GetString(8);
        foreach (var campo in Trabajador.CamposDeDatosPersonales)
            definicion.Should().Contain($"'{campo}'", "la función enmascara los mismos campos que Anonimizar vacía");

        (await lector.ReadAsync()).Should().BeFalse("una sola sobrecarga");
    }

    // ── Siembra ──────────────────────────────────────────────────────────

    private Task<ArnesDeArranqueRuntime> CrearArnesAsync() =>
        ArnesDeArranqueRuntime.CrearAsync(
            datosDePruebaActivos: false,
            actorAuditoriaPersonalizado: new ActorFijo(ActorAuditoria.Normal(_usuario)));

    /// <summary>
    /// Tenant A: Manuela y Bartolomé, cada uno con su alta y una edición en el historial.
    /// </summary>
    private async Task<(Guid Manuela, Guid Bartolome, Guid CentroId)> SembrarTenantAAsync(ArnesDeArranqueRuntime arnes)
    {
        using var ambito = arnes.Servicios.CreateScope();
        var contexto = ambito.ServiceProvider.GetRequiredService<CaeManagerDbContext>();
        using var ambitoTenant = AmbitoTenantExplicito.Establecer(_tenantA);

        var cliente = Empresa.CrearComoCliente("Cliente Purga S.L.", "B12345674", false, null, null);
        var empresa = new Empresa("Contrata Purga S.L.", "B87654323");
        contexto.Empresas.AddRange(cliente, empresa);
        await contexto.SaveChangesAsync();

        var centro = new Centro(cliente.Id, empresa.Id, "Centro Purga", "Calle Falsa 3");
        var manuela = Trabajador.DeEmpresa(
            empresa.Id, "Manuela", "Quintanilla Ortega", "11111111H", new DateOnly(1985, 3, 4),
            "manuela.quintanilla@ejemplo.test", "Alergia al látex", "Manu", "+34600111222", "Soldadora");
        var bartolome = Trabajador.DeEmpresa(
            empresa.Id, "Bartolomé", "Ruiz Pardo", "22222222J", new DateOnly(1979, 7, 8),
            "bartolome.ruiz@ejemplo.test", null, null, "+34600333444", "Gruista");
        contexto.Centros.Add(centro);
        contexto.Trabajadores.AddRange(manuela, bartolome);
        await contexto.SaveChangesAsync();

        manuela.Actualizar("Manuela", "Quintanilla Ortiz", manuela.FechaNacimiento, manuela.Email,
            manuela.Observaciones, manuela.Alias, manuela.Telefono, manuela.Puesto);
        bartolome.Actualizar("Bartolomé", "Ruiz Prado", bartolome.FechaNacimiento, bartolome.Email,
            bartolome.Observaciones, bartolome.Alias, bartolome.Telefono, bartolome.Puesto);
        await contexto.SaveChangesAsync();

        return (manuela.Id, bartolome.Id, centro.Id);
    }

    /// <summary>Tenant B: Casilda, con alta y edición en claro, y luego anonimizada.</summary>
    private async Task<Guid> SembrarTrabajadorAnonimizadoEnTenantBAsync(ArnesDeArranqueRuntime arnes)
    {
        using var ambito = arnes.Servicios.CreateScope();
        var contexto = ambito.ServiceProvider.GetRequiredService<CaeManagerDbContext>();
        using var ambitoTenant = AmbitoTenantExplicito.Establecer(_tenantB);

        var empresa = new Empresa("Contrata Tenant B S.L.", "B87654323");
        contexto.Empresas.Add(empresa);
        await contexto.SaveChangesAsync();

        var casilda = Trabajador.DeEmpresa(
            empresa.Id, "Casilda", "Ferrer Gil", "33333333P", new DateOnly(1990, 1, 2),
            "casilda.ferrer@ejemplo.test", null, null, "+34600555666", "Electricista");
        contexto.Trabajadores.Add(casilda);
        await contexto.SaveChangesAsync();

        casilda.Anonimizar(DateTime.UtcNow);
        await contexto.SaveChangesAsync();

        return casilda.Id;
    }

    private async Task AsignarConBajaAsync(ArnesDeArranqueRuntime arnes, Guid trabajadorId, Guid centroId, DateOnly fechaBaja)
    {
        using var ambito = arnes.Servicios.CreateScope();
        var contexto = ambito.ServiceProvider.GetRequiredService<CaeManagerDbContext>();
        using var ambitoTenant = AmbitoTenantExplicito.Establecer(_tenantA);

        var asignacion = new Asignacion(trabajadorId, centroId, fechaBaja.AddYears(-1));
        asignacion.DarDeBaja(fechaBaja);
        contexto.Asignaciones.Add(asignacion);
        await contexto.SaveChangesAsync();
    }

    private async Task FijarNotaInternaAsync(ArnesDeArranqueRuntime arnes, Guid trabajadorId, string nota)
    {
        using var ambito = arnes.Servicios.CreateScope();
        var contexto = ambito.ServiceProvider.GetRequiredService<CaeManagerDbContext>();
        using var ambitoTenant = AmbitoTenantExplicito.Establecer(_tenantA);

        var trabajador = await contexto.Trabajadores.SingleAsync(t => t.Id == trabajadorId);
        trabajador.FijarNotaInterna(nota);
        await contexto.SaveChangesAsync();
    }

    private async Task FijarContactoEmergenciaAsync(ArnesDeArranqueRuntime arnes, Guid trabajadorId, string nombre, string telefono)
    {
        using var ambito = arnes.Servicios.CreateScope();
        var contexto = ambito.ServiceProvider.GetRequiredService<CaeManagerDbContext>();
        using var ambitoTenant = AmbitoTenantExplicito.Establecer(_tenantA);

        var trabajador = await contexto.Trabajadores.SingleAsync(t => t.Id == trabajadorId);
        trabajador.FijarContactoEmergencia(nombre, telefono);
        await contexto.SaveChangesAsync();
    }

    /// <summary>Anonimiza por EF, sin pasar por la purga ni llamar a la función.</summary>
    private async Task AnonimizarSinEnmascararAsync(ArnesDeArranqueRuntime arnes, Guid trabajadorId)
    {
        using var ambito = arnes.Servicios.CreateScope();
        var contexto = ambito.ServiceProvider.GetRequiredService<CaeManagerDbContext>();
        using var ambitoTenant = AmbitoTenantExplicito.Establecer(_tenantA);

        var trabajador = await contexto.Trabajadores.SingleAsync(t => t.Id == trabajadorId);
        trabajador.Anonimizar(DateTime.UtcNow);
        await contexto.SaveChangesAsync();
    }

    /// <summary>Detección, autorización y ejecución reales, en el Tenant A y bajo runtime.</summary>
    private async Task<int> EjecutarPurgaDeTrabajadoresAsync(ArnesDeArranqueRuntime arnes)
    {
        using var ambitoTenant = AmbitoTenantExplicito.Establecer(_tenantA);

        Guid solicitudId;
        using (var ambito = arnes.Servicios.CreateScope())
        {
            var contexto = ambito.ServiceProvider.GetRequiredService<CaeManagerDbContext>();
            var deteccion = new DeteccionPurgaService(
                contexto, contexto, contexto, new SolicitudPurgaRepository(contexto),
                Microsoft.Extensions.Options.Options.Create(new RetencionDatosOptions
                {
                    AniosRetencionDocumentos = 5,
                    AniosRetencionTrabajadores = 5,
                }),
                ambito.ServiceProvider.GetRequiredService<ITenantActual>(), contexto);
            await deteccion.DetectarAsync(_hoy);

            var solicitud = await contexto.SolicitudesPurga.SingleAsync(s => s.TipoDato == TipoDatoPurgable.TrabajadoresDadosDeBaja);
            solicitud.Programar(_hoy, _usuario, _hoy);
            await contexto.SaveChangesAsync();
            solicitudId = solicitud.Id;
        }

        using (var ambito = arnes.Servicios.CreateScope())
        {
            var contexto = ambito.ServiceProvider.GetRequiredService<CaeManagerDbContext>();
            var ejecucion = new EjecucionPurgaService(
                contexto, contexto, contexto, new SolicitudPurgaRepository(contexto),
                new ExtraccionIaCacheRepository(contexto), new AlmacenamientoSinArchivos(),
                ambito.ServiceProvider.GetRequiredService<ITenantActual>(), contexto,
                new TransaccionDeComando(contexto), new EnmascaradoAuditoriaTrabajadorAnonimizado(contexto),
                new AlertaOperativaSinEfecto(), NullLogger<EjecucionPurgaService>.Instance);
            return await ejecucion.EjecutarAsync(solicitudId, _hoy);
        }
    }

    // ── Base de datos ────────────────────────────────────────────────────

    private static async Task<NpgsqlConnection> AbrirComoRuntimeAsync(ArnesDeArranqueRuntime arnes, Guid? tenant)
    {
        var conexion = new NpgsqlConnection(BaseDatosPostgresDePruebas.CadenaComoRuntime(arnes.CadenaPropietario));
        await conexion.OpenAsync();
        await using var fijar = conexion.CreateCommand();
        fijar.CommandText = "SELECT set_config('app.tenant_id', $1, false);";
        fijar.Parameters.AddWithValue(tenant?.ToString() ?? string.Empty);
        await fijar.ExecuteNonQueryAsync();
        return conexion;
    }

    private static async Task<int> EnmascararAsync(NpgsqlConnection runtime, Guid trabajadorId)
    {
        await using var orden = runtime.CreateCommand();
        orden.CommandText = $"SELECT public.{Funcion}($1);";
        orden.Parameters.AddWithValue(trabajadorId);
        return (int)(await orden.ExecuteScalarAsync())!;
    }

    /// <summary>Una fila escrita por el runtime con el INSERT que sí tiene, en el Tenant dado.</summary>
    private static async Task InsertarFilaComoRuntimeAsync(ArnesDeArranqueRuntime arnes, Guid tenant, Guid entidadId, string datos)
    {
        await using var runtime = await AbrirComoRuntimeAsync(arnes, tenant);
        await using var orden = runtime.CreateCommand();
        orden.CommandText =
            """
            INSERT INTO "RegistrosAuditoria" ("Id", "TenantId", "EntidadTipo", "EntidadId", "Accion", "DatosAntes", "DatosDespues", "UsuarioId", "TipoActor", "FechaUtc")
            VALUES (gen_random_uuid(), $1, 'Trabajador', $2, 'Modificado', $3, $3, NULL, 'Desconocido', now());
            """;
        orden.Parameters.AddWithValue(tenant);
        orden.Parameters.AddWithValue(entidadId);
        orden.Parameters.AddWithValue(datos);
        await orden.ExecuteNonQueryAsync();
    }

    private static async Task<List<FilaAuditoria>> LeerFilasDeTrabajadorAsync(ArnesDeArranqueRuntime arnes)
    {
        await using var propietario = new NpgsqlConnection(arnes.CadenaPropietario);
        await propietario.OpenAsync();
        await using var orden = new NpgsqlCommand(
            """
            SELECT "Id", "TenantId", "EntidadTipo", "EntidadId", "Accion", "DatosAntes", "DatosDespues",
                   "UsuarioId", "ActorRealUsuarioId", "ViaAcceso", "ViaAccesoId", "TipoActor", "FechaUtc"
            FROM "RegistrosAuditoria" WHERE "EntidadTipo" = 'Trabajador' ORDER BY "Id";
            """, propietario);
        await using var lector = await orden.ExecuteReaderAsync();
        var filas = new List<FilaAuditoria>();
        while (await lector.ReadAsync())
        {
            filas.Add(new FilaAuditoria(
                lector.GetGuid(0), lector.GetGuid(1), lector.GetString(2), lector.GetGuid(3), lector.GetString(4),
                lector.IsDBNull(5) ? null : lector.GetString(5), lector.IsDBNull(6) ? null : lector.GetString(6),
                lector.IsDBNull(7) ? null : lector.GetGuid(7), lector.IsDBNull(8) ? null : lector.GetGuid(8),
                lector.IsDBNull(9) ? null : lector.GetString(9), lector.IsDBNull(10) ? null : lector.GetGuid(10),
                lector.GetString(11), lector.GetDateTime(12)));
        }

        return filas;
    }

    private static bool ContieneAlguno(FilaAuditoria fila, IEnumerable<string> rastros) =>
        rastros.Any(r => (fila.DatosAntes?.Contains(r, StringComparison.Ordinal) ?? false)
                         || (fila.DatosDespues?.Contains(r, StringComparison.Ordinal) ?? false));

    /// <summary>
    /// Misma fila salvo los valores personales: actor, fecha, acción y el resto de columnas
    /// intactos; en los JSON, las mismas claves en el mismo orden, "***" en las personales y
    /// el valor de antes en las demás.
    /// </summary>
    private static void DebeSerLaMismaConDatosPersonalesEnmascarados(FilaAuditoria antes, FilaAuditoria despues)
    {
        despues.Should().BeEquivalentTo(antes, o => o.Excluding(f => f.DatosAntes).Excluding(f => f.DatosDespues));
        MismoJsonConPersonalesEnmascarados(antes.DatosAntes, despues.DatosAntes);
        MismoJsonConPersonalesEnmascarados(antes.DatosDespues, despues.DatosDespues);
    }

    private static void MismoJsonConPersonalesEnmascarados(string? antes, string? despues)
    {
        if (antes is null)
        {
            despues.Should().BeNull();
            return;
        }

        // Mismo formato compacto que escribe AuditoriaInterceptor: ObtenerAuditoriaQuery busca
        // el marcador literal "EstaEliminado":true para ofrecer Restaurar, y un JSON
        // reformateado (json_object_agg escribe "k" : v) lo perdería en silencio.
        despues.Should().NotContain("\" : ").And.NotContain(", \"");
        if (antes.Contains("\"EstaEliminado\":true", StringComparison.Ordinal))
            despues.Should().Contain("\"EstaEliminado\":true");

        var objetoAntes = JsonNode.Parse(antes)!.AsObject();
        var objetoDespues = JsonNode.Parse(despues!)!.AsObject();
        objetoDespues.Select(p => p.Key).Should().Equal(objetoAntes.Select(p => p.Key));

        foreach (var (clave, valor) in objetoAntes)
        {
            if (Trabajador.CamposDeDatosPersonales.Contains(clave))
                objetoDespues[clave]!.GetValue<string>().Should().Be("***", $"{clave} es un dato personal");
            else
                JsonNode.DeepEquals(objetoDespues[clave], valor).Should().BeTrue($"{clave} no es un dato personal y no cambia");
        }
    }

    private sealed record FilaAuditoria(
        Guid Id, Guid TenantId, string EntidadTipo, Guid EntidadId, string Accion, string? DatosAntes, string? DatosDespues,
        Guid? UsuarioId, Guid? ActorRealUsuarioId, string? ViaAcceso, Guid? ViaAccesoId, string TipoActor, DateTime FechaUtc);

    private sealed class ActorFijo(ActorAuditoria actor) : IActorAuditoria
    {
        public Task<ActorAuditoria> ObtenerAsync() => Task.FromResult(actor);

        public ActorAuditoria? ObtenerSiYaEstaResuelto() => actor;
    }

    private sealed class AlmacenamientoSinArchivos : IFileStorageService
    {
        public Task<string> GuardarAsync(Stream contenido, string nombreArchivoOriginal, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Stream> AbrirAsync(string identificador, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task EliminarAsync(string identificador, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class AlertaOperativaSinEfecto : IAlertaOperativa
    {
        public void Emitir(string mensaje, NivelAlertaOperativa nivel) { }

        public void CapturarExcepcion(Exception excepcion) { }

        public void DejarMigaDePan(string mensaje) { }

        public IDisposable IniciarAmbitoDeCaptura() => new AmbitoVacio();

        private sealed class AmbitoVacio : IDisposable
        {
            public void Dispose() { }
        }
    }
}
