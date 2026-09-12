using CaeManager.Application.Asignaciones.Commands.CrearAsignacion;
using CaeManager.Application.Centros.Commands.CrearCentro;
using CaeManager.Application.Centros.Queries.ObtenerCentroPorId;
using CaeManager.Application.Clientes.Commands.CrearCliente;
using CaeManager.Application.Clientes.Queries.ObtenerClientePorId;
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
/// (<see cref="EmpresaIdEntrada"/>/<see cref="ClienteIdEntrada"/>/
/// <see cref="CentroIdEntrada"/>): quien ya creó una Empresa, un Cliente o
/// un Centro en otra pantalla puede continuar aquí sin repetir el paso ya
/// hecho. Un identificador de la URL es una coordenada, no una autoridad:
/// cada uno se resuelve contra su Query (<c>ObtenerEmpresaPorIdQuery</c>/
/// <c>ObtenerClientePorIdQuery</c>/<c>ObtenerCentroPorIdQuery</c>), que
/// devuelve null tanto si no existe como si es de otro tenant o queda fuera
/// de la cartera de quien mira — las dos situaciones se tratan igual, para
/// no filtrar cuál de las dos es. Solo si resuelve se marca el paso como
/// completado y se pinta el nombre, y ese nombre sale siempre de la
/// respuesta de la Query, nunca del parámetro de la URL. El paso de partida
/// es siempre el primero, en orden canónico, cuyo identificador no resolvió.
/// </summary>
public partial class AltaGuiada : ComponentBase
{
    private static readonly IReadOnlyList<PasoDefinicion> Pasos =
    [
        new("empresa", "Empresa"),
        new("cliente", "Cliente empresarial"),
        new("centro", "Centro"),
        new("trabajadores", "Trabajadores")
    ];

    // Solo el identificador viaja por query string — el nombre nunca se lee
    // de la URL (ver comentario normativo de la clase): se pinta el que
    // devuelve la Query de resolución, o no se pinta nada si no resuelve.
    [SupplyParameterFromQuery(Name = "empresaId")]
    private Guid? EmpresaIdEntrada { get; set; }

    [SupplyParameterFromQuery(Name = "clienteId")]
    private Guid? ClienteIdEntrada { get; set; }

    [SupplyParameterFromQuery(Name = "centroId")]
    private Guid? CentroIdEntrada { get; set; }

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

    /// <summary>
    /// El Cliente empresarial ya está creado (<see cref="_clienteId"/> no es
    /// null) pero la vinculación con la Empresa falló y sigue sin resolverse
    /// — ver <see cref="CrearClienteNuevoAsync"/> y
    /// <see cref="ReintentarVinculacionClienteAsync"/>. Nunca se vuelve a
    /// invitar a crear el Cliente: eso chocaría contra el registro ya persistido.
    /// </summary>
    private bool _vinculacionEmpresaClientePendiente;

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

    private Guid? _ultimoTrabajadorId;
    private string _ultimoTrabajadorNombre = string.Empty;
    private int _trabajadoresCreados;

    /// <summary>
    /// Si la asignación del último Trabajador creado al Centro sigue sin
    /// resolverse — ver <see cref="AsignarTrabajadorRecienCreadoAsync"/>. El
    /// resumen del paso (AltaGuiada.razor) lo lee para no anunciar "asignado
    /// al centro" cuando esa segunda escritura todavía no ocurrió.
    /// </summary>
    private bool _ultimoTrabajadorAsignacionPendiente;

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

        // Cada identificador de la URL se resuelve contra su propia Query
        // antes de afirmar nada de él — una coordenada de contexto no es
        // autoridad. La Query ya aplica el alcance de quien mira (tenant y
        // cartera) y devuelve null por igual si no existe o si es ajeno, así
        // que aquí no hay nada más que comprobar: si no resuelve, el paso
        // arranca como si no hubiera nada preseleccionado.
        if (EmpresaIdEntrada is { } empresaId)
        {
            var empresa = await Mediator.Send(new ObtenerEmpresaPorIdQuery(empresaId));
            if (empresa is not null)
            {
                _empresaId = empresa.Id;
                _empresaNombre = empresa.RazonSocial;
                _pasosCompletados.Add("empresa");
            }
        }

        if (ClienteIdEntrada is { } clienteId)
        {
            var cliente = await Mediator.Send(new ObtenerClientePorIdQuery(clienteId));
            if (cliente is not null)
            {
                _clienteId = cliente.Id;
                _clienteNombre = cliente.RazonSocial;
                _pasosCompletados.Add("cliente");
            }
        }

