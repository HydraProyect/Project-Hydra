using Bunit;
using CaeManager.Application.Subcontratas.Commands.CambiarNivelServicioSubcontrata;
using CaeManager.Application.Subcontratas.Commands.EditarSubcontrata;
using CaeManager.Domain.Common;
using CaeManager.Web.Components.Workspace;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Web;

namespace CaeManager.Web.Tests;

/// <summary>
/// El panel de Subcontrata del Context Workspace avisa del guardado: sin el aviso, la fila de
/// /subcontratas seguiría enseñando el dato anterior, porque el panel vive en MainLayout y guarda
/// sin pasar por la página. Avisan los dos guardados que la fila pinta: la identidad y el nivel
/// de servicio. Un guardado rechazado no avisa: no hay nada nuevo que leer.
/// </summary>
public partial class Subcontrata360Gen2Tests
{
    [Fact]
    public async Task Guardar_la_identidad_avisa_del_guardado_con_el_tipo_y_el_id_de_la_Subcontrata()
    {
        var escena = PrepararEdicion(new MediatorFalso());
        var cut = await AbrirEdicionAsync(escena);
        var avisos = this.EscucharAvisosDeGuardado();

        await Boton(cut, "Guardar").ClickAsync(new MouseEventArgs());

        escena.Mediador.Enviadas.OfType<EditarSubcontrataCommand>().Should().ContainSingle("control positivo: el guardado se envió")
            .Which.Id.Should().Be(escena.Original.Id);
        avisos.Should().Equal([(EntidadWorkspace.Subcontrata, escena.Original.Id)]);
    }

    [Fact]
    public async Task Un_guardado_rechazado_de_la_identidad_no_avisa_del_guardado()
    {
        const string motivo = "Ya existe otra subcontrata con esta identificación fiscal.";
        var escena = PrepararEdicion(new MediatorFalso { ResultadoEditar = Result.Fallo(Error.Crear("Subcontrata.CifDuplicado", motivo)) });
        var cut = await AbrirEdicionAsync(escena);
        var avisos = this.EscucharAvisosDeGuardado();

        await Boton(cut, "Guardar").ClickAsync(new MouseEventArgs());

        escena.Mediador.Enviadas.OfType<EditarSubcontrataCommand>().Should().ContainSingle("control positivo: el guardado se envió");
        cut.Markup.Should().Contain(motivo, "control positivo: el rechazo llegó al panel");
        avisos.Should().BeEmpty("sin guardado firme no hay fila que volver a pedir");
    }

    /// <summary>El nivel de servicio también se pinta en la fila del listado: mismo aviso que al guardar la identidad.</summary>
    [Fact]
    public async Task Cambiar_el_nivel_de_servicio_avisa_del_guardado_con_el_tipo_y_el_id_de_la_Subcontrata()
    {
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        mediador.Detalles[id] = Detalle(id, "Pinturas Lauburu S.A.");
        var cut = Renderizar(id);
        var avisos = this.EscucharAvisosDeGuardado();

        await Boton(cut, "Cambiar a Supervisada").ClickAsync(new MouseEventArgs());

        mediador.Enviadas.OfType<CambiarNivelServicioSubcontrataCommand>().Should().ContainSingle("control positivo: el cambio se envió");
        avisos.Should().Equal([(EntidadWorkspace.Subcontrata, id)]);
    }
}
