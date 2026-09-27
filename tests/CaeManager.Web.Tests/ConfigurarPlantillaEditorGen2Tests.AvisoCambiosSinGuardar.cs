using Bunit;
using CaeManager.Application.Plantillas.Commands.GenerarDocumentoIndividual;
using CaeManager.Application.Plantillas.Commands.GuardarElementosPlantilla;
using CaeManager.Application.Plantillas.Commands.IniciarLoteGeneracionDocumentos;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadoresParaSelector;
using CaeManager.Domain.Common;
using CaeManager.Domain.Plantillas;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Plantillas.Pages;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Xunit;

namespace CaeManager.Web.Tests;

/// <summary>
/// P1-E2b: salir del editor de plantillas con algo sin guardar pregunta antes: los datos
/// del alta, la configuración de un campo, una caja movida sobre el PDF o, en una versión
/// confirmada, lo escrito para generar. La versión recién cargada no es un cambio, y
/// guardar o confirmar (que navega al catálogo) no preguntan.
/// </summary>
public partial class ConfigurarPlantillaEditorGen2Tests
{
    private static Task EscribirAsync(IRenderedComponent<ConfigurarPlantilla> cut, string etiqueta, string valor) =>
        cut.FindComponents<CampoTexto>().First(c => c.Instance.Etiqueta == etiqueta)
            .Find("input").InputAsync(new ChangeEventArgs { Value = valor });

    private async Task<IRenderedComponent<ConfigurarPlantilla>> EditorConEtiquetaCambiadaAsync(MediatorFalso mediador)
    {
        var cut = Renderizar(mediador);
        await CajaDe(cut, "Razón social").ClickAsync(new MouseEventArgs());
        await EscribirAsync(cut, "Etiqueta", "Razón social de la contrata");
        return cut;
    }

    [Fact]
    public async Task Salir_con_un_campo_editado_sin_guardar_pregunta()
    {
        var cut = await EditorConEtiquetaCambiadaAsync(new MediatorFalso { Version = Result.Exito(Detalle(_versionId)) });

        await cut.SalirYComprobarQuePreguntaAsync(Navegacion);
    }

    [Fact]
    public async Task La_version_recien_cargada_no_es_un_cambio()
    {
        var cut = Renderizar(new MediatorFalso { Version = Result.Exito(Detalle(_versionId)) });
        await CajaDe(cut, "Razón social").ClickAsync(new MouseEventArgs());

        await cut.SalirYComprobarQueNoPreguntaAsync(Navegacion, "cargar la versión y seleccionar una caja no cambia nada");
    }

    [Fact]
    public async Task Mover_una_caja_sobre_el_PDF_es_un_cambio()
    {
        var cut = Renderizar(new MediatorFalso { Version = Result.Exito(Detalle(_versionId)) });
        var idLocal = int.Parse(CajaDe(cut, "Razón social").GetAttribute("data-id-local")!, System.Globalization.CultureInfo.InvariantCulture);

        await cut.InvokeAsync(() => cut.Instance.ActualizarPosicionAsync(idLocal, 60, 140, 200, 22));

        await cut.SalirYComprobarQuePreguntaAsync(Navegacion);
    }

    [Fact]
    public async Task Tras_guardar_los_cambios_salir_no_pregunta()
    {
        var cut = await EditorConEtiquetaCambiadaAsync(new MediatorFalso { Version = Result.Exito(Detalle(_versionId)) });

        await Boton(cut, "Guardar cambios").ClickAsync(new MouseEventArgs());

        await cut.SalirYComprobarQueNoPreguntaAsync(Navegacion, "lo editado ya está guardado");
    }

    /// <summary>
    /// Revisión Codex (lote E): los controles siguen editables mientras el guardado está en
    /// vuelo. Lo que se cambia entonces no viaja en el comando, así que al terminar el
    /// guardado sigue siendo un cambio: la instantánea es la de lo enviado, no la del final.
    /// El clic no se espera: su manejador se queda dentro del guardado retenido.
    /// </summary>
    [Fact]
    public async Task Lo_cambiado_mientras_se_guarda_sigue_contando_como_cambio()
    {
        var respuesta = new TaskCompletionSource<object>();
        var mediador = new MediatorFalso { Version = Result.Exito(Detalle(_versionId)) };
        var cut = await EditorConEtiquetaCambiadaAsync(mediador);
        mediador.Retener = p => p is GuardarElementosPlantillaCommand ? respuesta.Task : null;

        var guardado = Boton(cut, "Guardar cambios").ClickAsync(new MouseEventArgs());
        await EscribirAsync(cut, "Etiqueta", "Escrito durante el guardado");
        await cut.InvokeAsync(() => respuesta.SetResult(Result.Exito()));
        await guardado;

        await cut.SalirYComprobarQuePreguntaAsync(Navegacion);
    }

    [Fact]
    public async Task Una_caja_movida_mientras_se_guarda_sigue_contando_como_cambio()
    {
        var respuesta = new TaskCompletionSource<object>();
        var mediador = new MediatorFalso { Version = Result.Exito(Detalle(_versionId)) };
        var cut = Renderizar(mediador);
        var idLocal = int.Parse(CajaDe(cut, "Razón social").GetAttribute("data-id-local")!, System.Globalization.CultureInfo.InvariantCulture);
        mediador.Retener = p => p is GuardarElementosPlantillaCommand ? respuesta.Task : null;

        var guardado = Boton(cut, "Guardar cambios").ClickAsync(new MouseEventArgs());
        await cut.InvokeAsync(() => cut.Instance.ActualizarPosicionAsync(idLocal, 60, 140, 200, 22));
        await cut.InvokeAsync(() => respuesta.SetResult(Result.Exito()));
        await guardado;

        await cut.SalirYComprobarQuePreguntaAsync(Navegacion);
    }

