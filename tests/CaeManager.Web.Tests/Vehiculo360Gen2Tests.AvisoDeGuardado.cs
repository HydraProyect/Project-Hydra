using Bunit;
using CaeManager.Application.Vehiculos.Commands.EditarVehiculo;
using CaeManager.Domain.Common;
using CaeManager.Web.Components.Workspace;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Web;

namespace CaeManager.Web.Tests;

/// <summary>
/// El panel de Vehículo del Context Workspace avisa del guardado: sin el aviso, la fila de
/// /vehiculos seguiría enseñando el dato anterior, porque el panel vive en MainLayout y guarda
/// sin pasar por la página. Un guardado rechazado no avisa: no hay nada nuevo que leer.
/// </summary>
public partial class Vehiculo360Gen2Tests
{
    [Fact]
    public async Task Guardar_desde_el_lapiz_avisa_del_guardado_con_el_tipo_y_el_id_del_Vehiculo()
    {
        var id = Guid.NewGuid();
        var m = Registrar(new MediadorFalso());
        m.Detalles[id] = Detalle(id, "Furgoneta de obra");
        var cut = RenderizarPanel(id);
        var avisos = this.EscucharAvisosDeGuardado();

        await BotonEditar(cut).ClickAsync(new MouseEventArgs());
        await Boton(cut, "Guardar").ClickAsync(new MouseEventArgs());

        m.Enviadas.OfType<EditarVehiculoCommand>().Should().ContainSingle("control positivo: el guardado se envió")
            .Which.Id.Should().Be(id);
        avisos.Should().Equal([(EntidadWorkspace.Vehiculo, id)]);
    }

    [Fact]
    public async Task Un_guardado_rechazado_no_avisa_del_guardado()
    {
        const string motivo = "Ya existe otro vehículo con esta matrícula.";
        var id = Guid.NewGuid();
        var m = Registrar(new MediadorFalso { Edicion = Result.Fallo(Error.Crear("Vehiculo.MatriculaDuplicada", motivo)) });
        m.Detalles[id] = Detalle(id, "Furgoneta de obra");
        var cut = RenderizarPanel(id);
        var avisos = this.EscucharAvisosDeGuardado();

        await BotonEditar(cut).ClickAsync(new MouseEventArgs());
        await Boton(cut, "Guardar").ClickAsync(new MouseEventArgs());

        m.Enviadas.OfType<EditarVehiculoCommand>().Should().ContainSingle("control positivo: el guardado se envió");
        cut.Markup.Should().Contain(motivo, "control positivo: el rechazo llegó al panel");
        avisos.Should().BeEmpty("sin guardado firme no hay fila que volver a pedir");
    }
}
