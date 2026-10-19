using CaeManager.Domain.Trabajadores;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Trabajadores;

/// <summary>
/// <see cref="Trabajador.FijarNotaInterna"/>: la «Nota interna» de Trabajador 360, con el mismo contrato que la de
/// Empresa 360 (mismo límite, vacía o solo espacios = sin nota), sin tocar ningún otro dato y, por ser texto libre
/// sobre una persona, tratada como dato personal: la anonimización la vacía y un Trabajador anonimizado no la admite.
/// </summary>
public class TrabajadorNotaInternaTests
{
    private static Trabajador Manuela() =>
        Trabajador.DeEmpresa(Guid.NewGuid(), "Manuela", "Quintanilla Ortega", "11111111H",
            email: "manuela@ejemplo.test", observaciones: "Observación pública", telefono: "+34600111222");

    [Fact]
    public void Un_trabajador_nuevo_no_tiene_nota()
    {
        Manuela().Notas.Should().BeNull();
    }

    [Fact]
    public void Fija_la_nota_recortada_y_no_toca_las_observaciones_ni_la_identidad()
    {
        var trabajador = Manuela();

        trabajador.FijarNotaInterna("  Prefiere turno de tarde.\nLlamar antes de las 10.  ");

        trabajador.Notas.Should().Be("Prefiere turno de tarde.\nLlamar antes de las 10.");
        trabajador.Observaciones.Should().Be("Observación pública");
        trabajador.Nombre.Should().Be("Manuela");
        trabajador.Email.Should().Be("manuela@ejemplo.test");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n\t ")]
    [InlineData(null)]
    public void Vacia_o_solo_espacios_deja_al_trabajador_sin_nota(string? notas)
    {
        var trabajador = Manuela();
        trabajador.FijarNotaInterna("Nota anterior.");

        trabajador.FijarNotaInterna(notas);

        trabajador.Notas.Should().BeNull();
    }

    [Fact]
    public void Admite_el_maximo_y_rechaza_uno_mas_sin_cambiar_la_nota()
    {
        var trabajador = Manuela();
        trabajador.FijarNotaInterna(new string('a', Trabajador.LongitudMaximaNotas));

        var accion = () => trabajador.FijarNotaInterna(new string('b', Trabajador.LongitudMaximaNotas + 1));

        accion.Should().Throw<ArgumentException>()
            .WithMessage($"La nota interna no puede superar {Trabajador.LongitudMaximaNotas} caracteres.*");
        trabajador.Notas.Should().HaveLength(Trabajador.LongitudMaximaNotas).And.NotContain("b");
    }

    [Fact]
    public void El_limite_es_el_mismo_que_el_de_la_nota_de_Empresa()
    {
        Trabajador.LongitudMaximaNotas.Should().Be(CaeManager.Domain.Empresas.Empresa.LongitudMaximaNotas);
    }

    [Fact]
    public void Es_dato_personal_y_la_anonimizacion_la_vacia()
    {
        var trabajador = Manuela();
        trabajador.FijarNotaInterna("Está de baja hasta el lunes.");

        trabajador.Anonimizar(DateTime.UtcNow);

        Trabajador.CamposDeDatosPersonales.Should().Contain(nameof(Trabajador.Notas));
        trabajador.Notas.Should().BeNull();
    }

    [Fact]
    public void Un_trabajador_anonimizado_no_admite_nota()
    {
        var trabajador = Manuela();
        trabajador.Anonimizar(DateTime.UtcNow);

        var accion = () => trabajador.FijarNotaInterna("Dato reintroducido.");

        accion.Should().Throw<InvalidOperationException>().WithMessage("*anonimizado*");
        trabajador.Notas.Should().BeNull();
    }
}
