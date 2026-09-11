using CaeManager.Application.Asignaciones.Commands.CrearAsignacion;
using CaeManager.Application.Centros.Commands.CrearCentro;
using CaeManager.Application.Clientes.Commands.CrearCliente;
using CaeManager.Application.Clientes.Queries.ObtenerClientesParaSelector;
using CaeManager.Application.Empresas.Commands.CrearEmpresa;
using CaeManager.Application.Empresas.Commands.EditarEmpresa;
using CaeManager.Application.Empresas.Queries.ObtenerEmpresaPorId;
using CaeManager.Application.Empresas.Queries.ObtenerEmpresasParaSelector;
using CaeManager.Application.Trabajadores.Commands.CrearTrabajador;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Workspace;
using FluentValidation;
using Microsoft.AspNetCore.Components;

namespace CaeManager.Web.Features.Clientes.Pages;

/// <summary>
/// Asistente de alta encadenada Empresa → Cliente → Centro → Trabajadores.
/// Guardado incremental real, no una transacción larga: cada paso llama a
/// su Command existente y el resultado queda persistido de inmediato — si
/// el usuario se detiene a mitad de camino, lo ya creado no se pierde.
/// "Terminar aquí" es literal: no deshace nada, solo dirige a lo que ya se
/// guardó.
///
/// El orden Empresa → Cliente reemplaza al Cliente → Empresa anterior:
/// desde F3b "crear un Cliente" ya es crear una Empresa contraparte
/// (<see cref="CaeManager.Domain.Empresas.Empresa.CrearComoCliente"/>), y el
/// orden viejo arrastraba la distinción previa a la congelación de
/// terminología, cuando Cliente y Empresa eran conceptos separados sin
/// relación expresada. Aquí la Empresa operativa (donde cuelgan Centros y
/// Trabajadores) se crea primero y el Cliente comercial se vincula después
/// — o al revés si se entra por una rama que ya trae el Cliente resuelto.
///
/// El asistente admite entrar por cualquier rama vía query string
/// (<see cref="EmpresaId"/>/<see cref="ClienteId"/>/<see cref="CentroId"/>):
/// quien ya creó una Empresa, un Cliente o un Centro en otra pantalla puede
/// continuar aquí sin repetir el paso ya hecho. El paso de partida es
/// siempre el primero, en orden canónico, cuyo identificador no llegó por
/// query string.
/// </summary>
public partial class AltaGuiada : ComponentBase
{
    private static readonly IReadOnlyList<PasoDefinicion> Pasos =
    [
        new("empresa", "Empresa"),
        new("cliente", "Cliente"),
        new("centro", "Centro"),
        new("trabajadores", "Trabajadores")
    ];

    [SupplyParameterFromQuery(Name = "empresaId")]
    private Guid? EmpresaIdEntrada { get; set; }

    [SupplyParameterFromQuery(Name = "empresaNombre")]
    private string? EmpresaNombreEntrada { get; set; }

    [SupplyParameterFromQuery(Name = "clienteId")]
    private Guid? ClienteIdEntrada { get; set; }

    [SupplyParameterFromQuery(Name = "clienteNombre")]
    private string? ClienteNombreEntrada { get; set; }

    [SupplyParameterFromQuery(Name = "centroId")]
    private Guid? CentroIdEntrada { get; set; }

    [SupplyParameterFromQuery(Name = "centroNombre")]
    private string? CentroNombreEntrada { get; set; }

    private string _pasoActual = "empresa";
    private readonly HashSet<string> _pasosCompletados = [];

    // Paso 1: Empresa — nueva, o una ya existente que se vincula al Cliente
    // en cuanto este exista (mismo Command que la vinculación del paso 2).
    private bool _empresaExistente;
    private string _razonSocialEmpresa = string.Empty;
    private string _cifEmpresa = string.Empty;
    private string _empresaExistenteId = string.Empty;
    private IReadOnlyList<EmpresaSelectorDto> _empresasCatalogo = [];

    // Arranca en true: entre el primer render y el final de OnInitializedAsync
    // el catálogo está vacío porque todavía no ha llegado, no porque no haya
    // empresas. Sin esta bandera esos dos casos son indistinguibles y el
    // paso anunciaría "todavía no hay ninguna empresa dada de alta" mientras
    // carga.
    private bool _cargandoCatalogoEmpresas = true;
    private bool _guardandoEmpresa;
    private string? _mensajeErrorEmpresa;
    private Dictionary<string, string> _erroresEmpresa = new();

