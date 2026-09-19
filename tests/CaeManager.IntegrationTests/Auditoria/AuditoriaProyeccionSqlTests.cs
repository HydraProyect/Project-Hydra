using System.Text.Json;
using CaeManager.Application.Auditoria.Queries;
using CaeManager.Domain.Auditoria;
using CaeManager.Domain.Empresas;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Auditoria;

/// <summary>
/// Módulo 8 § 4.1: el listado de Auditoría traía <c>DatosAntes</c>/<c>DatosDespues</c>
/// —el snapshot JSON completo— de cada fila de la página solo para calcular
/// <c>PuedeRestaurar</c>/<c>TieneArchivoAnterior</c> con
/// <c>JsonDocument.Parse</c>, y descartaba el resto.
/// <see cref="ObtenerAuditoriaQueryHandler"/> ahora traduce esos dos booleanos
/// a un <c>Contains</c> de C# sobre el TEXT (que EF Core empuja a SQL como
/// <c>LIKE</c>/<c>strpos</c>), sin traer la columna completa.
///
/// <see cref="ObtenerAuditoriaQueryListadoMinimizadoTests"/> ya fija el
/// resultado observable con una decena de casos concretos. Este archivo prueba
/// la propiedad de fondo que ese substring podría romper en silencio: que
/// coincide con <c>JsonDocument.Parse</c> —la fuente de verdad real de "qué
/// dice el JSON"— para una muestra amplia y variada de payloads, incluidos los
/// que un <c>Contains</c> ingenuo podría confundir (propiedades con nombre
/// parecido, JSON anidado, valores en otras posiciones).
/// </summary>
public class AuditoriaProyeccionSqlTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenant = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto();
        await contexto.Database.MigrateAsync();
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

#pragma warning disable CS8625 // null literal en object[] — DatosAntes/DatosDespues son legítimamente null en varios casos.
    public static IEnumerable<object[]> Casos()
    {
        // (EntidadTipo, Accion, DatosAntes, DatosDespues)
        yield return ["Documento", "Modificado", """{"ArchivoUrl":"a/b.pdf","Comentarios":"nota"}""", """{"ArchivoUrl":"a/c.pdf"}"""];
        yield return ["Documento", "Modificado", """{"Comentarios":"sin url"}""", null];
        // Otra propiedad que CONTIENE la palabra "ArchivoUrl" en el valor, no
        // en la clave — un Contains descuidado sobre el texto entero (en vez
        // de sobre el patrón "clave":" ) podría confundirlo con la propiedad real.
        yield return ["Documento", "Modificado", """{"Comentarios":"referencia a ArchivoUrl en texto libre"}""", null];
        // "EstaEliminado" con valor false: no debe contar como candidato.
        yield return ["Cliente", "Modificado", """{"EstaEliminado":true}""", """{"EstaEliminado":false}"""];
        // Un prefijo de propiedad que casi coincide ("EstaEliminadoPorLote")
        // no debe confundirse con "EstaEliminado" a secas.
        yield return ["Empresa", "Modificado", null, """{"EstaEliminadoPorLote":true}"""];
        yield return ["Empresa", "Modificado", null, """{"Otro":1,"EstaEliminado":true,"Mas":"x"}"""];
        yield return ["Centro", "Modificado", null, """{"EstaEliminado":true}"""];
        yield return ["TipoDocumento", "Modificado", null, """{"EstaEliminado":true}"""]; // no restaurable
        yield return ["Trabajador", "Creado", null, """{"EstaEliminado":true}"""]; // no es Modificado
        yield return ["Trabajador", "Modificado", null, null];
        yield return ["Documento", "Modificado", "esto no es JSON", "tampoco esto"];
        yield return ["Vehiculo", "Modificado", null, """{"EstaEliminado":true}"""]; // no restaurable
        // Hallazgo de Codex (revisión previa a esta PR): un TEXT corrupto que
        // ni siquiera empieza por "{" pero contiene el marcador en texto
        // libre. JsonDocument.Parse lo rechaza (no es JSON); el StartsWith("{")
        // añadido tras el hallazgo también lo rechaza — antes de esa
        // corrección, un Contains puro lo habría marcado candidato.
        yield return ["Cliente", "Modificado", null, """ruido antes {"EstaEliminado":true} ruido después"""];
    }
