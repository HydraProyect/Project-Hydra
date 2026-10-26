using CaeManager.Application.Importacion;
using CaeManager.Domain.Empresas;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Importacion;

/// <summary>
/// La importación Combinada lleva una <c>OperacionId</c> como la CAE completa: confirmar dos veces
/// el MISMO plan (doble clic, reintento de red) escribe una sola vez. Estos tests cubren el camino
/// secuencial con los fakes de la suite; la carrera concurrente real vive en IntegrationTests.
/// </summary>
public class EjecutarImportacionCombinadaIdempotenciaTests
{
    private static ClienteImportadoDto Cliente(int fila = 5) => new("Cliente Uno S.A.", "B12345674", false, YaExiste: false, fila);

    [Fact]
    public async Task Confirmar_la_misma_operacion_dos_veces_no_duplica_el_Cliente_empresarial()
    {
        var escenario = new EscenarioImportacionCombinada();
        var plan = EscenarioImportacionCombinada.Plan(clientes: [Cliente()]);

        var primera = await escenario.EjecutarAsync(plan);
        primera.ClientesCreados.Should().Be(1);
        escenario.EmpresaRepositorio.Empresas.Should().ContainSingle();

        var segunda = await escenario.EjecutarAsync(plan);

        segunda.ClientesCreados.Should().Be(0, "la operación ya se confirmó: la segunda vez no crea nada");
        segunda.Omitidos.Should().ContainSingle().Which.Motivo.Should().Contain("ya se había confirmado antes");
        escenario.EmpresaRepositorio.Empresas.Should().ContainSingle(
            "confirmar dos veces la misma operación no puede dejar dos Empresas donde el plan pedía una");
    }

    [Fact]
    public async Task Un_fallo_inesperado_a_mitad_de_construir_el_plan_no_deja_la_operacion_falsamente_confirmada()
    {
        var escenario = new EscenarioImportacionCombinada();
        var operacionId = Guid.NewGuid();
        escenario.EmpresaRepositorio.ExcepcionAlAgregar = new InvalidOperationException("Fallo simulado a mitad de construir el plan.");
        var plan = EscenarioImportacionCombinada.Plan(operacionId, clientes: [Cliente()]);

        Func<Task> intento = () => escenario.EjecutarAsync(plan);
        await intento.Should().ThrowAsync<InvalidOperationException>();

        (await escenario.OperacionImportacionRepositorio.ExisteAsync(operacionId)).Should().BeFalse(
            "el fallo ocurrió antes de guardar de verdad: un reintento legítimo no puede creer que ya se hizo");
        escenario.OperacionImportacionRepositorio.VecesDescartado.Should().Be(1,
            "el contexto del circuito se comparte: lo pendiente (la marca y las altas) se descarta, no se deja para el siguiente guardado");
    }

    [Fact]
    public async Task Una_operacion_ya_confirmada_se_corta_antes_de_leer_o_escribir_nada()
    {
        var escenario = new EscenarioImportacionCombinada();
        var plan = EscenarioImportacionCombinada.Plan(clientes: [Cliente()]);
        await escenario.EjecutarAsync(plan);
        var empresasAntes = escenario.EmpresaRepositorio.Empresas.Count;

        var repeticion = await escenario.EjecutarAsync(plan with { Clientes = [Cliente() with { RazonSocial = "Otro nombre S.A.", Cif = "P1234567D" }] });

        repeticion.ClientesCreados.Should().Be(0);
        escenario.EmpresaRepositorio.Empresas.Should().HaveCount(empresasAntes);
    }
}