    private Guid? _empresaId;
    private string _empresaNombre = string.Empty;

    // Paso 2: Cliente — nuevo, o uno ya existente al que se vincula la
    // Empresa del paso 1.
    private bool _clienteExistente;
    private string _razonSocialCliente = string.Empty;
    private string _cifCliente = string.Empty;
    private bool _esCriticoCliente;
    private string _notasCliente = string.Empty;
    private string _clienteExistenteId = string.Empty;
    private IReadOnlyList<ClienteSelectorDto> _clientesCatalogo = [];
    private bool _cargandoCatalogoClientes = true;
    private bool _guardandoCliente;
    private string? _mensajeErrorCliente;
    private Dictionary<string, string> _erroresCliente = new();

    private Guid? _clienteId;
    private string _clienteNombre = string.Empty;

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

    // Paso 4: Trabajadores — de la Empresa del asistente, asignados de
    // inmediato al último Centro creado (mismo patrón de "guardar y crear
    // otro" que el paso de Centro).
    private string _nombreTrabajador = string.Empty;
    private string _apellidosTrabajador = string.Empty;
    private string _dniTrabajador = string.Empty;
    private string _emailTrabajador = string.Empty;
    private string _puestoTrabajador = string.Empty;
    private bool _guardandoTrabajador;
    private string? _mensajeErrorTrabajador;
    private Dictionary<string, string> _erroresTrabajador = new();

    private string _ultimoTrabajadorNombre = string.Empty;
    private int _trabajadoresCreados;

    protected override async Task OnInitializedAsync()
    {
        // Catálogos completos, no filtrados: en cualquiera de los dos pasos
        // de vinculación el registro que se está vinculando es el que acaba
        // de crearse en el otro, así que un filtro "ya asociados entre sí"
        // siempre estaría vacío — lo que hace falta aquí es el catálogo
        // entero, igual que al dar de alta un Trabajador.
        try
        {
            _empresasCatalogo = await Mediator.Send(new ObtenerEmpresasParaSelectorQuery());
        }
        finally
        {
            _cargandoCatalogoEmpresas = false;
        }

        try
        {
            _clientesCatalogo = await Mediator.Send(new ObtenerClientesParaSelectorQuery());
        }
        finally
        {
            _cargandoCatalogoClientes = false;
        }

        if (EmpresaIdEntrada is { } empresaId)
        {
            _empresaId = empresaId;
            _empresaNombre = EmpresaNombreEntrada ?? string.Empty;
            _pasosCompletados.Add("empresa");
        }

        if (ClienteIdEntrada is { } clienteId)
        {
            _clienteId = clienteId;
            _clienteNombre = ClienteNombreEntrada ?? string.Empty;
            _pasosCompletados.Add("cliente");
        }

        if (CentroIdEntrada is { } centroId)
        {
            _ultimoCentroId = centroId;
            _ultimoCentroNombre = CentroNombreEntrada ?? string.Empty;
            _centrosCreados = 1;
            _pasosCompletados.Add("centro");
        }

        // El paso de partida es el primero, en orden canónico, que no llegó
        // ya resuelto por query string — no siempre "empresa": quien entra
        // con clienteId ya fijado retoma en "empresa" solo si tampoco trae
        // empresaId, y quien trae los tres retoma directamente en
        // "trabajadores".
        _pasoActual = Pasos.Select(p => p.Clave).FirstOrDefault(p => !_pasosCompletados.Contains(p)) ?? "trabajadores";
    }

    private static readonly IReadOnlyList<BreadcrumbElemento> MigueroEstatico =
        [new BreadcrumbElemento("Clientes"), new BreadcrumbElemento("Alta guiada")];

    private IReadOnlyList<BreadcrumbElemento> Miguero => MigueroEstatico;

    private void IrABreadcrumb(int indice)
    {
        if (indice == 0)
            NavigationManager.NavigateTo("/clientes");
    }

