using Bunit;
using CaeManager.Web.Components.DesignSystem;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

public partial class Cliente360Gen2Tests
{
    [Fact]
    public async Task Guardar_la_identidad_confirma_que_la_informacion_se_ha_guardado()
    {
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        mediador.Detalles[id] = Detalle(id, "Empresa de prueba");
        mediador.Resumenes[id] = Resumen(id, 1, 2);
        var cut = Renderizar(id);
        await Boton(cut, "Editar identidad").ClickAsync(new MouseEventArgs());

        await Boton(cut, "Guardar").ClickAsync(new MouseEventArgs());

        Services.GetRequiredService<ToastService>().Mensajes.Should()
            .ContainSingle(m => m.Mensaje == "Información guardada" && m.Tono == TonoToast.Exito);
    }
}