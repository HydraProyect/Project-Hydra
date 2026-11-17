using CaeManager.Application.Common;
using CaeManager.Application.Documentos;
using CaeManager.Application.Importacion;
using CaeManager.Application.Importacion.Commands.EjecutarImportacion;
using CaeManager.Application.Tests.Importacion;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Documentos;

/// <summary>
/// D-12: quién sube cada versión. La regla (<see cref="AutorDeLaSubida"/>) sale de la identidad de auditoría y separa
/// el usuario efectivo del Actor real; cada productor con persona detrás (Crear, Renovar con archivo nuevo, Importar;
/// Generar desde plantilla tiene el suyo en <c>GenerarDocumentoIndividualCommandHandlerTests</c>) la aplica a la versión
/// que nace. Lo que llega por Crear o Renovar (subida masiva con IA, reparto de adjuntos del buzón, división de PDF,
/// formulario y panel del documento) hereda el autor de esos dos handlers.
/// </summary>
public class AutorDeLaSubidaTests
{
    private static readonly Guid ActorReal = Guid.NewGuid();
    private static readonly Guid Simulado = Guid.NewGuid();

    private static readonly ActorAuditoria EnImpersonacion =
        new(ActorReal, Simulado, TipoViaAcceso.SesionPrivilegiada, Guid.NewGuid());

    // ── La regla ───────────────────────────────────────────────────────────

    [Fact]
    public void Fuera_de_una_impersonacion_el_usuario_efectivo_y_el_Actor_real_son_la_misma_persona()
    {
        var autor = AutorDeLaSubida.Desde(ActorAuditoria.Normal(ActorReal));

        autor.UsuarioId.Should().Be(ActorReal);
        autor.ActorRealUsuarioId.Should().Be(ActorReal);
    }

    [Fact]
    public void En_una_impersonacion_el_usuario_efectivo_es_el_Usuario_simulado_y_el_Actor_real_quien_teclea()
    {
        var autor = AutorDeLaSubida.Desde(EnImpersonacion);

        autor.UsuarioId.Should().Be(Simulado);
        autor.ActorRealUsuarioId.Should().Be(ActorReal);
    }

    [Theory]
    [InlineData(TipoViaAcceso.SesionPrivilegiada)]
    [InlineData(TipoViaAcceso.OperacionDelegada)]
    [InlineData(TipoViaAcceso.EncargoAdministracion)]
    public void Sin_Usuario_simulado_cualquier_via_firma_al_Actor_real_en_las_dos_columnas(TipoViaAcceso via)
    {
        var autor = AutorDeLaSubida.Desde(new ActorAuditoria(ActorReal, null, via, Guid.NewGuid()));

        autor.Should().Be(AutorVersionDocumento.Persona(ActorReal, ActorReal));
    }

    [Fact]
    public void Sin_identidad_resuelta_la_version_queda_sin_autor()
    {
        AutorDeLaSubida.Desde(ActorAuditoria.SinResolver).Should().Be(AutorVersionDocumento.Desconocido);
        AutorDeLaSubida.Desde(ActorAuditoria.Normal(Guid.Empty)).Should().Be(AutorVersionDocumento.Desconocido);
    }

    [Fact]
    public void Un_proceso_de_sistema_no_es_autor_aunque_lleve_identidad()
    {
        using var _ = AmbitoActorAuditoria.EstablecerSistema();

        AutorDeLaSubida.Desde(ActorAuditoria.Normal(ActorReal)).Should().Be(AutorVersionDocumento.Desconocido);
    }

    [Fact]
    public void Una_integracion_externa_no_es_autora_aunque_traiga_el_Id_de_su_clave_como_identidad()
    {
        using var _ = AmbitoActorAuditoria.EstablecerIntegracionExterna();

        AutorDeLaSubida.Desde(ActorAuditoria.Normal(ActorReal)).Should().Be(AutorVersionDocumento.Desconocido);
    }

    // ── Los productores ────────────────────────────────────────────────────

