using CaeManager.Application.Auditoria.Queries;
using CaeManager.Domain.Auditoria;
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
        await using var contexto = CrearContexto();
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

    [Fact]
    public async Task Un_cliente_borrado_logicamente_marca_PuedeRestaurar()
    {
        var clienteId = Guid.NewGuid();
        await InsertarRegistroAsync(new RegistroAuditoria(
            "Cliente", clienteId, "Modificado",
            datosAntes: """{"EstaEliminado":false}""",
            datosDespues: """{"EstaEliminado":true}""",
            usuarioId: null));

        var fila = await ObtenerFilaAsync(clienteId);

        fila.PuedeRestaurar.Should().BeTrue();
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
        await using var contexto = CrearContexto();
        contexto.RegistrosAuditoria.Add(registro);
        await contexto.SaveChangesAsync();
    }

    private async Task<RegistroAuditoriaListaDto> ObtenerFilaAsync(Guid entidadId)
    {
        await using var contexto = CrearContexto();
        var handler = new ObtenerAuditoriaQueryHandler(contexto);

        var resultado = await handler.Handle(
            new ObtenerAuditoriaQuery(EntidadTipo: null, UsuarioId: null, Pagina: 1, TamanoPagina: 30),
            CancellationToken.None);

        return resultado.Elementos.Single(r => r.EntidadId == entidadId);
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