        if (CentroIdEntrada is { } centroId)
        {
            var centro = await Mediator.Send(new ObtenerCentroPorIdQuery(centroId));
            if (centro is not null)
            {
                _ultimoCentroId = centro.Id;
                _ultimoCentroNombre = centro.Nombre;
                _centrosCreados = 1;
                _pasosCompletados.Add("centro");
            }
        }

        // El paso de partida es el primero, en orden canónico, que no llegó
        // ya resuelto por query string — no siempre "empresa": quien entra
        // con clienteId ya fijado retoma en "empresa" solo si tampoco trae
        // empresaId, y quien trae los tres retoma directamente en
        // "trabajadores".
        _pasoActual = Pasos.Select(p => p.Clave).FirstOrDefault(p => !_pasosCompletados.Contains(p)) ?? "trabajadores";
    }

    private static readonly IReadOnlyList<BreadcrumbElemento> MigueroEstatico =
        [new BreadcrumbElemento("Clientes empresariales"), new BreadcrumbElemento("Alta guiada")];

    private IReadOnlyList<BreadcrumbElemento> Miguero => MigueroEstatico;

    private void IrABreadcrumb(int indice)
    {
        if (indice == 0)
            NavigationManager.NavigateTo("/clientes");
    }

    private async Task GuardarEmpresaAsync()
    {
        // Guarda de reentrada del método, no del botón: Boton conserva su
        // @onclick enganchado aunque esté disabled, y un segundo clic ya
        // viajaba cuando el primero puso "Cargando" — mismo patrón que
        // AcordeonAsignacionesCentro.ConfirmarBajaLoteAsync.
        if (_guardandoEmpresa) return;

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
        // Guarda de reentrada del método, no del botón — ver GuardarEmpresaAsync.
        if (_guardandoCliente) return;

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
            _mensajeErrorCliente = "Selecciona un Cliente empresarial.";
            return;
        }

        await VincularEmpresaAClienteAsync(clienteId);
        if (_mensajeErrorCliente is not null) return;

        _clienteId = clienteId;
        _clienteNombre = _clientesCatalogo.First(c => c.Id == clienteId).RazonSocial;
        _pasosCompletados.Add("cliente");
        _pasoActual = "centro";
        ToastService.Mostrar("Empresa vinculada al Cliente empresarial.", TonoToast.Exito);
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

        // El Cliente empresarial YA está persistido a partir de aquí: son dos
        // escrituras (CrearClienteCommand y, a continuación,
        // EditarEmpresaCommand para la vinculación), no una transacción — si
        // la segunda falla, el registro del Cliente sigue existiendo y no hay
        // vuelta atrás honesta. Por eso _clienteId se fija YA: un reintento de
        // la vinculación no puede volver a crear el Cliente, y este panel deja
        // de invitar a repetir el alta (ver el bloque "_clienteId is null" de
        // AltaGuiada.razor).
        _clienteId = clienteId;
        _clienteNombre = _razonSocialCliente;
        _pasosCompletados.Add("cliente");

        await VincularEmpresaAClienteAsync(clienteId);

        if (_mensajeErrorCliente is not null)
        {
            _vinculacionEmpresaClientePendiente = true;
            var mensajeVinculacion = _mensajeErrorCliente;
            _mensajeErrorCliente = null;
            ToastService.Mostrar(
                $"Cliente empresarial «{_clienteNombre}» creado, pero no se pudo vincular con la Empresa: {mensajeVinculacion}",
                TonoToast.Advertencia, "Reintentar vinculación", () => ReintentarVinculacionClienteAsync(clienteId));
            return;
        }

        _pasoActual = "centro";
        ToastService.Mostrar("Cliente empresarial creado correctamente.", TonoToast.Exito);
    }

    /// <summary>
    /// Reintento manual de la vinculación tras un fallo parcial: el Cliente
    /// empresarial ya existe (se creó en <see cref="CrearClienteNuevoAsync"/>),
    /// esto solo repite <see cref="VincularEmpresaAClienteAsync"/> — nunca
    /// vuelve a crear el Cliente. Se llama desde el botón "Reintentar
    /// vinculación" del resumen y desde la acción del propio toast de aviso.
    /// </summary>
    private async Task ReintentarVinculacionClienteAsync(Guid clienteId)
    {
        await VincularEmpresaAClienteAsync(clienteId);

        if (_mensajeErrorCliente is not null)
        {
            var mensajeVinculacion = _mensajeErrorCliente;
            _mensajeErrorCliente = null;
            ToastService.Mostrar(
                $"Sigue sin poder vincularse: {mensajeVinculacion}",
                TonoToast.Advertencia, "Reintentar vinculación", () => ReintentarVinculacionClienteAsync(clienteId));
            StateHasChanged();
            return;
        }

        _vinculacionEmpresaClientePendiente = false;
        ToastService.Mostrar("Empresa vinculada al Cliente empresarial.", TonoToast.Exito);
        StateHasChanged();
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
        // Guarda de reentrada del método, no del botón — ver GuardarEmpresaAsync.
        if (_guardandoCentro) return;

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
        // Guarda de reentrada del método, no del botón — ver GuardarEmpresaAsync.
        if (_guardandoTrabajador) return;

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
            var trabajadorNombre = $"{_nombreTrabajador} {_apellidosTrabajador}";

            // El Trabajador YA está persistido a partir de aquí: la
            // asignación al centro (segundo Command, CrearAsignacionCommand)
            // es una escritura aparte que puede fallar por su cuenta. Se
            // cuenta como creado, se limpia el formulario para el siguiente
            // (evita chocar contra este mismo DNI si alguien reintenta) y
            // cualquier fallo de la asignación se avisa y se ofrece
            // reintentar sobre ESTE trabajador — nunca crea uno nuevo.
            _pasosCompletados.Add("trabajadores");
            _ultimoTrabajadorId = trabajadorId;
            _ultimoTrabajadorNombre = trabajadorNombre;
            _trabajadoresCreados++;

            _nombreTrabajador = string.Empty;
            _apellidosTrabajador = string.Empty;
            _dniTrabajador = string.Empty;
            _emailTrabajador = string.Empty;
            _puestoTrabajador = string.Empty;

            if (_ultimoCentroId is not { } centroId)
            {
                ToastService.Mostrar($"{trabajadorNombre} creado correctamente.", TonoToast.Exito);
                return;
            }

            _ultimoTrabajadorAsignacionPendiente = true;
            await AsignarTrabajadorRecienCreadoAsync(trabajadorId, trabajadorNombre, centroId);
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

    /// <summary>
    /// Asigna al Trabajador recién creado (ya persistido, ver
    /// <see cref="GuardarTrabajadorAsync"/>) al centro de este asistente. Un
    /// fallo aquí —de red, de autorización, lo que sea— no deshace ni oculta
    /// la creación: se avisa con el nombre del Trabajador y se ofrece
    /// reintentar la asignación sola, sobre el mismo <paramref name="trabajadorId"/>.
    /// </summary>
    private async Task AsignarTrabajadorRecienCreadoAsync(Guid trabajadorId, string trabajadorNombre, Guid centroId)
    {
        try
        {
            var resultadoAsignacion = await Mediator.Send(
                new CrearAsignacionCommand(trabajadorId, centroId, DateOnly.FromDateTime(DateTime.Today)));

            if (resultadoAsignacion.EsFallido)
            {
                ToastService.Mostrar(
                    $"{trabajadorNombre} se creó, pero no se pudo asignar a {_ultimoCentroNombre}: {resultadoAsignacion.Error.Mensaje}",
                    TonoToast.Advertencia, "Reintentar asignación",
                    () => AsignarTrabajadorRecienCreadoAsync(trabajadorId, trabajadorNombre, centroId));
                return;
            }

            _ultimoTrabajadorAsignacionPendiente = false;
            ToastService.Mostrar($"{trabajadorNombre} creado y asignado correctamente.", TonoToast.Exito);
        }
        catch (Exception)
        {
            ToastService.Mostrar(
                $"{trabajadorNombre} se creó, pero no pudimos asignarlo. Intenta nuevamente en unos segundos.",
                TonoToast.Advertencia, "Reintentar asignación",
                () => AsignarTrabajadorRecienCreadoAsync(trabajadorId, trabajadorNombre, centroId));
        }
        finally
        {
            // Necesario para el reintento: se llama desde la acción de un
            // toast, fuera del ciclo normal de eventos de este componente, y
            // sin esto el resumen del paso no reflejaría el nuevo estado.
            StateHasChanged();
        }
    }

    private string SufijoAsignacionUltimoTrabajador()
    {
        if (_ultimoCentroId is null) return "";
        return _ultimoTrabajadorAsignacionPendiente ? "" : " y asignado al centro";
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
