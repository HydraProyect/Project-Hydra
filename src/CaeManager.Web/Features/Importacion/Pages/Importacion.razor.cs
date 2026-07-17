using CaeManager.Application.Importacion;
using CaeManager.Application.Importacion.Commands.EjecutarImportacion;
using CaeManager.Application.Importacion.Queries;
using CaeManager.Web.Components.DesignSystem;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;

namespace CaeManager.Web.Features.Importacion.Pages;

public partial class Importacion : ComponentBase
{
    private const long TamanoMaximoArchivoBytes = 20 * 1024 * 1024;

    private static readonly string[] MensajesAnalizando =
    [
        "Leyendo el archivo Excel…",
        "Revisando Clientes y Centros…",
        "Comprobando Empleados y Documentos…",
        "Verificando Asignaciones…",
    ];

    private static readonly string[] MensajesImportando =
    [
        "Creando Clientes y Centros…",
        "Dando de alta Trabajadores…",
        "Registrando Documentos…",
        "Asignando Trabajadores a sus Centros…",
    ];

    [Inject] private IMediator Mediator { get; set; } = default!;
    [Inject] private ToastService ToastService { get; set; } = default!;

    private bool _analizando;
    private bool _importando;
    private string? _mensajeError;
    private string? _nombreArchivo;
    private byte[]? _contenidoArchivo;
    private PlanImportacionDto? _plan;
    private ResultadoImportacionDto? _resultado;

    private async Task ManejarArchivoSeleccionadoAsync(InputFileChangeEventArgs e)
    {
        var archivo = e.File;
        _mensajeError = null;
        _plan = null;
        _resultado = null;

        if (archivo.Size > TamanoMaximoArchivoBytes)
        {
            ToastService.Mostrar("El archivo no puede superar los 20 MB.", TonoToast.Error);
            return;
        }

        _analizando = true;
        _nombreArchivo = archivo.Name;
        StateHasChanged();

        try
        {
            await using var flujo = archivo.OpenReadStream(TamanoMaximoArchivoBytes);
            using var memoria = new MemoryStream();
            await flujo.CopyToAsync(memoria);
            _contenidoArchivo = memoria.ToArray();

            _plan = await Mediator.Send(new AnalizarImportacionExcelQuery(_contenidoArchivo));
        }
        catch (Exception)
        {
            _mensajeError = "No pudimos leer este archivo. Comprueba que sea el Excel del Cuadro de Control CAE.";
        }
        finally
        {
            _analizando = false;
        }
    }

    private async Task ConfirmarImportacionAsync()
    {
        if (_plan is null) return;

        _importando = true;
        _mensajeError = null;
        StateHasChanged();

        try
        {
            _resultado = await Mediator.Send(new EjecutarImportacionCommand(_plan));
            ToastService.Mostrar("Importación completada.", TonoToast.Exito);
        }
        catch (Exception)
        {
            _mensajeError = "No pudimos completar la importación. Intenta nuevamente en unos segundos.";
        }
        finally
        {
            _importando = false;
        }
    }

    private void EmpezarDeNuevo()
    {
        _nombreArchivo = null;
        _contenidoArchivo = null;
        _plan = null;
        _resultado = null;
        _mensajeError = null;
    }
}
