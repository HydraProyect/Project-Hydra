using CaeManager.Application.Importacion;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Importacion;

/// <summary>
/// REC-108 (DEC-20): confirmar la MISMA operación dos veces no debe duplicar
/// nada — ver la carrera concurrente real en
/// CaeManager.IntegrationTests.Importacion.EjecutarImportacionConcurrenciaTests,
/// que es la propiedad que de verdad importa. Estos tests cubren el camino
/// secuencial (mismo <c>OperacionId</c>, dos confirmaciones seguidas: doble
/// clic, reintento de red) con los fakes de esta suite.
/// </summary>
public class EjecutarImportacionIdempotenciaTests
{
    [Fact]
    public async Task Confirmar_la_misma_operacion_dos_veces_no_duplica_el_documento_creado()
    {
        var escenario = new EscenarioImportacion().ConTrabajadorExistente().ConTipoDocumentoExistente();
        var operacionId = Guid.NewGuid();
        var plan = EscenarioImportacion.Plan(operacionId: operacionId, documentos:
        [
            new DocumentoImportadoDto(
                EscenarioImportacion.DniConocido, EscenarioImportacion.TipoDocumentoConocido,
                new DateOnly(2026, 2, 1), YaExiste: false)
        ]);

        var primeraEjecucion = await escenario.EjecutarAsync(plan);
        primeraEjecucion.DocumentosCreados.Should().Be(1);
        escenario.DocumentoRepositorio.Documentos.Should().ContainSingle();

        var segundaEjecucion = await escenario.EjecutarAsync(plan);

        segundaEjecucion.DocumentosCreados.Should().Be(0, "la operación ya se confirmó — la segunda vez no crea nada");
        segundaEjecucion.Omitidos.Should().ContainSingle()
            .Which.Motivo.Should().Contain("ya se había confirmado antes");
        escenario.DocumentoRepositorio.Documentos.Should().ContainSingle(
            "confirmar dos veces la misma operación no puede dejar dos documentos donde el plan solo pedía uno");
    }

    [Fact]
    public async Task Dos_operaciones_distintas_del_mismo_archivo_no_se_confunden()
    {
        // Control negativo: analizar el archivo dos veces (dos OperacionId
        // distintos) es DELIBERADAMENTE una operación nueva cada vez — DEC-20 no
        // pide impedir reimportar, solo impedir que la MISMA confirmación se
        // materialice dos veces. La segunda operación reutiliza el documento vía
        // la deduplicación ya existente (YaExiste, ya probada en
        // EjecutarImportacionOmitidosTests), no vía el guardia nuevo de REC-108
        // — por eso aquí se siembra el documento en el contexto de lectura como
        // haría una segunda importación real que ya lo encuentra creado.
        var escenario = new EscenarioImportacion().ConTrabajadorExistente().ConTipoDocumentoExistente();
        var filaDocumento = new DocumentoImportadoDto(
            EscenarioImportacion.DniConocido, EscenarioImportacion.TipoDocumentoConocido,
            new DateOnly(2026, 2, 1), YaExiste: false);

        var primeraOperacion = EscenarioImportacion.Plan(operacionId: Guid.NewGuid(), documentos: [filaDocumento]);
        var primeraEjecucion = await escenario.EjecutarAsync(primeraOperacion);
        primeraEjecucion.DocumentosCreados.Should().Be(1);

        escenario.ConDocumentoExistente();
        var segundaOperacion = EscenarioImportacion.Plan(
            operacionId: Guid.NewGuid(),
            documentos: [filaDocumento with { YaExiste = true }]);
        var segundaEjecucion = await escenario.EjecutarAsync(segundaOperacion);

        segundaEjecucion.DocumentosCreados.Should().Be(0, "el documento ya existe — lo detecta la deduplicación, no el guardia de operación");
        segundaEjecucion.Omitidos.Should().BeEmpty("YaExiste:true es la reutilización anunciada por el análisis, no una omisión");
        escenario.DocumentoRepositorio.Documentos.Should().ContainSingle("la segunda operación no debe crear un segundo documento");
    }

    /// <summary>
    /// Si algo revienta DESPUÉS de que el handler registre la marca de
    /// operación pero ANTES del guardado final, la operación no puede quedar
    /// "confirmada" sin haberse confirmado de verdad — un reintento legítimo
    /// se quedaría creyendo para siempre que ya se hizo. Encontrado por
    /// revisión adversarial de Codex antes de esta PR (Finding 1).
    /// </summary>
    [Fact]
    public async Task Un_fallo_inesperado_a_mitad_de_construir_el_plan_no_deja_la_operacion_falsamente_confirmada()
    {
        var escenario = new EscenarioImportacion().ConTrabajadorExistente().ConTipoDocumentoExistente();
        var operacionId = Guid.NewGuid();
        escenario.DocumentoRepositorio.ExcepcionAlAgregar = new InvalidOperationException("Fallo simulado a mitad de construir el plan.");

        var plan = EscenarioImportacion.Plan(operacionId: operacionId, documentos:
        [
            new DocumentoImportadoDto(
                EscenarioImportacion.DniConocido, EscenarioImportacion.TipoDocumentoConocido,
                new DateOnly(2026, 2, 1), YaExiste: false)
        ]);

        Func<Task> intento = () => escenario.EjecutarAsync(plan);
        await intento.Should().ThrowAsync<InvalidOperationException>();

        (await escenario.OperacionImportacionRepositorio.ExisteAsync(operacionId)).Should().BeFalse(
            "el fallo ocurrió antes de guardar de verdad — la operación no puede darse por confirmada");
    }
}
