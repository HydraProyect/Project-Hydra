using CaeManager.Domain.Visitas;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Visitas;

/// <summary>
/// Contacto de la Visita (decisión de producto, 2026-10-16): campo propio, rellenado por defecto con el contacto del
/// Centro y editable después.
/// </summary>
public class ContactoVisitaTests
{
    private static Visita NuevaVisita() => new(Guid.NewGuid(), new DateOnly(2026, 11, 2), new DateOnly(2026, 11, 3), null);

    [Fact]
    public void Una_visita_nueva_nace_sin_contacto()
    {
        var visita = NuevaVisita();

        visita.ContactoNombre.Should().BeNull();
        visita.ContactoTelefono.Should().BeNull();
        visita.ContactoCorreo.Should().BeNull();
    }

    [Fact]
    public void El_contacto_del_Centro_en_texto_libre_se_copia_tal_cual_al_nombre()
    {
        var visita = NuevaVisita();

        visita.RellenarContactoDesdeCentro("  Pedro Salas · 954 555 010  ");

        visita.ContactoNombre.Should().Be("Pedro Salas · 954 555 010", "no se parte ni se interpreta el texto libre");
        visita.ContactoTelefono.Should().BeNull();
        visita.ContactoCorreo.Should().BeNull();
    }

    [Fact]
    public void Un_contacto_del_Centro_que_es_solo_una_direccion_de_correo_va_al_correo()
    {
        var visita = NuevaVisita();

        visita.RellenarContactoDesdeCentro("accesos@planta.example");

        visita.ContactoCorreo.Should().Be("accesos@planta.example");
        visita.ContactoNombre.Should().BeNull();
    }

    [Theory]
    [InlineData("Pedro accesos@planta.example")]
    [InlineData("@planta.example")]
    [InlineData("pedro@")]
    [InlineData("a@b@c.example")]
    public void Un_texto_con_arroba_que_no_es_una_sola_direccion_va_al_nombre(string contacto)
    {
        var visita = NuevaVisita();

        visita.RellenarContactoDesdeCentro(contacto);

        visita.ContactoNombre.Should().Be(contacto);
        visita.ContactoCorreo.Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Sin_contacto_en_el_Centro_la_visita_queda_sin_contacto(string? contacto)
    {
        var visita = NuevaVisita();

        visita.RellenarContactoDesdeCentro(contacto);

        visita.ContactoNombre.Should().BeNull();
        visita.ContactoCorreo.Should().BeNull();
    }

    [Fact]
    public void El_contacto_mas_largo_que_admite_el_Centro_se_copia_entero()
    {
        var visita = NuevaVisita();
        var largo = new string('x', Visita.LongitudMaximaContactoNombre);

        visita.RellenarContactoDesdeCentro(largo);

        visita.ContactoNombre.Should().Be(largo);
    }

    [Fact]
    public void Actualizar_el_contacto_lo_sustituye_y_deja_sin_informar_los_campos_vacios()
    {
        var visita = NuevaVisita();
        visita.RellenarContactoDesdeCentro("Pedro Salas");

        visita.ActualizarContacto(" Lucía Marín ", "954 555 022", "  ");

        visita.ContactoNombre.Should().Be("Lucía Marín");
        visita.ContactoTelefono.Should().Be("954 555 022");
        visita.ContactoCorreo.Should().BeNull();
    }

    [Fact]
    public void Un_telefono_demasiado_largo_no_deja_el_contacto_a_medias()
    {
        var visita = NuevaVisita();
        visita.ActualizarContacto("Pedro Salas", null, null);

        var actuar = () => visita.ActualizarContacto("Otra persona", new string('9', Visita.LongitudMaximaContactoTelefono + 1), null);

        actuar.Should().Throw<ArgumentException>();
        visita.ContactoNombre.Should().Be("Pedro Salas");
    }
}
