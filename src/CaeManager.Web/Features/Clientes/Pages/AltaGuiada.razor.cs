using CaeManager.Application.Centros.Commands.CrearCentro;
using CaeManager.Application.Clientes.Commands.CrearCliente;
using CaeManager.Application.Empresas.Commands.CrearEmpresa;
using CaeManager.Application.Empresas.Commands.EditarEmpresa;
using CaeManager.Application.Empresas.Queries.ObtenerEmpresaPorId;
using CaeManager.Application.Empresas.Queries.ObtenerEmpresasParaSelector;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Workspace;
using FluentValidation;
using Microsoft.AspNetCore.Components;

namespace CaeManager.Web.Features.Clientes.Pages;

/// <summary>
/// Asistente de alta encadenada Cliente → Empresa → Centro (Fase A del
/// paquete de UI del gestor). Guardado incremental real, no una transacción
/// larga: cada paso llama a su Command existente y el resultado queda
/// persistido de inmediato — si el usuario se detiene en el paso 2, el
/// Cliente ya existe y se puede retomar desde /clientes. "Terminar aquí" es
/// literal: no deshace nada, solo dirige a lo que ya se guardó.
/// </summary>
public partial class AltaGuiada : ComponentBase
{
    private static readonly IReadOnlyList<PasoDefinicion> Pasos =
    [
        new("cliente", "Cliente"),
        new("empresa", "Empresa"),
        new("centro", "Centro")
    ];

    private string _pasoActual = "cliente";
    private readonly HashSet<string> _pasosCompletados = [];

    // Paso 1: Cliente.
    private string _razonSocialCliente = string.Empty;
    private string _cifCliente = string.Empty;
    private bool _esCriticoCliente;
    private string _notasCliente = string.Empty;
    private bool _guardandoCliente;
    private string? _mensajeErrorCliente;
    private Dictionary<string, string> _erroresCliente = new();

    private Guid? _clienteId;
    private string _clienteNombre = string.Empty;

    // Paso 2: Empresa — nueva, o una ya existente que se vincula al Cliente
    // recién creado (EditarEmpresaCommand ya acepta ClienteIds, así que
    // vincular no exige ningún Command nuevo).
    private bool _empresaExistente;
    private string _razonSocialEmpresa = string.Empty;
    private string _cifEmpresa = string.Empty;
    private string _empresaExistenteId = string.Empty;
    private IReadOnlyList<EmpresaSelectorDto> _empresasCatalogo = [];
    private bool _guardandoEmpresa;
    private string? _mensajeErrorEmpresa;
    private Dictionary<string, string> _erroresEmpresa = new();

    private Guid? _empresaId;
    private string _empresaNombre = string.Empty;

    // Paso 3: Centro — Cliente y Empresa viajan fijos (nunca se editan aquí);
    // "Guardar centro" se puede pulsar varias veces sin salir del paso.
    private string _nombreCentro = string.Empty;
    private string _codigoCentro = string.Empty;
    private string _direccionCentro = string.Empty;
    private string _contactoCentro = string.Empty;
    private string _contratoVigenteHasta = string.Empty;
    private bool _guardandoCentro;
    private string? _mensajeErrorCentro;
    private Dictionary<string, string> _erroresCentro = new();

    private Guid? _ultimoCentroId;
    private string _ultimoCentroNombre = string.Empty;
    private int _centrosCreados;

    protected override async Task OnInitializedAsync()
    {
        // Catálogo completo, no filtrado por Cliente: en el paso 2 el Cliente
        // que se está vinculando es el que acaba de crearse, así que el
        // filtro "empresas ya asociadas a este cliente" siempre estaría
        // vacío — lo que hace falta aquí es el catálogo entero, igual que al
        // dar de alta un Trabajador.
        _empresasCatalogo = await Mediator.Send(new ObtenerEmpresasParaSelectorQuery());
    }

    private async Task GuardarClienteAsync()
    {
        _guardandoCliente = true;
        _mensajeErrorCliente = null;
        _erroresCliente = new Dictionary<string, string>();

        try
        {
            var resultado = await Mediator.Send(new CrearClienteCommand(
                _razonSocialCliente, _cifCliente, _esCriticoCliente,
                string.IsNullOrWhiteSpace(_notasCliente) ? null : _notasCliente));

            if (resultado.EsFallido)
            {
                _mensajeErrorCliente = resultado.Error.Mensaje;
                return;
            }

            _clienteId = resultado.Valor;
            _clienteNombre = _razonSocialCliente;
            _pasosCompletados.Add("cliente");
            _pasoActual = "empresa";
            ToastService.Mostrar("Cliente creado correctamente.", TonoToast.Exito);
        }
        catch (ValidationException ex)
        {
            _erroresCliente = ex.Errors.GroupBy(e => e.PropertyName).ToDictionary(g => g.Key, g => g.First().ErrorMessage);
        }
        catch (Exception)
        {
            _mensajeErrorCliente = "No pudimos guardar los cambios. Intenta nuevamente en unos segundos.";
        }
        finally
        {
            _guardandoCliente = false;
        }
    }

    private async Task GuardarEmpresaAsync()
    {
        if (_clienteId is null) return;

        _guardandoEmpresa = true;
        _mensajeErrorEmpresa = null;
        _erroresEmpresa = new Dictionary<string, string>();

        try
        {
            if (_empresaExistente)
                await VincularEmpresaExistenteAsync(_clienteId.Value);
            else
                await CrearEmpresaNuevaAsync(_clienteId.Value);
        }
        catch (ValidationException ex)
        {
            _erroresEmpresa = ex.Errors.GroupBy(e => e.PropertyName).ToDictionary(g => g.Key, g => g.First().ErrorMessage);
        }
        catch (Exception)
        {
            _mensajeErrorEmpresa = "No pudimos guardar los cambios. Intenta nuevamente en unos segundos.";
        }
        finally
        {
            _guardandoEmpresa = false;
        }
    }

