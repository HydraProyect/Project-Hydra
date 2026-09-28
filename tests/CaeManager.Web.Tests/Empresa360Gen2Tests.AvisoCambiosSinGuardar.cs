using Bunit;
using CaeManager.Application.Empresas.Commands.GuardarCredencialAccesoEmpresa;
using CaeManager.Web.Features.Empresas.Components;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// P1-E2b (lote 1): la edición en línea del panel de Empresa (Información y credenciales, que
/// se guardan por separado) pinta su propio aviso de cambios sin guardar. La salida a activar
/// el 2FA que decide la propia credencial no pregunta (ver los tests Sin_2FA_*).
/// </summary>
public partial class Empresa360Gen2Tests
{
    private async Task<(IRenderedComponent<EmpresaWorkspacePanel> Cut, MediadorFalso Mediador, NavigationManager Navegacion)> EditarAsync()
    {
        // La pregunta pinta sus textos con IStringLocalizer<TextosComunes>.
        Services.AddLocalization();
        var (id, m, navegacion) = FichaConCredencial();
        var cut = Renderizar(id);
        await Boton(cut, "Editar identidad").ClickAsync(new MouseEventArgs());
        return (cut, m, navegacion);
    }

    [Fact]
    public async Task Aviso_la_informacion_editada_pregunta_al_salir()
    {
        var (cut, _, navegacion) = await EditarAsync();
        await Control(cut, "Razón social").InputAsync(new ChangeEventArgs { Value = "Montajes Norte S.L." });

        await cut.SalirYComprobarQuePreguntaAsync(navegacion);
    }

    [Fact]
    public async Task Aviso_la_credencial_editada_pregunta_al_salir()
    {
        var (cut, _, navegacion) = await EditarAsync();
        await Control(cut, "Usuario").InputAsync(new ChangeEventArgs { Value = "ebro.admin" });

        await cut.SalirYComprobarQuePreguntaAsync(navegacion);
    }

    [Fact]
    public async Task Aviso_abrir_la_edicion_sin_tocar_nada_no_pregunta()
    {
        var (cut, _, navegacion) = await EditarAsync();

        await cut.SalirYComprobarQueNoPreguntaAsync(navegacion, "la credencial precargada no es un cambio de quien edita");
    }

    [Fact]
    public async Task Aviso_la_credencial_ya_guardada_deja_de_contar_como_cambio()
    {
        var (cut, m, navegacion) = await EditarAsync();
        await Control(cut, "Usuario").InputAsync(new ChangeEventArgs { Value = "ebro.admin" });

        await Boton(cut, "Guardar credenciales").ClickAsync(new MouseEventArgs());

        m.Enviadas.OfType<GuardarCredencialAccesoEmpresaCommand>().Should().ContainSingle();
        await cut.SalirYComprobarQueNoPreguntaAsync(navegacion, "lo guardado es el nuevo punto de partida");
    }
}
