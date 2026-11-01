using CaeManager.Application.Importacion;
using CaeManager.Domain.Empresas;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Importacion;

/// <summary>
/// La importación Combinada (plantilla de Clientes, Empresas, Centros y Trabajadores) ante datos legítimos que antes
/// abortaban el archivo entero (C-06, C-07): un DNI de un Trabajador en la Papelera (el índice único lo incluye) y
/// dos Empresas cuyas razones sociales solo difieren en mayúsculas. La fila afectada se omite con su causal propia y
/// el resto se escribe (éxito parcial, DCR-12 B).
/// </summary>
public class EjecutarImportacionCombinadaPapeleraTests
{
    private const string EmpresaNueva = "Montajes del Ebro S.L.";
    private const string DniEnPapelera = "12345678Z";
    private const string DniSano = "00000000T";
    private const string RazonSocialRepetida = "ACME Servicios S.L.";
    private const string EmpresaUnica = "Instalaciones Únicas S.L.";

    private static TrabajadorImportadoDto Trabajador(string razonSocial, string dni, int fila) =>
        new(razonSocial, "Lucía", "Fernández", dni, null, null, YaExiste: false, Hoja: "Trabajadores", Fila: fila);

    [Fact]
    public async Task Trabajador_nuevo_con_un_DNI_en_la_Papelera_se_omite_y_los_demas_se_crean()
    {
        var escenario = new EscenarioImportacionCombinada();
        escenario.TrabajadorRepositorio.DnisEnPapelera.Add(DniEnPapelera);
        var plan = EscenarioImportacionCombinada.Plan(
            empresas: [new EmpresaCombinadaImportadaDto(EmpresaNueva, [], YaExiste: false, Fila: 2)],
            trabajadores: [Trabajador(EmpresaNueva, DniEnPapelera, 3), Trabajador(EmpresaNueva, DniSano, 4)]);

        var resultado = await escenario.EjecutarAsync(plan);

        var omitido = resultado.Omitidos.Should().ContainSingle().Subject;
        omitido.Hoja.Should().Be("Trabajadores");
        omitido.Fila.Should().Be(3);
        omitido.Motivo.Should().Contain("Papelera").And.Contain("restáuralo");
        resultado.TrabajadoresCreados.Should().Be(1, "la fila sana se escribe: éxito parcial, no 0 de N");
        escenario.TrabajadorRepositorio.Trabajadores.Should().ContainSingle().Which.Dni.Should().Be(DniSano);
    }

    [Fact]
    public async Task Razones_sociales_que_solo_difieren_en_mayusculas_omiten_las_filas_que_las_citan_sin_elegir_una_al_azar()
    {
        var escenario = new EscenarioImportacionCombinada();
        escenario.EmpresaRepositorio.Empresas.Add(new Empresa(RazonSocialRepetida));
        escenario.EmpresaRepositorio.Empresas.Add(new Empresa(RazonSocialRepetida.ToUpperInvariant()));
        escenario.EmpresaRepositorio.Empresas.Add(new Empresa(EmpresaUnica));
        var plan = EscenarioImportacionCombinada.Plan(
            clientes: [new ClienteImportadoDto("Cliente Uno S.A.", "B12345674", false, YaExiste: false, Fila: 2)],
            empresas: [new EmpresaCombinadaImportadaDto(RazonSocialRepetida, [], YaExiste: true, Fila: 3)],
            centros:
            [
                new CentroImportadoDto("Planta Norte", "Cliente Uno S.A.", RazonSocialRepetida, null, null, null, null, YaExiste: false, Fila: 4),
            ],
            trabajadores:
            [
                Trabajador(RazonSocialRepetida, DniSano, 5),
                Trabajador(EmpresaUnica, DniEnPapelera, 6),
            ]);

        var resultado = await escenario.EjecutarAsync(plan);

        resultado.Omitidos.Should().HaveCount(3).And.OnlyContain(o => o.Motivo.Contains("Hay varias empresas"));
        resultado.Omitidos.Select(o => o.Hoja).Should().BeEquivalentTo("Empresas", "Centros", "Trabajadores");
        resultado.ClientesCreados.Should().Be(1, "el resto del archivo sigue");
        resultado.TrabajadoresCreados.Should().Be(1);
        escenario.TrabajadorRepositorio.Trabajadores.Should().ContainSingle().Which.Dni.Should().Be(DniEnPapelera);
    }
}