    private async Task VincularEmpresaExistenteAsync(Guid clienteId)
    {
        if (!Guid.TryParse(_empresaExistenteId, out var empresaId))
        {
            _mensajeErrorEmpresa = "Selecciona una empresa.";
            return;
        }

        var existente = await Mediator.Send(new ObtenerEmpresaPorIdQuery(empresaId));
        if (existente is null)
        {
            _mensajeErrorEmpresa = "No encontramos esta empresa. Puede que ya se haya eliminado.";
            return;
        }

        if (!existente.ClienteIds.Contains(clienteId))
        {
            var clienteIds = existente.ClienteIds.Append(clienteId).ToList();
            var resultado = await Mediator.Send(
                new EditarEmpresaCommand(existente.Id, existente.RazonSocial, existente.Cif, clienteIds, existente.Version));

            if (resultado.EsFallido)
            {
                _mensajeErrorEmpresa = resultado.Error.Mensaje;
                return;
            }
        }

        _empresaId = existente.Id;
        _empresaNombre = existente.RazonSocial;
        _pasosCompletados.Add("empresa");
        _pasoActual = "centro";
        ToastService.Mostrar("Empresa vinculada al cliente.", TonoToast.Exito);
    }

    private async Task CrearEmpresaNuevaAsync(Guid clienteId)
    {
        var resultado = await Mediator.Send(new CrearEmpresaCommand(
            _razonSocialEmpresa, string.IsNullOrWhiteSpace(_cifEmpresa) ? null : _cifEmpresa, [clienteId]));

        if (resultado.EsFallido)
        {
            _mensajeErrorEmpresa = resultado.Error.Mensaje;
            return;
        }

        _empresaId = resultado.Valor;
        _empresaNombre = _razonSocialEmpresa;
        _pasosCompletados.Add("empresa");
        _pasoActual = "centro";
        ToastService.Mostrar("Empresa creada correctamente.", TonoToast.Exito);
    }

    private async Task GuardarCentroAsync()
    {
        if (_clienteId is null || _empresaId is null) return;

        _guardandoCentro = true;
        _mensajeErrorCentro = null;
        _erroresCentro = new Dictionary<string, string>();

        try
        {
            var contratoVigenteHasta = DateOnly.TryParse(_contratoVigenteHasta, out var fecha) ? fecha : (DateOnly?)null;

            var resultado = await Mediator.Send(new CrearCentroCommand(
                _clienteId.Value, _empresaId.Value, _nombreCentro,
                string.IsNullOrWhiteSpace(_codigoCentro) ? null : _codigoCentro,
                string.IsNullOrWhiteSpace(_direccionCentro) ? null : _direccionCentro,
                string.IsNullOrWhiteSpace(_contactoCentro) ? null : _contactoCentro,
                contratoVigenteHasta));

            if (resultado.EsFallido)
            {
                _mensajeErrorCentro = resultado.Error.Mensaje;
                return;
            }

            _pasosCompletados.Add("centro");
            _ultimoCentroId = resultado.Valor;
            _ultimoCentroNombre = _nombreCentro;
            _centrosCreados++;
            ToastService.Mostrar("Centro creado correctamente.", TonoToast.Exito);

            // Limpia el formulario sin abandonar el paso — "Guardar y crear
            // otro centro" (UX_PATTERNS.md § Crear) manteniendo Cliente/Empresa.
            _nombreCentro = string.Empty;
            _codigoCentro = string.Empty;
            _direccionCentro = string.Empty;
            _contactoCentro = string.Empty;
            _contratoVigenteHasta = string.Empty;
        }
        catch (ValidationException ex)
        {
            _erroresCentro = ex.Errors.GroupBy(e => e.PropertyName).ToDictionary(g => g.Key, g => g.First().ErrorMessage);
        }
        catch (Exception)
        {
            _mensajeErrorCentro = "No pudimos guardar los cambios. Intenta nuevamente en unos segundos.";
        }
        finally
        {
            _guardandoCentro = false;
        }
    }

    private Task VerClienteAsync() =>
        _clienteId is null ? Task.CompletedTask : WorkspaceService.AbrirAsync(EntidadWorkspace.Cliente, _clienteId.Value, _clienteNombre, "informacion");

    private Task VerEmpresaAsync() =>
        _empresaId is null ? Task.CompletedTask : WorkspaceService.AbrirAsync(EntidadWorkspace.Empresa, _empresaId.Value, _empresaNombre, "informacion");

    private Task AnadirRequisitosAsync() =>
        _ultimoCentroId is null ? Task.CompletedTask : WorkspaceService.AbrirAsync(EntidadWorkspace.Centro, _ultimoCentroId.Value, _ultimoCentroNombre, "requisitos");

    /// <summary>
    /// Asignar ya no es una página aparte (Centro 360, PLAN-EJECUCION-UX.md
    /// § 0.1) — lleva a /centros filtrado por el nombre de este centro, listo
    /// para expandir su acordeón y usar "+ Asignar trabajador".
    /// </summary>
    private void IrAAsignarTrabajadores() =>
        NavigationManager.NavigateTo($"/centros?q={Uri.EscapeDataString(_ultimoCentroNombre)}");

    private static string? ObtenerError(Dictionary<string, string> errores, string campo) => errores.GetValueOrDefault(campo);
}