    /// <summary>Mismo hueco en la generación: lo escrito mientras se genera no se usó.</summary>
    [Fact]
    public async Task Lo_escrito_mientras_se_genera_sigue_contando_como_cambio()
    {
        var trabajadorId = Guid.NewGuid();
        var respuesta = new TaskCompletionSource<object>();
        var mediador = new MediatorFalso
        {
            Version = Result.Exito(Detalle(_versionId, estado: EstadoConfiguracionPlantilla.Confirmada,
                elementos: [Elemento("Observaciones", fuente: FuenteDatoPlantilla.Manual)])),
            Retener = p => p switch
            {
                ObtenerTrabajadoresParaSelectorQuery => Task.FromResult<object>(
                    (IReadOnlyList<TrabajadorSelectorDto>)[new TrabajadorSelectorDto(trabajadorId, "Ana Pérez", null, null)]),
                GenerarDocumentoIndividualCommand => respuesta.Task,
                _ => null
            }
        };
        var cut = Renderizar(mediador);
        await cut.FindComponents<CampoSelect>().First(c => c.Instance.Etiqueta == "Trabajador")
            .Find("select").ChangeAsync(new ChangeEventArgs { Value = trabajadorId.ToString() });
        await EscribirAsync(cut, "Observaciones", "Acceso por la puerta 3");

        var generacion = Boton(cut, "Generar documento").ClickAsync(new MouseEventArgs());
        await EscribirAsync(cut, "Observaciones", "Acceso por la puerta 5");
        await cut.InvokeAsync(() => respuesta.SetResult(
            Result.Exito(new GenerarDocumentoIndividualResultadoDto(Guid.NewGuid(), Guid.NewGuid(), []))));
        await generacion;

        await cut.SalirYComprobarQuePreguntaAsync(Navegacion);
    }

    /// <summary>Y en el lote: lo escrito mientras se crea el lote no viajó en él.</summary>
    [Fact]
    public async Task Lo_escrito_mientras_se_crea_el_lote_sigue_contando_como_cambio()
    {
        var trabajadorId = Guid.NewGuid();
        var respuesta = new TaskCompletionSource<object>();
        var mediador = new MediatorFalso
        {
            Version = Result.Exito(Detalle(_versionId, estado: EstadoConfiguracionPlantilla.Confirmada,
                elementos: [Elemento("Observaciones", fuente: FuenteDatoPlantilla.Manual)])),
            Retener = p => p switch
            {
                ObtenerTrabajadoresParaSelectorQuery => Task.FromResult<object>(
                    (IReadOnlyList<TrabajadorSelectorDto>)[new TrabajadorSelectorDto(trabajadorId, "Ana Pérez", null, null)]),
                IniciarLoteGeneracionDocumentosCommand => respuesta.Task,
                _ => null
            }
        };
        var cut = Renderizar(mediador);
        await cut.FindAll("input[type=radio]")[1].ChangeAsync(new ChangeEventArgs { Value = true });
        await cut.Find(".editor-plantilla-lista-trabajadores input").ChangeAsync(new ChangeEventArgs { Value = true });
        await EscribirAsync(cut, "Observaciones", "Acceso por la puerta 3");

        var lote = Boton(cut, "Generar lote").ClickAsync(new MouseEventArgs());
        await EscribirAsync(cut, "Observaciones", "Acceso por la puerta 5");
        // Sin items: el lote se crea y no queda nada que procesar.
        await cut.InvokeAsync(() => respuesta.SetResult(
            Result.Exito(new IniciarLoteGeneracionDocumentosResultadoDto(Guid.NewGuid(), []))));
        await lote;

        await cut.SalirYComprobarQuePreguntaAsync(Navegacion);
    }

    [Fact]
    public async Task Confirmar_con_cambios_vuelve_al_catalogo_sin_preguntar()
    {
        var cut = await EditorConEtiquetaCambiadaAsync(new MediatorFalso { Version = Result.Exito(Detalle(_versionId)) });

        await Boton(cut, "Confirmar plantilla").ClickAsync(new MouseEventArgs());
        await Boton(cut, "Sí, confirmar").ClickAsync(new MouseEventArgs());

        Navegacion.Uri.Should().EndWith("/plantillas", "confirmar guarda antes de volver al catálogo: no hay nada que perder");
        cut.FindAll(".modal-pie button").Should().NotContain(b => b.TextContent.Trim() == "Salir y descartar");
    }

    [Fact]
    public async Task En_una_version_confirmada_lo_escrito_para_generar_es_un_cambio()
    {
        var cut = Renderizar(new MediatorFalso
        {
            Version = Result.Exito(Detalle(_versionId, estado: EstadoConfiguracionPlantilla.Confirmada,
                elementos: [Elemento("Observaciones", fuente: FuenteDatoPlantilla.Manual)]))
        });

        await EscribirAsync(cut, "Observaciones", "Acceso por la puerta 3");

        await cut.SalirYComprobarQuePreguntaAsync(Navegacion);
    }

    [Fact]
    public async Task En_el_alta_escribir_el_nombre_es_un_cambio_y_sin_tocar_nada_no()
    {
        Registrar(new MediatorFalso());
        var cut = Render<ConfigurarPlantilla>();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Crear y configurar campos"));

        await cut.SalirYComprobarQueNoPreguntaAsync(Navegacion, "el alta recién abierta no tiene nada que perder");

        await EscribirAsync(cut, "Nombre", "Anexo II");
        await cut.SalirYComprobarQuePreguntaAsync(Navegacion);
    }
}