    private async Task GuardarEmpresaAsync()
    {
        _guardandoEmpresa = true;
        _mensajeErrorEmpresa = null;
        _erroresEmpresa = new Dictionary<string, string>();

        try
        {
            if (_empresaExistente)
                await VincularEmpresaExistenteAsync();
            else
                await CrearEmpresaNuevaAsync();
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

    private async Task VincularEmpresaExistenteAsync()
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

        // Si el Cliente ya está resuelto (rama que entró con clienteId), la
        // vinculación se hace ya mismo — igual que hacía el paso 2 antiguo.
        // Si no, el paso siguiente ("cliente") es el que decide con qué
        // Cliente se vincula esta Empresa.
        if (_clienteId is { } clienteId && !existente.ClienteIds.Contains(clienteId))
        {
            var clienteIds = existente.ClienteIds.Append(clienteId).ToList();
            var resultado = await Mediator.Send(
                new EditarEmpresaCommand(
                    existente.Id, existente.RazonSocial, existente.Cif, clienteIds,
                    existente.Cnae, existente.ConvenioAplicable, existente.EsActividadAnexoI, existente.Version));

            if (resultado.EsFallido)
            {
                _mensajeErrorEmpresa = resultado.Error.Mensaje;
                return;
            }
        }

        _empresaId = existente.Id;
        _empresaNombre = existente.RazonSocial;
        _pasosCompletados.Add("empresa");
        _pasoActual = SiguientePasoTrasEmpresa();
        ToastService.Mostrar("Empresa vinculada correctamente.", TonoToast.Exito);
    }

    private async Task CrearEmpresaNuevaAsync()
    {
        var clienteIds = _clienteId is { } clienteId ? new List<Guid> { clienteId } : [];
        var resultado = await Mediator.Send(new CrearEmpresaCommand(
            _razonSocialEmpresa, string.IsNullOrWhiteSpace(_cifEmpresa) ? null : _cifEmpresa, clienteIds));

        if (resultado.EsFallido)
        {
            _mensajeErrorEmpresa = resultado.Error.Mensaje;
            return;
        }

        _empresaId = resultado.Valor;
        _empresaNombre = _razonSocialEmpresa;
        _pasosCompletados.Add("empresa");
        _pasoActual = SiguientePasoTrasEmpresa();
        ToastService.Mostrar("Empresa creada correctamente.", TonoToast.Exito);
    }

    private string SiguientePasoTrasEmpresa() => _clienteId is not null ? "centro" : "cliente";

    private async Task GuardarClienteAsync()
    {
        _guardandoCliente = true;
        _mensajeErrorCliente = null;
        _erroresCliente = new Dictionary<string, string>();

        try
        {
            if (_clienteExistente)
                await VincularClienteExistenteAsync();
            else
                await CrearClienteNuevoAsync();
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

    private async Task VincularClienteExistenteAsync()
    {
        if (!Guid.TryParse(_clienteExistenteId, out var clienteId))
        {
            _mensajeErrorCliente = "Selecciona un cliente.";
            return;
        }

        await VincularEmpresaAClienteAsync(clienteId);
        if (_mensajeErrorCliente is not null) return;

        _clienteId = clienteId;
        _clienteNombre = _clientesCatalogo.First(c => c.Id == clienteId).RazonSocial;
        _pasosCompletados.Add("cliente");
        _pasoActual = "centro";
        ToastService.Mostrar("Empresa vinculada al cliente.", TonoToast.Exito);
    }

    private async Task CrearClienteNuevoAsync()
    {
        var resultado = await Mediator.Send(new CrearClienteCommand(
            _razonSocialCliente, _cifCliente, _esCriticoCliente,
            string.IsNullOrWhiteSpace(_notasCliente) ? null : _notasCliente));

        if (resultado.EsFallido)
        {
            _mensajeErrorCliente = resultado.Error.Mensaje;
            return;
        }

        var clienteId = resultado.Valor;

        await VincularEmpresaAClienteAsync(clienteId);
        if (_mensajeErrorCliente is not null) return;

        _clienteId = clienteId;
        _clienteNombre = _razonSocialCliente;
        _pasosCompletados.Add("cliente");
        _pasoActual = "centro";
        ToastService.Mostrar("Cliente creado correctamente.", TonoToast.Exito);
    }

    /// <summary>
    /// Si la Empresa ya está resuelta (el caso normal: viene del paso 1), la
    /// vincula al Cliente que se acaba de crear o elegir aquí. Si no (rama
    /// que entró solo con clienteId y todavía no pasó por "empresa" — no
    /// debería ocurrir con el orden canónico, pero un Cliente sin Empresa no
    /// tiene con qué vincular), no hay nada que editar.
    /// </summary>
    private async Task VincularEmpresaAClienteAsync(Guid clienteId)
    {
        if (_empresaId is not { } empresaId) return;

        var existente = await Mediator.Send(new ObtenerEmpresaPorIdQuery(empresaId));
        if (existente is null)
        {
            _mensajeErrorCliente = "No encontramos la empresa de este alta. Puede que ya se haya eliminado.";
            return;
        }

        if (existente.ClienteIds.Contains(clienteId)) return;

        var clienteIds = existente.ClienteIds.Append(clienteId).ToList();
        var resultado = await Mediator.Send(
            new EditarEmpresaCommand(
                existente.Id, existente.RazonSocial, existente.Cif, clienteIds,
                existente.Cnae, existente.ConvenioAplicable, existente.EsActividadAnexoI, existente.Version));

        if (resultado.EsFallido)
            _mensajeErrorCliente = resultado.Error.Mensaje;
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

    private void IrAPasoTrabajadores() => _pasoActual = "trabajadores";

    private async Task GuardarTrabajadorAsync()
    {
        if (_empresaId is null) return;

        _guardandoTrabajador = true;
        _mensajeErrorTrabajador = null;
        _erroresTrabajador = new Dictionary<string, string>();

        try
        {
            var resultado = await Mediator.Send(new CrearTrabajadorCommand(
                _empresaId.Value, null, _nombreTrabajador, _apellidosTrabajador, _dniTrabajador,
                null, string.IsNullOrWhiteSpace(_emailTrabajador) ? null : _emailTrabajador, null,
                Puesto: string.IsNullOrWhiteSpace(_puestoTrabajador) ? null : _puestoTrabajador));

            if (resultado.EsFallido)
            {
                _mensajeErrorTrabajador = resultado.Error.Mensaje;
                return;
            }

            var trabajadorId = resultado.Valor;

            // El centro es el que se acaba de crear (o el que llegó por query
            // string en una rama que entró ya con él resuelto) — asignar de
            // inmediato es lo que cierra "dar de alta a los trabajadores en
            // la empresa que ya se creó y poder asignarlos a los centros a
            // los que ingresan" sin salir del asistente.
            if (_ultimoCentroId is { } centroId)
            {
                var resultadoAsignacion = await Mediator.Send(
                    new CrearAsignacionCommand(trabajadorId, centroId, DateOnly.FromDateTime(DateTime.Today)));

                if (resultadoAsignacion.EsFallido)
                {
                    _mensajeErrorTrabajador = resultadoAsignacion.Error.Mensaje;
                    return;
                }
            }

            _pasosCompletados.Add("trabajadores");
            _ultimoTrabajadorNombre = $"{_nombreTrabajador} {_apellidosTrabajador}";
            _trabajadoresCreados++;
            ToastService.Mostrar("Trabajador creado y asignado correctamente.", TonoToast.Exito);

            _nombreTrabajador = string.Empty;
            _apellidosTrabajador = string.Empty;
            _dniTrabajador = string.Empty;
            _emailTrabajador = string.Empty;
            _puestoTrabajador = string.Empty;
        }
        catch (ValidationException ex)
        {
            _erroresTrabajador = ex.Errors.GroupBy(e => e.PropertyName).ToDictionary(g => g.Key, g => g.First().ErrorMessage);
        }
        catch (Exception)
        {
            _mensajeErrorTrabajador = "No pudimos guardar los cambios. Intenta nuevamente en unos segundos.";
        }
        finally
        {
            _guardandoTrabajador = false;
        }
    }

    private Task VerClienteAsync() =>
        _clienteId is null ? Task.CompletedTask : WorkspaceService.AbrirAsync(EntidadWorkspace.Cliente, _clienteId.Value, _clienteNombre, "informacion");

    private Task VerEmpresaAsync() =>
        _empresaId is null ? Task.CompletedTask : WorkspaceService.AbrirAsync(EntidadWorkspace.Empresa, _empresaId.Value, _empresaNombre, "informacion");

    private Task AnadirRequisitosAsync() =>
        _ultimoCentroId is null ? Task.CompletedTask : WorkspaceService.AbrirAsync(EntidadWorkspace.Centro, _ultimoCentroId.Value, _ultimoCentroNombre, "requisitos");

    private static string? ObtenerError(Dictionary<string, string> errores, string campo) => errores.GetValueOrDefault(campo);

    /// <summary>
    /// Validación inline al salir del campo, en los cuatro pasos del
    /// asistente (mismo patrón que Centros.razor, UX_PATTERNS.md, P1-18 de
    /// docs/business/MATURITY_REVIEW.md).
    /// </summary>
    private Task ValidarRazonSocialClienteAsync() => ValidarCampoClienteAsync(nameof(CrearClienteCommand.RazonSocial));

    private Task ValidarCifClienteAsync() => ValidarCampoClienteAsync(nameof(CrearClienteCommand.Cif));

    private async Task ValidarCampoClienteAsync(string campo)
    {
        var notas = string.IsNullOrWhiteSpace(_notasCliente) ? null : _notasCliente;
        var resultado = await ValidadorClienteCrear.ValidateAsync(
            new CrearClienteCommand(_razonSocialCliente, _cifCliente, _esCriticoCliente, notas),
            opciones => opciones.IncludeProperties(campo));

        if (resultado.IsValid) _erroresCliente.Remove(campo);
        else _erroresCliente[campo] = resultado.Errors[0].ErrorMessage;
    }

    private Task ValidarRazonSocialEmpresaAsync() => ValidarCampoEmpresaAsync(nameof(CrearEmpresaCommand.RazonSocial));

    private Task ValidarCifEmpresaAsync() => ValidarCampoEmpresaAsync(nameof(CrearEmpresaCommand.Cif));

    private async Task ValidarCampoEmpresaAsync(string campo)
    {
        var cif = string.IsNullOrWhiteSpace(_cifEmpresa) ? null : _cifEmpresa;
        var resultado = await ValidadorEmpresaCrear.ValidateAsync(
            new CrearEmpresaCommand(_razonSocialEmpresa, cif, []),
            opciones => opciones.IncludeProperties(campo));

        if (resultado.IsValid) _erroresEmpresa.Remove(campo);
        else _erroresEmpresa[campo] = resultado.Errors[0].ErrorMessage;
    }

    private async Task ValidarNombreCentroAsync()
    {
        const string campo = nameof(CrearCentroCommand.Nombre);
        var resultado = await ValidadorCentroCrear.ValidateAsync(
            new CrearCentroCommand(Guid.Empty, Guid.Empty, _nombreCentro, null, null, null, null),
            opciones => opciones.IncludeProperties(campo));

        if (resultado.IsValid) _erroresCentro.Remove(campo);
        else _erroresCentro[campo] = resultado.Errors[0].ErrorMessage;
    }

    private async Task ValidarCampoTrabajadorAsync(string campo)
    {
        var resultado = await ValidadorTrabajadorCrear.ValidateAsync(
            new CrearTrabajadorCommand(_empresaId, null, _nombreTrabajador, _apellidosTrabajador, _dniTrabajador, null,
                string.IsNullOrWhiteSpace(_emailTrabajador) ? null : _emailTrabajador, null,
                Puesto: string.IsNullOrWhiteSpace(_puestoTrabajador) ? null : _puestoTrabajador),
            opciones => opciones.IncludeProperties(campo));

        if (resultado.IsValid) _erroresTrabajador.Remove(campo);
        else _erroresTrabajador[campo] = resultado.Errors[0].ErrorMessage;
    }

    private Task ValidarNombreTrabajadorAsync() => ValidarCampoTrabajadorAsync(nameof(CrearTrabajadorCommand.Nombre));

    private Task ValidarApellidosTrabajadorAsync() => ValidarCampoTrabajadorAsync(nameof(CrearTrabajadorCommand.Apellidos));

    private Task ValidarDniTrabajadorAsync() => ValidarCampoTrabajadorAsync(nameof(CrearTrabajadorCommand.Dni));
}
