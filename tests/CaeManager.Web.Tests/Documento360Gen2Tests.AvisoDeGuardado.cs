using Bunit;
using CaeManager.Application.Documentos.Commands.RenovarDocumento;
using CaeManager.Domain.Common;
using CaeManager.Web.Components.Workspace;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace CaeManager.Web.Tests;

/// <summary>
/// El panel de Documento del Context Workspace avisa del guardado: sin el aviso, la fila de
/// /documentos seguiría enseñando las fechas anteriores, porque el panel vive en MainLayout y
/// guarda sin pasar por la página. Un guardado rechazado no avisa: no hay nada nuevo que leer.
/// </summary>
public partial class Documento360Gen2Tests
{
    private const string GuardarFechas = "Guardar fechas y comentarios (sin sustituir el archivo)";

    [Fact]
    public async Task Guardar_fechas_y_comentarios_avisa_del_guardado_con_el_tipo_y_el_id_del_Documento()
    {
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediadorFalso());
        mediador.Detalles[id] = Detalle(id);
        var cut = Renderizar(id);
        var avisos = this.EscucharAvisosDeGuardado();

        await Boton(cut, "Renovar").ClickAsync(new MouseEventArgs());
        await cut.FindAll("input[type=date]")[0].InputAsync(new ChangeEventArgs { Value = "2026-02-03" });
        await Boton(cut, GuardarFechas).ClickAsync(new MouseEventArgs());

        mediador.Enviadas.OfType<RenovarDocumentoCommand>().Should().ContainSingle("control positivo: el guardado se envió")
            .Which.Id.Should().Be(id);
        avisos.Should().Equal([(EntidadWorkspace.Documento, id)]);
    }

    [Fact]
    public async Task Un_guardado_rechazado_de_fechas_y_comentarios_no_avisa_del_guardado()
    {
        const string motivo = "Otra persona ha modificado este documento.";
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediadorFalso { ResultadoRenovar = Result.Fallo<Guid>(Error.Crear("Documento.Concurrencia", motivo)) });
        mediador.Detalles[id] = Detalle(id);
        var cut = Renderizar(id);
        var avisos = this.EscucharAvisosDeGuardado();

        await Boton(cut, "Renovar").ClickAsync(new MouseEventArgs());
        await cut.FindAll("input[type=date]")[0].InputAsync(new ChangeEventArgs { Value = "2026-02-03" });
        await Boton(cut, GuardarFechas).ClickAsync(new MouseEventArgs());

        mediador.Enviadas.OfType<RenovarDocumentoCommand>().Should().ContainSingle("control positivo: el guardado se envió");
        cut.Markup.Should().Contain(motivo, "control positivo: el rechazo llegó al panel");
        avisos.Should().BeEmpty("sin guardado firme no hay fila que volver a pedir");
    }
}