#pragma warning restore CS8625

    [Theory]
    [MemberData(nameof(Casos))]
    public async Task El_booleano_calculado_en_SQL_coincide_con_JsonDocument_Parse(
        string entidadTipo, string accion, string? datosAntes, string? datosDespues)
    {
        var entidadId = Guid.NewGuid();
        await using (var contextoEscritura = CrearContexto())
        {
            contextoEscritura.RegistrosAuditoria.Add(new RegistroAuditoria(
                entidadTipo, entidadId, accion, datosAntes, datosDespues, usuarioId: null));
            await contextoEscritura.SaveChangesAsync();
        }

        await using var contextoLectura = CrearContexto();
        var handler = new ObtenerAuditoriaQueryHandler(
            contextoLectura, contextoLectura, contextoLectura, contextoLectura, contextoLectura,
            new TenantActualAmbiental { TenantId = _tenant });

        var resultado = await handler.Handle(
            new ObtenerAuditoriaQuery(EntidadTipo: entidadTipo, UsuarioId: null, Pagina: 1, TamanoPagina: 10),
            CancellationToken.None);

        var fila = resultado.Elementos.Single(r => r.EntidadId == entidadId);

        fila.TieneArchivoAnterior.Should().Be(
            TieneArchivoAnteriorOraculo(entidadTipo, accion, datosAntes),
            "TieneArchivoAnterior calculado en SQL debe coincidir con JsonDocument.Parse");

        // PuedeRestaurar además cruza con el estado ACTUAL (ObtenerEliminadasActualmenteAsync,
        // sin tocar en este incremento); aquí no hay ninguna entidad real
        // creada, así que el oráculo puro de "candidato histórico" solo puede
        // dar un resultado observable cuando es FALSE (si fuera candidato,
        // PuedeRestaurar seguiría siendo false porque la entidad referenciada
        // no existe y por tanto nunca "sigue eliminada hoy").
        var esCandidatoHistorico = EsCandidataHistoricaOraculo(entidadTipo, accion, datosDespues);
        if (!esCandidatoHistorico)
            fila.PuedeRestaurar.Should().BeFalse();
    }

    /// <summary>
    /// Hueco residual DECLARADO (hallazgo de Codex, revisión previa a esta
    /// PR): <c>StartsWith("{")</c> descarta un TEXT que ni siquiera empieza
    /// como JSON, pero no valida el resto — un TEXT truncado que empieza por
    /// "{" y contiene el marcador sigue dando positivo aquí, mientras que
    /// <c>JsonDocument.Parse</c> lo habría rechazado por JSON inválido.
    /// Cerrarlo del todo exigiría traer la columna completa a la aplicación,
    /// justo lo que este incremento evita, y esta columna solo la escribe
    /// <c>AuditoriaInterceptor</c> (JSON válido siempre) — el caso solo se da
    /// ante corrupción manual de la base. Este test fija el comportamiento
    /// ACEPTADO (no lo esconde): si algún día deja de ser aceptable, aquí es
    /// donde hay que mirar.
    /// </summary>
    [Fact]
    public async Task Hueco_declarado_JSON_truncado_que_empieza_por_llave_con_el_marcador_sigue_dando_falso_positivo()
    {
        const string datosDespues = """{"EstaEliminado":true"""; // sin cerrar — JSON inválido a propósito
        EsCandidataHistoricaOraculo("Cliente", "Modificado", datosDespues).Should().BeFalse(
            "JsonDocument.Parse rechaza el JSON sin cerrar — este es el comportamiento que ya NO se reproduce en SQL");

        // Empresa real y eliminada HOY, para que el falso positivo del
        // candidato interno se vuelva observable en PuedeRestaurar (si no
        // existiera, ObtenerEliminadasActualmenteAsync no la encontraría y
        // PuedeRestaurar daría false de todos modos, ocultando el hueco).
        Guid empresaId;
        await using (var contextoEscritura = CrearContexto())
        {
            var empresa = new Empresa("Empresa de prueba SL");
            empresa.MarcarComoEliminado(Guid.NewGuid());
            contextoEscritura.Empresas.Add(empresa);
            await contextoEscritura.SaveChangesAsync();
            empresaId = empresa.Id;

            contextoEscritura.RegistrosAuditoria.Add(new RegistroAuditoria(
                "Cliente", empresaId, "Modificado", datosAntes: null, datosDespues, usuarioId: null));
            await contextoEscritura.SaveChangesAsync();
        }

        await using var contextoLectura = CrearContexto();
        var handler = new ObtenerAuditoriaQueryHandler(
            contextoLectura, contextoLectura, contextoLectura, contextoLectura, contextoLectura,
            new TenantActualAmbiental { TenantId = _tenant });

        var resultado = await handler.Handle(
            new ObtenerAuditoriaQuery(EntidadTipo: "Cliente", UsuarioId: null, Pagina: 1, TamanoPagina: 10),
            CancellationToken.None);

        resultado.Elementos.Single(r => r.EntidadId == empresaId).PuedeRestaurar.Should().BeTrue(
            "hueco declarado: StartsWith(\"{\")+Contains no valida el JSON entero, así que un TEXT truncado " +
            "que empiece por \"{\" y contenga el marcador sigue marcando PuedeRestaurar, aunque " +
            "JsonDocument.Parse lo habría rechazado por inválido");
    }

    /// <summary>
    /// Hallazgo P1 de Codex (4ª ronda, tras abrir esta PR): el listado no
    /// exponía <c>RoleId</c>, así que no se podía distinguir una concesión de
    /// Administrador de una de Consulta. Mismo criterio que el resto de este
    /// archivo: probar la extracción de <c>ObtenerAuditoriaQueryHandler.ExtraerRolId</c>
    /// contra Postgres real, con la traducción SQL del <c>CASE WHEN</c> +
    /// <c>COALESCE</c> de <c>JsonRolDeUsuario</c> incluida — no solo el método
    /// en aislamiento, que no observaría un fallo de traducción EF-a-SQL.
    /// </summary>
    /// <summary>
    /// (Accion, DatosAntes, DatosDespues, RolIdEsperado) de cada caso,
    /// resuelto por índice desde <see cref="ObtenerCasoRolId"/> — ver el
    /// comentario de ese método para el porqué del índice en vez del
    /// contenido directo como argumento del <c>Theory</c>.
    /// </summary>
    private static (string Accion, string? DatosAntes, string? DatosDespues, Guid? RolIdEsperado) ObtenerCasoRolId(int indice)
    {
        var rolId = Guid.NewGuid();
        return indice switch
        {
            // Creado (alta): el JSON va en DatosDespues, nunca en DatosAntes.
            0 => ("Creado", null, $$"""{"UserId":"{{Guid.NewGuid()}}","RoleId":"{{rolId}}"}""", rolId),
            // Eliminado (revocar): el JSON va en DatosAntes, DatosDespues es null.
            1 => ("Eliminado", $$"""{"UserId":"{{Guid.NewGuid()}}","RoleId":"{{rolId}}"}""", null, rolId),
            // Orden de propiedades invertido — el marcador no depende de la posición.
            2 => ("Creado", null, $$"""{"RoleId":"{{rolId}}","UserId":"{{Guid.NewGuid()}}"}""", rolId),
            // JSON sin el marcador (fila anterior a este cambio, o corrupción manual): null, no una excepción.
            3 => ("Creado", null, """{"UserId":"11111111-1111-1111-1111-111111111111"}""", null),
            _ => throw new ArgumentOutOfRangeException(nameof(indice)),
        };
    }

    /// <summary>
    /// Hallazgo de infraestructura de CI (revisión de sesión coordinadora
    /// sobre `8982cdc8`): la versión anterior de este generador pasaba el
    /// JSON con GUIDs aleatorios directamente como argumento del
    /// <c>Theory</c>. <c>dotnet test --list-tests</c> deriva el nombre de
    /// cada caso a partir de sus argumentos, y con ese JSON largo (los
    /// backslashes de escape casi duplican su longitud aparente en el
    /// nombre mostrado) el descubrimiento colapsaba los 4 casos en una sola
    /// entrada genérica sin argumentos — detectado porque el check
    /// "Build, format y tests" del CI contaba 1258 tests descubiertos frente
    /// a 1261 realmente ejecutados por los 4 bloques, ya que
    /// <c>scripts/repartir-clases-de-test.sh</c> usa ese mismo listado para
    /// calcular el reparto. Pasar solo el ÍNDICE (un <c>int</c>) como
    /// argumento del <c>Theory</c> deja el nombre corto y determinista, sin
    /// depender de cuánto quepa en el JSON de cada caso.
    /// </summary>
    public static IEnumerable<object[]> CasosRolId() => [[0], [1], [2], [3]];

    [Theory]
    [MemberData(nameof(CasosRolId))]
    public async Task El_RolId_se_extrae_del_JSON_de_RolDeUsuario_vía_SQL_y_en_memoria(int indiceDeCaso)
    {
        var (accion, datosAntes, datosDespues, rolIdEsperado) = ObtenerCasoRolId(indiceDeCaso);
        var entidadId = Guid.NewGuid();
        await using (var contextoEscritura = CrearContexto())
        {
            contextoEscritura.RegistrosAuditoria.Add(new RegistroAuditoria(
                "RolDeUsuario", entidadId, accion, datosAntes, datosDespues, usuarioId: null));
            await contextoEscritura.SaveChangesAsync();
        }

        await using var contextoLectura = CrearContexto();
        var handler = new ObtenerAuditoriaQueryHandler(
            contextoLectura, contextoLectura, contextoLectura, contextoLectura, contextoLectura,
            new TenantActualAmbiental { TenantId = _tenant });

        var resultado = await handler.Handle(
            new ObtenerAuditoriaQuery(EntidadTipo: "RolDeUsuario", UsuarioId: null, Pagina: 1, TamanoPagina: 10),
            CancellationToken.None);

        resultado.Elementos.Single(r => r.EntidadId == entidadId).RolId.Should().Be(rolIdEsperado);
    }

    /// <summary>
    /// Control negativo: para cualquier OTRO EntidadTipo, <c>RolId</c> es
    /// siempre <c>null</c> aunque el JSON contenga el marcador por casualidad
    /// — <c>JsonRolDeUsuario</c> solo se computa cuando EntidadTipo es
    /// exactamente "RolDeUsuario" (ver su comentario en el handler).
    /// </summary>
    [Fact]
    public async Task El_RolId_es_siempre_null_para_entidades_que_no_son_RolDeUsuario()
    {
        var entidadId = Guid.NewGuid();
        var rolId = Guid.NewGuid();
        await using (var contextoEscritura = CrearContexto())
        {
            contextoEscritura.RegistrosAuditoria.Add(new RegistroAuditoria(
                "Usuario", entidadId, "Modificado", datosAntes: null,
                $$"""{"RoleId":"{{rolId}}"}""", usuarioId: null));
            await contextoEscritura.SaveChangesAsync();
        }

        await using var contextoLectura = CrearContexto();
        var handler = new ObtenerAuditoriaQueryHandler(
            contextoLectura, contextoLectura, contextoLectura, contextoLectura, contextoLectura,
            new TenantActualAmbiental { TenantId = _tenant });

        var resultado = await handler.Handle(
            new ObtenerAuditoriaQuery(EntidadTipo: "Usuario", UsuarioId: null, Pagina: 1, TamanoPagina: 10),
            CancellationToken.None);

        resultado.Elementos.Single(r => r.EntidadId == entidadId).RolId.Should().BeNull(
            "JsonRolDeUsuario solo se computa para EntidadTipo == \"RolDeUsuario\", nunca para \"Usuario\"");
    }

    private static readonly HashSet<string> EntidadesRestaurables = ["Cliente", "Empresa", "Centro", "Trabajador", "Documento"];

    private static bool EsCandidataHistoricaOraculo(string entidadTipo, string accion, string? datosDespues)
    {
        if (!EntidadesRestaurables.Contains(entidadTipo) || accion != "Modificado" || datosDespues is null)
            return false;
        try
        {
            using var documento = JsonDocument.Parse(datosDespues);
            return documento.RootElement.TryGetProperty("EstaEliminado", out var valor) && valor.ValueKind == JsonValueKind.True;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TieneArchivoAnteriorOraculo(string entidadTipo, string accion, string? datosAntes)
    {
        if (entidadTipo != "Documento" || accion != "Modificado" || datosAntes is null)
            return false;
        try
        {
            using var documento = JsonDocument.Parse(datosAntes);
            return documento.RootElement.TryGetProperty("ArchivoUrl", out var valor) && valor.ValueKind == JsonValueKind.String;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private CaeManagerDbContext CrearContexto()
    {
        var tenantActual = new TenantActualAmbiental { TenantId = _tenant };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }
}
