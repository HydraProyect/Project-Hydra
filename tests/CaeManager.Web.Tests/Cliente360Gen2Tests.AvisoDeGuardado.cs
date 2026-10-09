using Bunit;
using CaeManager.Application.Clientes.Commands.EditarCliente;
using CaeManager.Domain.Common;
using CaeManager.Web.Components.Workspace;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Web;

namespace CaeManager.Web.Tests;

/// <summary>
/// El panel de Cliente empresarial del Context Workspace avisa del guardado: sin el aviso, la
/// fila de /clientes seguiría enseñando el dato anterior, porque el panel vive en MainLayout y
/// guarda sin pasar por la página. Un guardado rechazado no avisa: no hay nada nuevo que leer.
/// </summary>
public partial class Cliente360Gen2Tests
{
    [Fact]
    public async Task Guardar_la_identidad_avisa_del_guardado_con_el_tipo_y_el_id_del_Cliente_empresarial()
    {
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        mediador.Detalles[id] = Detalle(id, "Refrielectric S.A.");
        mediador.Resumenes[id] = Resumen(id, 3, 42);
        var cut = Renderizar(id);
        var avisos = this.EscucharAvisosDeGuardado();

        await Boton(cut, "Editar identidad").ClickAsync(new MouseEventArgs());
        await Boton(cut, "Guardar").ClickAsync(new MouseEventArgs());

        mediador.Enviadas.OfType<EditarClienteCommand>().Should().ContainSingle("control positivo: el guardado se envió")
            .Which.Id.Should().Be(id);
        avisos.Should().Equal([(EntidadWorkspace.Cliente, id)]);
    }

    [Fact]
    public async Task Un_guardado_rechazado_de_la_identidad_no_avisa_del_guardado()
    {
        const string motivo = "Ya existe otro cliente con esta identificación fiscal.";
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso { ResultadoEditar = Result.Fallo(Error.Crear("Cliente.CifDuplicado", motivo)) });
        mediador.Detalles[id] = Detalle(id, "Refrielectric S.A.");
        mediador.Resumenes[id] = Resumen(id, 3, 42);
        var cut = Renderizar(id);
        var avisos = this.EscucharAvisosDeGuardado();

        await Boton(cut, "Editar identidad").ClickAsync(new MouseEventArgs());
        await Boton(cut, "Guardar").ClickAsync(new MouseEventArgs());

        mediador.Enviadas.OfType<EditarClienteCommand>().Should().ContainSingle("control positivo: el guardado se envió");
        cut.Markup.Should().Contain(motivo, "control positivo: el rechazo llegó al panel");
        avisos.Should().BeEmpty("sin guardado firme no hay fila que volver a pedir");
    }
}
