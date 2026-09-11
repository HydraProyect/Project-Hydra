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
/// Hallazgo señalado por el Módulo 8 en su auditoría de persistencia y
/// trabajado desde el Módulo 9 (2026-08-30): el listado paginado de
/// Auditoría devolvía <c>DatosAntes</c>/<c>DatosDespues</c> —el snapshot JSON
/// completo de la entidad— en cada una de las filas cargadas, cuando la UI
/// solo necesita saber si hay archivo anterior o si el borrado es
/// reversible. <see cref="ObtenerAuditoriaQuery"/> ahora devuelve
/// <see cref="RegistroAuditoriaListaDto"/> con esos dos booleanos ya
/// calculados, sin el JSON. Estos casos fijan que el cálculo —movido desde
/// Auditoria.razor.cs a la Query— sigue produciendo el mismo resultado.
/// </summary>
public class ObtenerAuditoriaQueryListadoMinimizadoTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenant = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto(_tenant);
        await contexto.Database.MigrateAsync();
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task La_proyeccion_de_lista_no_expone_los_campos_JSON()
    {
        typeof(RegistroAuditoriaListaDto).GetProperty("DatosAntes").Should().BeNull();
        typeof(RegistroAuditoriaListaDto).GetProperty("DatosDespues").Should().BeNull();
    }

    [Fact]
    public async Task Un_documento_modificado_con_ArchivoUrl_anterior_marca_TieneArchivoAnterior()
    {
        var documentoId = Guid.NewGuid();
        await InsertarRegistroAsync(new RegistroAuditoria(
            "Documento", documentoId, "Modificado",
            datosAntes: """{"ArchivoUrl":"3f3e.../antiguo.pdf"}""",
            datosDespues: """{"ArchivoUrl":"3f3e.../nuevo.pdf"}""",
            usuarioId: null));

        var fila = await ObtenerFilaAsync(documentoId);

        fila.TieneArchivoAnterior.Should().BeTrue();
        fila.PuedeRestaurar.Should().BeFalse("el JSON de DatosDespues no marca EstaEliminado");
    }

    [Fact]
    public async Task Un_documento_modificado_sin_ArchivoUrl_previo_no_marca_TieneArchivoAnterior()
    {
        var documentoId = Guid.NewGuid();
        await InsertarRegistroAsync(new RegistroAuditoria(
            "Documento", documentoId, "Modificado",
            datosAntes: """{"Comentarios":"sin archivo"}""",
            datosDespues: null,
            usuarioId: null));

        var fila = await ObtenerFilaAsync(documentoId);

        fila.TieneArchivoAnterior.Should().BeFalse();
    }

    /// <summary>
    /// DEFECTO (revisión de Codex, 2026-09-11): antes de este cambio,
    /// PuedeRestaurar salía solo del JSON histórico de DatosDespues, sin
    /// mirar si la Empresa (ex-Cliente, ver RestaurarClienteCommand) sigue
    /// eliminada HOY. Este caso fija el camino feliz: sigue eliminada →
    /// sigue ofreciéndose "Restaurar". Los dos siguientes fijan los dos
    /// huecos que dejaba mirar solo el histórico.
    /// </summary>
    [Fact]
    public async Task Un_cliente_que_sigue_eliminado_hoy_marca_PuedeRestaurar()
    {
        var empresaId = await CrearEmpresaAsync(_tenant, eliminada: true);

        await InsertarRegistroAsync(new RegistroAuditoria(
            "Cliente", empresaId, "Modificado",
            datosAntes: """{"EstaEliminado":false}""",
            datosDespues: """{"EstaEliminado":true}""",
            usuarioId: null));

        var fila = await ObtenerFilaAsync(empresaId, accion: "Modificado");

        fila.PuedeRestaurar.Should().BeTrue();
    }

    /// <summary>
    /// El hueco que reportó Codex: el JSON de este cambio histórico dice
    /// EstaEliminado=true (fue una baja lógica real, en su momento), pero la
    /// Empresa ya se restauró después — el botón no debería seguir
    /// ofreciendo una restauración que RestaurarClienteCommand va a
    /// rechazar.
    /// </summary>
    [Fact]
    public async Task Una_empresa_ya_restaurada_no_marca_PuedeRestaurar_aunque_el_historico_diga_EstaEliminado()
    {
        var empresaId = await CrearEmpresaAsync(_tenant, eliminada: false);

        await InsertarRegistroAsync(new RegistroAuditoria(
            "Empresa", empresaId, "Modificado",
            datosAntes: """{"EstaEliminado":false}""",
            datosDespues: """{"EstaEliminado":true}""",
            usuarioId: null));

        var fila = await ObtenerFilaAsync(empresaId, accion: "Modificado");

        fila.PuedeRestaurar.Should().BeFalse("la empresa ya se restauró después de este cambio histórico");
    }

    /// <summary>
    /// Frontera de aislamiento: una Empresa eliminada con el mismo Id pero de
    /// OTRO tenant nunca debe contar como "sigue eliminada" para el tenant
    /// que consulta su propia auditoría — el mismo tipo de fuga que
    /// RestaurarClienteCommand evita comparando TenantId a mano tras
    /// IgnoreQueryFilters().
    /// </summary>
    [Fact]
    public async Task Una_empresa_eliminada_de_otro_tenant_nunca_marca_PuedeRestaurar()
    {
        var otroTenant = Guid.NewGuid();
        var empresaIdOtroTenant = await CrearEmpresaAsync(otroTenant, eliminada: true);

        await InsertarRegistroAsync(new RegistroAuditoria(
            "Empresa", empresaIdOtroTenant, "Modificado",
            datosAntes: """{"EstaEliminado":false}""",
            datosDespues: """{"EstaEliminado":true}""",
            usuarioId: null));

        var fila = await ObtenerFilaAsync(empresaIdOtroTenant, accion: "Modificado");

        fila.PuedeRestaurar.Should().BeFalse("la empresa eliminada pertenece a otro tenant");
    }

    [Fact]
    public async Task Una_entidad_no_restaurable_no_marca_PuedeRestaurar_aunque_el_JSON_diga_EstaEliminado()
    {
        // TipoDocumento no está en EntidadesRestaurables (no tiene
        // Restaurar*Command) — el filtro por tipo tiene que cortar antes de
        // mirar el JSON, no solo depender de lo que el JSON diga.
        var tipoDocumentoId = Guid.NewGuid();
        await InsertarRegistroAsync(new RegistroAuditoria(
            "TipoDocumento", tipoDocumentoId, "Modificado",
            datosAntes: null,
            datosDespues: """{"EstaEliminado":true}""",
            usuarioId: null));

        var fila = await ObtenerFilaAsync(tipoDocumentoId);

        fila.PuedeRestaurar.Should().BeFalse();
    }

    [Fact]
    public async Task Un_JSON_malformado_no_revienta_la_consulta_y_se_trata_como_no_restaurable()
    {
        var clienteId = Guid.NewGuid();
        await InsertarRegistroAsync(new RegistroAuditoria(
            "Cliente", clienteId, "Modificado",
            datosAntes: "esto no es JSON",
            datosDespues: "tampoco esto",
            usuarioId: null));

        var fila = await ObtenerFilaAsync(clienteId);

        fila.PuedeRestaurar.Should().BeFalse();
        fila.TieneArchivoAnterior.Should().BeFalse();
    }

    [Fact]
    public async Task Una_creacion_no_marca_ni_restaurar_ni_archivo_anterior_aunque_el_tipo_encaje()
    {
        // "Creado" no es "Modificado": ninguno de los dos booleanos debería
        // depender solo de que el tipo de entidad encaje.
        var documentoId = Guid.NewGuid();
        await InsertarRegistroAsync(new RegistroAuditoria(
            "Documento", documentoId, "Creado",
            datosAntes: null,
            datosDespues: """{"ArchivoUrl":"3f3e.../nuevo.pdf","EstaEliminado":false}""",
            usuarioId: null));

        var fila = await ObtenerFilaAsync(documentoId);

        fila.PuedeRestaurar.Should().BeFalse();
        fila.TieneArchivoAnterior.Should().BeFalse();
    }

    private async Task InsertarRegistroAsync(RegistroAuditoria registro)
    {
        await using var contexto = CrearContexto(_tenant);
        contexto.RegistrosAuditoria.Add(registro);
        await contexto.SaveChangesAsync();
    }

    /// <summary>
    /// Crea una Empresa bajo <paramref name="tenantId"/> (el interceptor de
    /// sellado le pone ese TenantId al guardar) con el estado ACTUAL que
    /// cada test necesita cruzar contra el histórico del registro de
    /// auditoría que se inserta aparte. Para "ya restaurada" no se llama a
    /// <c>MarcarComoEliminado</c>+<c>Restaurar</c> sobre la misma fila: eso
    /// generaría un segundo registro "Modificado" automático (vía
    /// AuditoriaInterceptor) con el mismo EntidadId, ambiguo frente al
    /// "Modificado" que el test inserta a mano. Crearla directamente en el
    /// estado final basta — la query solo mira <c>EstaEliminado</c> hoy, no
    /// cuántas veces cambió. El alta sí deja su propio "Creado", pero esa
    /// accion nunca coincide con el "Modificado" que busca cada test.
    /// </summary>
    private async Task<Guid> CrearEmpresaAsync(Guid tenantId, bool eliminada)
    {
        var empresa = new Empresa("Empresa de prueba SL");
        if (eliminada)
            empresa.MarcarComoEliminado(Guid.NewGuid());

        await using var contexto = CrearContexto(tenantId);
        contexto.Empresas.Add(empresa);
        await contexto.SaveChangesAsync();

        return empresa.Id;
    }

    /// <summary>
    /// <paramref name="accion"/> desambigua cuando el mismo EntidadId tiene
    /// más de una fila (p. ej. el "Creado" automático de
    /// <see cref="CrearEmpresaEliminadaAsync"/> junto al "Modificado" que
    /// inserta el test) — sin filtro, sigue devolviendo la única fila de los
    /// tests que no crean una entidad real.
    /// </summary>
    private async Task<RegistroAuditoriaListaDto> ObtenerFilaAsync(Guid entidadId, string? accion = null)
    {
        await using var contexto = CrearContexto(_tenant);
        var tenantActual = new TenantActualAmbiental { TenantId = _tenant };
        var handler = new ObtenerAuditoriaQueryHandler(contexto, contexto, contexto, contexto, contexto, tenantActual);

        var resultado = await handler.Handle(
            new ObtenerAuditoriaQuery(EntidadTipo: null, UsuarioId: null, Pagina: 1, TamanoPagina: 30),
            CancellationToken.None);

        var candidatas = resultado.Elementos.Where(r => r.EntidadId == entidadId);
        return accion is null ? candidatas.Single() : candidatas.Single(r => r.Accion == accion);
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
