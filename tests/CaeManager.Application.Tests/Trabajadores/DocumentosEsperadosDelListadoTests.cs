using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadores;
using FluentAssertions;

namespace CaeManager.Application.Tests.Trabajadores;

/// <summary>
/// El denominador de «registrados vigentes» (listado de Trabajadores y Empresa 360) cuenta también los Tipos exigidos
/// sin documento: quien tiene «Falta: Documento de identidad» no puede leerse 4/4.
/// </summary>
public class DocumentosEsperadosDelListadoTests
{
    private static TrabajadorListaDto Trabajador(int registrados, int vigentes, params string[] faltantes) =>
        new(Guid.NewGuid(), "Javier", "Salas Moreno", "12345678Z", "Montajes Ebro S.L.")
        {
            DocumentosRegistrados = registrados,
            DocumentosVigentes = vigentes,
            DocumentosFaltantes = faltantes,
        };

    [Fact]
    public void Un_documento_exigido_sin_registrar_cuenta_en_el_denominador()
    {
        var trabajador = Trabajador(registrados: 4, vigentes: 4, "Documento de identidad");

        trabajador.DocumentosEsperados.Should().Be(5);
    }

    [Fact]
    public void Sin_nada_exigido_que_falte_el_denominador_son_los_registrados()
    {
        Trabajador(registrados: 4, vigentes: 3).DocumentosEsperados.Should().Be(4);
    }

    [Fact]
    public void Cada_Tipo_faltante_suma_uno()
    {
        Trabajador(registrados: 0, vigentes: 0, "Documento de identidad", "Aptitud médica").DocumentosEsperados.Should().Be(2);
    }
}
