using Bunit;
using CaeManager.Application.Trabajadores.Commands.EditarFichaTrabajador;
using CaeManager.Web.Components.DesignSystem;
using FluentAssertions;

namespace CaeManager.Web.Tests;

/// <summary>
/// Contacto de emergencia del Trabajador (decisión de Chris, 2026-10-18): en el lateral «Datos del trabajador», tras
/// el teléfono y el correo, como en la maqueta Trabajador 360 del 17-10 (nombre, y teléfono con botón de copiar); y en
/// el drawer de la ficha, solo si el servidor lo envió.
/// </summary>
public partial class Trabajador360Gen2Tests
{
    [Fact]
    public void El_lateral_ensena_el_contacto_de_emergencia_tras_el_telefono_y_el_correo()
    {
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        var (_, berriz) = Escena();
        mediador.Detalles[id] = Detalle(id, "Javier", "Salas Moreno",
            telefono: "644 902 118", email: "javier.salas@refrielectric.example") with
        {
            ContactoEmergenciaNombre = "Lucía Moreno (madre)",
            ContactoEmergenciaTelefono = "+34611223344",
            ContactoEmergenciaVisible = true
        };
        mediador.Centros[id] = [berriz];

        var cut = Renderizar(id);

        var rejilla = cut.Find(".cuerpo-con-lateral-lateral .trabajador360-contactos");
        rejilla.QuerySelectorAll("dt").Select(d => d.TextContent.Trim())
            .Should().Equal(["Teléfono", "Correo electrónico", "Contacto de emergencia", "Teléfono de emergencia"]);
        cut.Find("[data-contacto='emergencia-nombre'] dd").TextContent.Trim().Should().Be("Lucía Moreno (madre)");
        cut.Find("[data-contacto='emergencia-telefono'] dd").TextContent.Should().Contain("+34611223344");
        cut.FindAll("[data-contacto='emergencia-nombre'] .boton-copiar").Should().BeEmpty("la maqueta solo copia el teléfono");
        cut.FindAll("[data-contacto='emergencia-telefono'] .boton-copiar").Should().ContainSingle();
    }

    [Fact]
    public void Solo_con_contacto_de_emergencia_se_pinta_la_tarjeta_con_sus_dos_filas()
    {
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        var (_, berriz) = Escena();
        mediador.Detalles[id] = Detalle(id, "Javier", "Salas Moreno") with
        {
            ContactoEmergenciaNombre = "Lucía Moreno",
            ContactoEmergenciaTelefono = "611223344",
            ContactoEmergenciaVisible = true
        };
        mediador.Centros[id] = [berriz];

        var cut = Renderizar(id);

        cut.Find(".cuerpo-con-lateral-lateral .trabajador360-contactos").QuerySelectorAll("dt")
            .Select(d => d.TextContent.Trim()).Should().Equal(["Contacto de emergencia", "Teléfono de emergencia"]);
    }

    [Fact]
    public async Task El_drawer_precarga_el_contacto_y_lo_envia_con_la_ficha()
    {
        var id = Guid.NewGuid(); var m = ConTrabajador(id);
        m.Detalles[id] = m.Detalles[id] with
        {
            ContactoEmergenciaNombre = "Lucía Moreno",
            ContactoEmergenciaTelefono = "611223344",
            ContactoEmergenciaVisible = true
        };
        var cut = Renderizar(id); var drawer = await AbrirEditorFicha(cut);

        drawer.FindComponents<CampoTexto>().Should().HaveCount(15);
        drawer.FindComponents<CampoTexto>().Single(c => c.Instance.Etiqueta == "Contacto de emergencia").Instance.Valor
            .Should().Be("Lucía Moreno");
        drawer.FindComponents<CampoTexto>().Single(c => c.Instance.Etiqueta == "Teléfono de emergencia").Instance.Type
            .Should().Be("tel");
        await CambiarCampoFicha(drawer, "Teléfono de emergencia", "+34 699 000 111");
        drawer.FindComponent<DrawerFormulario>().Instance.HayCambios().Should().BeTrue();
        await GuardarFicha(drawer);

        m.Enviadas.OfType<EditarFichaTrabajadorCommand>().Should().ContainSingle()
            .Which.ContactoEmergencia.Should().Be(new ContactoEmergenciaTrabajador("Lucía Moreno", "+34 699 000 111"));
    }

    [Fact]
    public async Task Un_telefono_de_emergencia_sin_formato_se_senala_en_su_campo_y_no_se_envia()
    {
        var id = Guid.NewGuid(); var m = ConTrabajador(id);
        m.Detalles[id] = m.Detalles[id] with { ContactoEmergenciaVisible = true };
        var cut = Renderizar(id); var drawer = await AbrirEditorFicha(cut);

        await CambiarCampoFicha(drawer, "Teléfono de emergencia", "llamar a su madre");
        await GuardarFicha(drawer);

        m.Enviadas.OfType<EditarFichaTrabajadorCommand>().Should().BeEmpty();
        drawer.FindComponents<CampoTexto>().Single(c => c.Instance.Etiqueta == "Teléfono de emergencia").Instance.MensajeError
            .Should().NotBeNullOrEmpty();
    }

    /// <summary>
    /// Sin <c>ContactoEmergenciaVisible</c> el drawer no pinta los campos y la orden lleva <c>ContactoEmergencia</c> en
    /// <c>null</c>: guardar la ficha no borra un contacto que el lector no pudo ver.
    /// </summary>
    [Fact]
    public async Task Sin_contacto_visible_el_drawer_no_lo_pinta_ni_lo_borra()
    {
        var id = Guid.NewGuid(); var m = ConTrabajador(id);
        var cut = Renderizar(id); var drawer = await AbrirEditorFicha(cut);

        drawer.FindComponents<CampoTexto>().Select(c => c.Instance.Etiqueta)
            .Should().NotContain(["Contacto de emergencia", "Teléfono de emergencia"]);
        await CambiarCampoFicha(drawer, "Nacionalidad", "Colombiana");
        await GuardarFicha(drawer);

        m.Enviadas.OfType<EditarFichaTrabajadorCommand>().Should().ContainSingle().Which.ContactoEmergencia.Should().BeNull();
    }
}
