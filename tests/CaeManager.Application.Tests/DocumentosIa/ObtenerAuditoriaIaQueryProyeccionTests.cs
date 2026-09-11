using CaeManager.Application.DocumentosIa.Queries;
using CaeManager.Domain.DocumentosIa;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.DocumentosIa;

/// <summary>
/// La pantalla de Auditoría IA (Gen 2) necesita reconstruir "con qué versión de
/// pipeline, qué modelo exacto y bajo qué identificador de petición se produjo
/// esta extracción" (ver el comentario de reproducibilidad en
/// <see cref="AuditoriaExtraccionIa"/>). Esos campos ya se escriben en el
/// dominio; esta suite comprueba que <see cref="ObtenerAuditoriaIaQueryHandler"/>
/// de verdad los proyecta al DTO en vez de dejarlos en la entidad sin usar.
/// </summary>
public class ObtenerAuditoriaIaQueryProyeccionTests
{
    [Fact]
    public async Task Proyecta_version_de_pipeline_modelo_exacto_peticion_y_proveedores_invocados()
    {
        var auditoria = AuditoriaExtraccionIa.Crear(
            hashSha256: new string('a', 64), tipoEsperado: "Certificado", proveedorCodigo: "anthropic",
            tiempoProcesamientoMs: 1200, costeEstimadoOcr: null, costeEstimado: 0.02m,
            numeroPaginas: 3, confianzaGeneral: 92, incidencias: null,
            documentoId: null, modeloExacto: "claude-sonnet-5-20260115",
            requestId: "req_9f0ef40e1234abcd", proveedoresInvocados: "anthropic,gemini");
        var contexto = new AuditoriaIaQueryContextFalso();
        contexto.ListaAuditoriasExtraccionIa.Add(auditoria);
        var handler = new ObtenerAuditoriaIaQueryHandler(contexto);

        var resultado = await handler.Handle(new ObtenerAuditoriaIaQuery(ProveedorCodigo: null), CancellationToken.None);

        var registro = resultado.Elementos.Should().ContainSingle().Which;
        registro.VersionPipeline.Should().Be(ExtraccionIaCache.VersionPipelineActual);
        registro.ModeloExacto.Should().Be("claude-sonnet-5-20260115");
        registro.RequestId.Should().Be("req_9f0ef40e1234abcd");
        registro.ProveedoresInvocados.Should().Be("anthropic,gemini");
    }

    [Fact]
    public async Task Sin_llamada_a_ningun_proveedor_modelo_peticion_y_proveedores_quedan_null()
    {
        var auditoria = AuditoriaExtraccionIa.Crear(
            hashSha256: new string('b', 64), tipoEsperado: "Certificado", proveedorCodigo: "cache",
            tiempoProcesamientoMs: 0, costeEstimadoOcr: null, costeEstimado: 0m,
            numeroPaginas: 3, confianzaGeneral: 0, incidencias: null);
        var contexto = new AuditoriaIaQueryContextFalso();
        contexto.ListaAuditoriasExtraccionIa.Add(auditoria);
        var handler = new ObtenerAuditoriaIaQueryHandler(contexto);

        var resultado = await handler.Handle(new ObtenerAuditoriaIaQuery(ProveedorCodigo: null), CancellationToken.None);

        var registro = resultado.Elementos.Should().ContainSingle().Which;
        registro.VersionPipeline.Should().Be(ExtraccionIaCache.VersionPipelineActual, "se registra siempre, incluso en caché");
        registro.ModeloExacto.Should().BeNull();
        registro.RequestId.Should().BeNull();
        registro.ProveedoresInvocados.Should().BeNull();
    }
}