    [Fact]
    public async Task Crear_un_documento_en_impersonacion_lo_firma_con_el_Usuario_simulado_y_el_Actor_real()
    {
        var (handler, documentos, almacen, trabajador, tipo) =
            CrearRenovarDocumentoValidanArchivoAlmacenadoTests.PrepararCrear(EnImpersonacion);
        var clave = await almacen.GuardarAsync(new MemoryStream(CrearRenovarDocumentoValidanArchivoAlmacenadoTests.PdfValido), "d.pdf");

        var resultado = await handler.Handle(
            CrearRenovarDocumentoValidanArchivoAlmacenadoTests.NuevoCrear(trabajador.Id, tipo.Id, clave), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        var documento = documentos.Documentos.Should().ContainSingle().Subject;
        documento.SubidoPorUsuarioId.Should().Be(Simulado);
        documento.SubidoPorActorRealUsuarioId.Should().Be(ActorReal);
    }

    [Fact]
    public async Task Crear_un_documento_sin_persona_detras_lo_deja_sin_autor()
    {
        var (handler, documentos, almacen, trabajador, tipo) =
            CrearRenovarDocumentoValidanArchivoAlmacenadoTests.PrepararCrear(ActorAuditoria.SinResolver);
        var clave = await almacen.GuardarAsync(new MemoryStream(CrearRenovarDocumentoValidanArchivoAlmacenadoTests.PdfValido), "d.pdf");

        await handler.Handle(CrearRenovarDocumentoValidanArchivoAlmacenadoTests.NuevoCrear(trabajador.Id, tipo.Id, clave), CancellationToken.None);

        var documento = documentos.Documentos.Should().ContainSingle().Subject;
        documento.SubidoPorUsuarioId.Should().BeNull();
        documento.SubidoPorActorRealUsuarioId.Should().BeNull();
    }

    [Fact]
    public async Task Renovar_con_archivo_nuevo_firma_la_version_nueva_con_quien_renueva()
    {
        var (handler, documentos, almacen, anterior) = CrearRenovarDocumentoValidanArchivoAlmacenadoTests.PrepararRenovar(EnImpersonacion);
        var clave = await almacen.GuardarAsync(new MemoryStream(CrearRenovarDocumentoValidanArchivoAlmacenadoTests.PdfValido), "d.pdf");

        var resultado = await handler.Handle(CrearRenovarDocumentoValidanArchivoAlmacenadoTests.NuevoRenovar(anterior.Id, clave), CancellationToken.None);

        var nueva = documentos.Documentos.Should().ContainSingle(d => d.Id == resultado.Valor).Subject;
        nueva.Id.Should().NotBe(anterior.Id);
        nueva.SubidoPorUsuarioId.Should().Be(Simulado);
        nueva.SubidoPorActorRealUsuarioId.Should().Be(ActorReal);
        anterior.SubidoPorUsuarioId.Should().BeNull("la versión anterior no cambia de autor por renovarla");
    }

    [Fact]
    public async Task Corregir_fechas_sin_archivo_nuevo_no_crea_version_ni_cambia_el_autor()
    {
        var (handler, documentos, _, documento) = CrearRenovarDocumentoValidanArchivoAlmacenadoTests.PrepararRenovar(EnImpersonacion);

        await handler.Handle(CrearRenovarDocumentoValidanArchivoAlmacenadoTests.NuevoRenovar(documento.Id, null), CancellationToken.None);

        documentos.Documentos.Should().ContainSingle();
        documento.SubidoPorUsuarioId.Should().BeNull();
        documento.SubidoPorActorRealUsuarioId.Should().BeNull();
    }

    [Fact]
    public async Task Importar_documentos_los_firma_con_quien_confirma_la_importacion()
    {
        var escenario = new EscenarioImportacion()
            .ConEmpresaExistente().ConTrabajadorExistente().ConCentroExistente().ConTipoDocumentoExistente();
        var plan = EscenarioImportacion.Plan(documentos:
        [
            new DocumentoImportadoDto(EscenarioImportacion.DniConocido, EscenarioImportacion.TipoDocumentoConocido, DiaDeNegocio.Hoy(), YaExiste: false),
        ]);

        var resultado = await escenario.Handler(actor: EnImpersonacion).Handle(new EjecutarImportacionCommand(plan), CancellationToken.None);

        resultado.Valor.DocumentosCreados.Should().Be(1);
        var documento = escenario.DocumentoRepositorio.Documentos.Should().ContainSingle().Subject;
        documento.SubidoPorUsuarioId.Should().Be(Simulado);
        documento.SubidoPorActorRealUsuarioId.Should().Be(ActorReal);
    }
}
