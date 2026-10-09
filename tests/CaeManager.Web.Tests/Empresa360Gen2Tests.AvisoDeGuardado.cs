using Bunit;
using CaeManager.Application.Empresas.Commands.EditarEmpresa;
using CaeManager.Domain.Common;
using CaeManager.Web.Components.Workspace;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Web;

namespace CaeManager.Web.Tests;

/// <summary>
/// El panel de Empresa del Context Workspace avisa del guardado: sin el aviso, la fila de
/// /empresas seguiría enseñando el dato anterior, porque el panel vive en MainLayout y guarda
/// sin pasar por la página. Un guardado rechazado no avisa: no hay nada nuevo que leer.
/// </summary>
public partial class Empresa360Gen2Tests
{
    [Fact]
    public async Task Guardar_la_identidad_avisa_del_guardado_con_el_tipo_y_el_id_de_la_Empresa()
    {
        var id = Guid.NewGuid(); var m = Registrar(new MediadorFalso());
        m.Detalles[id] = Detalle(id, "Montajes Ebro S.L."); m.Cumplimientos[id] = 80;
        var cut = Renderizar(id);
        var avisos = this.EscucharAvisosDeGuardado();

        await Boton(cut, "Editar identidad").ClickAsync(new MouseEventArgs());
        await Boton(cut, "Guardar").ClickAsync(new MouseEventArgs());

        m.Enviadas.OfType<EditarEmpresaCommand>().Should().ContainSingle("control positivo: el guardado se envió")
            .Which.Id.Should().Be(id);
        avisos.Should().Equal([(EntidadWorkspace.Empresa, id)]);
    }

    [Fact]
    public async Task Un_guardado_rechazado_de_la_identidad_no_avisa_del_guardado()
    {
        const string motivo = "Ya existe otra empresa con esta identificación fiscal.";
        var id = Guid.NewGuid();
        var m = Registrar(new MediadorFalso { Edicion = Result.Fallo(Error.Crear("Empresa.CifDuplicado", motivo)) });
        m.Detalles[id] = Detalle(id, "Montajes Ebro S.L."); m.Cumplimientos[id] = 80;
        var cut = Renderizar(id);
        var avisos = this.EscucharAvisosDeGuardado();

        await Boton(cut, "Editar identidad").ClickAsync(new MouseEventArgs());
        await Boton(cut, "Guardar").ClickAsync(new MouseEventArgs());

        m.Enviadas.OfType<EditarEmpresaCommand>().Should().ContainSingle("control positivo: el guardado se envió");
        cut.Markup.Should().Contain(motivo, "control positivo: el rechazo llegó al panel");
        avisos.Should().BeEmpty("sin guardado firme no hay fila que volver a pedir");
    }
}
