using CaeManager.Application.Importacion;
using CaeManager.Application.Importacion.Commands.EjecutarImportacionCombinada;
using CaeManager.Application.Importacion.Queries;
using CaeManager.Web.Components.DesignSystem;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;

namespace CaeManager.Web.Features.Clientes.Pages;

public partial class ImportarCombinado : ComponentBase
{
    private const long TamanoMaximoArchivoBytes = 5 * 1024 * 1024;

    private static readonly string[] MensajesAnalizando =
    [
        "Leyendo la plantilla…",
        "Comprobando Clientes y CIF…",
        "Comprobando Empresas y Centros…",
        "Comprobando Trabajadores…",
    ];

    private static readonly string[] MensajesImportando =
    [
        "Creando y actualizando Clientes…",
        "Creando y actualizando Empresas…",
        "Creando y actualizando Centros…",
        "Creando y actualizando Trabajadores…",
    ];

    [Inject] private IMediator Mediator { get; set; } = default!;
    [Inject] private ToastService ToastService { get; set; } = default!;

    private bool _analizando;
    private bool _importando;
    private string? _mensajeError;
    private string? _nombreArchivo;
    private PlanImportacionCombinadaDto? _plan;
    private ResultadoImportacionCombinadaDto? _resultado;
    private bool _reemplazarExistentes;

    private int TotalYaExistian => _plan is null
        ? 0
        : _plan.Clientes.Count(c => c.YaExiste) + _plan.Empresas.Count(e => e.YaExiste)
            + _plan.Centros.Count(c => c.YaExiste) + _plan.Trabajadores.Count(t => t.YaExiste);

    private async Task ManejarArchivoSeleccionadoAsync(InputFileChangeEventArgs e)
    {
        var archivo = e.File;
        _mensajeError = null;
        _plan = null;
        _resultado = null;
        _reemplazarExistentes = false;

        if (archivo.Size > TamanoMaximoArchivoBytes)
        {
            ToastService.Mostrar("El archivo no puede superar los 5 MB.", TonoToast.Error);
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

            _plan = await Mediator.Send(new AnalizarPlantillaCombinadaQuery(memoria.ToArray()));
        }
        catch (Exception)
        {
            _mensajeError = "No pudimos leer este archivo. Comprueba que sea la plantilla combinada descargada desde esta página.";
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
            var resultado = await Mediator.Send(new EjecutarImportacionCombinadaCommand(_plan, _reemplazarExistentes));

            if (resultado.EsFallido)
            {
                _mensajeError = resultado.Error.Mensaje;
                return;
            }

            _resultado = resultado.Valor;
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
        _plan = null;
        _resultado = null;
        _mensajeError = null;
        _reemplazarExistentes = false;
    }
}
