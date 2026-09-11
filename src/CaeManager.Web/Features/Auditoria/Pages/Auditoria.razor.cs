using CaeManager.Application.Common;
using CaeManager.Application.Auditoria.Queries;
using CaeManager.Application.Centros.Commands.RestaurarCentro;
using CaeManager.Application.Clientes.Commands.RestaurarCliente;
using CaeManager.Application.Documentos.Commands.RestaurarDocumento;
using CaeManager.Application.Empresas.Commands.RestaurarEmpresa;
using CaeManager.Application.Trabajadores.Commands.RestaurarTrabajador;
using CaeManager.Domain.Common;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components;
using CaeManager.Web.Components.DesignSystem;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Identity;

namespace CaeManager.Web.Features.Auditoria.Pages;

public partial class Auditoria : CaeManager.Web.Components.PaginaIntegrableConfiguracionBase
{
    // Catálogo fijo de agregados de dominio auditables (ver
    // AuditoriaInterceptor: EntidadTipo es el nombre simple de la clase).
    private static readonly string[] TiposEntidad =
        ["Cliente", "Empresa", "Centro", "Trabajador", "TipoDocumento", "Documento", "Asignacion", "ParametroSistema"];

    [Inject] private IMediator Mediator { get; set; } = default!;
    [Inject] private UserManager<ApplicationUser> UserManager { get; set; } = default!;
    [Inject] private PuertaAccesoDatos PuertaAccesoDatos { get; set; } = default!;
    [Inject] private NavigationManager NavigationManager { get; set; } = default!;
    [Inject] private ToastService ToastService { get; set; } = default!;

    [SupplyParameterFromQuery(Name = "entidad")]
    public string? EntidadTipoInicial { get; set; }

    private ResultadoPaginado<RegistroAuditoriaListaDto>? _resultado;
    private Dictionary<Guid, string> _usuariosPorId = new();
    private bool _cargando = true;
    private bool _error;
    private string? _filtroEntidadTipo;
    private int _pagina = 1;
    private const int TamanoPagina = 30;
    private readonly HashSet<Guid> _restaurando = [];

    /// <summary>Identifica la carga vigente; ver <see cref="CargarAsync"/>.</summary>
    private int _versionCarga;

    /// <summary>
    /// Identity no encuentra el Id. AspNetUsers no tiene RLS ni filtro de
    /// tenant, así que no es un usuario de otro tenant oculto: no existe.
    /// </summary>
    private const string UsuarioNoEncontrado = "(usuario eliminado)";

    protected override Task OnInitializedAsync()
    {
        // Los [Parameter] ya están asignados en este punto (SetParametersAsync
        // corre antes de OnInitialized) — se lee aquí y no solo en
        // OnParametersSet porque en el primer render OnInitializedAsync se
        // ejecuta ANTES que OnParametersSet, y esta carga inicial necesita el
        // filtro ya resuelto.
        _filtroEntidadTipo = string.IsNullOrWhiteSpace(EntidadTipoInicial) ? null : EntidadTipoInicial;
        return CargarAsync();
    }

    /// <summary>
    /// Re-sincroniza el filtro con la URL en navegaciones posteriores dentro
    /// de la propia página (volver atrás o adelante, abrir una URL compartida).
    ///
    /// <para>
    /// Los cambios que inicia la propia página los recarga su manejador, no
    /// este método, para no depender del timing del router (P1-18 de
    /// docs/business/MATURITY_REVIEW.md): cuando llegan aquí, el filtro ya
    /// coincide con la URL y no se hace nada. Solo recarga cuando la URL trae
    /// un filtro DISTINTO del que enseña la página, que es lo que pasa al
    /// volver atrás. Antes solo cambiaba el filtro: el desplegable y el enlace
    /// de exportar pasaban a decir una cosa mientras la tabla seguía
    /// enseñando las filas de otra consulta.
    /// </para>
    /// </summary>
    protected override Task OnParametersSetAsync()
    {
        var filtroDeLaUrl = string.IsNullOrWhiteSpace(EntidadTipoInicial) ? null : EntidadTipoInicial;
        if (filtroDeLaUrl == _filtroEntidadTipo)
            return Task.CompletedTask;

        _filtroEntidadTipo = filtroDeLaUrl;
        _pagina = 1;
        return CargarAsync();
    }

    /// <summary>
    /// Carga la página vigente de la auditoría.
    ///
    /// <para>
    /// Filtro y página se capturan al empezar, y la respuesta se descarta si
    /// al volver del <c>await</c> ya no es la carga vigente: sin esto, cambiar
    /// de filtro mientras la carga anterior sigue en vuelo dejaba que la
    /// respuesta VIEJA, si llegaba la última, pintara sus filas bajo el
    /// desplegable y el enlace de exportar del filtro NUEVO — un rastro de
    /// auditoría que enseña filas de otra consulta es exactamente lo que esta
    /// pantalla no puede permitirse.
    /// </para>
    /// </summary>
    private async Task CargarAsync()
    {
        var version = ++_versionCarga;
        var filtro = _filtroEntidadTipo;
        var pagina = _pagina;

        _cargando = true;
        _error = false;
        StateHasChanged();

        try
        {
            var resultado = await Mediator.Send(new ObtenerAuditoriaQuery(filtro, UsuarioId: null, pagina, TamanoPagina));
            if (version != _versionCarga)
                return;

            var idsFaltantes = resultado.Elementos
                .Where(r => r.UsuarioId is not null && !_usuariosPorId.ContainsKey(r.UsuarioId.Value))
                .Select(r => r.UsuarioId!.Value)
                .Distinct()
                .ToList();

            // Por la puerta: UserManager no pasa por MediatR y esta carga
            // corre en paralelo con los componentes del layout sobre el mismo
            // DbContext scoped (ver PuertaAccesoDatos).
            await PuertaAccesoDatos.EjecutarAsync(async () =>
            {
                foreach (var id in idsFaltantes)
                {
                    var usuario = await UserManager.FindByIdAsync(id.ToString());
                    _usuariosPorId[id] = usuario?.NombreCompleto ?? usuario?.Email ?? UsuarioNoEncontrado;
                }
            });

            // La caché de nombres sí puede quedarse lo resuelto por una carga
            // superada (un nombre por Id no depende del filtro); las filas no.
            if (version != _versionCarga)
                return;

            _resultado = resultado;
        }
        catch (Exception)
        {
            if (version == _versionCarga)
                _error = true;
        }
        finally
        {
            if (version == _versionCarga)
                _cargando = false;
        }
    }

    private Task FiltrarPorEntidadAsync(string? entidadTipo)
    {
        _filtroEntidadTipo = string.IsNullOrWhiteSpace(entidadTipo) ? null : entidadTipo;
        _pagina = 1;
        NavigationManager.ActualizarFiltroEnUrl("entidad", entidadTipo);
        return CargarAsync();
    }

    /// <summary>
    /// Único filtro de la página. Separa "no hay registros" de "ninguno con
    /// este filtro": son situaciones opuestas y la primera, dicha a quien
    /// acaba de filtrar, hace creer que la auditoría no registra nada.
    /// </summary>
    private bool HayFiltrosActivos => !string.IsNullOrWhiteSpace(_filtroEntidadTipo);

    /// <summary>
    /// La condición va en una propiedad y no en la plantilla a propósito: el
    /// trinquete <c>ListasDistinguenVacioPorFiltroTests</c> reconoce la guarda
    /// con <c>[^)]*</c>, que no cruza un paréntesis, así que un
    /// <c>if ((a || b) &amp;&amp; HayFiltrosActivos)</c> le pasa desapercibido y
    /// da falsa alarma. Es la dirección segura de fallo para un trinquete
    /// —avisa de más, nunca de menos— y sale más barato adoptar su idioma que
    /// aflojarlo.
    /// </summary>
    private bool SinRegistros => _resultado is null || _resultado.Elementos.Count == 0;

    /// <summary>
    /// La página ya cargada. Solo se usa en la rama que <see cref="SinRegistros"/>
    /// descarta, donde nunca es null — pero el compilador no puede verlo a
    /// través de una propiedad, y CI compila con <c>-warnaserror</c>.
    /// </summary>
    private ResultadoPaginado<RegistroAuditoriaListaDto> Resultado => _resultado!;

    private Task LimpiarFiltrosAsync() => FiltrarPorEntidadAsync(null);

    private Task IrAPaginaAsync(int pagina)
    {
        _pagina = pagina;
        return CargarAsync();
    }

    private string EnlaceExportar =>
        _filtroEntidadTipo is null ? "/auditoria/exportar.xlsx" : $"/auditoria/exportar.xlsx?entidad={Uri.EscapeDataString(_filtroEntidadTipo)}";

    /// <summary>
    /// El filtro vigente llegó por la URL y no es de las entidades principales
    /// del desplegable: se ofrece como opción propia para que el control no
    /// diga «Todas» sobre una tabla filtrada.
    /// </summary>
    private bool FiltroFueraDelCatalogo =>
        _filtroEntidadTipo is not null && !TiposEntidad.Contains(_filtroEntidadTipo);

    private string NombreUsuario(Guid? usuarioId) =>
        usuarioId is null ? "Sistema" : _usuariosPorId.GetValueOrDefault(usuarioId.Value, "—");

    private string? ClaseUsuario(Guid? usuarioId) =>
        usuarioId is not null && _usuariosPorId.GetValueOrDefault(usuarioId.Value) == UsuarioNoEncontrado
            ? "usuario-no-resuelto"
            : null;

    private bool EstaRestaurando(RegistroAuditoriaListaDto registro) => _restaurando.Contains(registro.Id);

    private async Task RestaurarAsync(RegistroAuditoriaListaDto registro)
    {
        _restaurando.Add(registro.Id);
        StateHasChanged();

        Result resultado = registro.EntidadTipo switch
        {
            "Cliente" => await Mediator.Send(new RestaurarClienteCommand(registro.EntidadId)),
            "Empresa" => await Mediator.Send(new RestaurarEmpresaCommand(registro.EntidadId)),
            "Centro" => await Mediator.Send(new RestaurarCentroCommand(registro.EntidadId)),
            "Trabajador" => await Mediator.Send(new RestaurarTrabajadorCommand(registro.EntidadId)),
            "Documento" => await Mediator.Send(new RestaurarDocumentoCommand(registro.EntidadId)),
            _ => Result.Fallo(Error.Crear("Auditoria.NoRestaurable", "Esta entidad no se puede restaurar."))
        };

        _restaurando.Remove(registro.Id);
        ToastService.Mostrar(
            resultado.EsExitoso ? $"{registro.EntidadTipo} restaurado(a) correctamente." : resultado.Error.Mensaje,
            resultado.EsExitoso ? TonoToast.Exito : TonoToast.Error);

        if (resultado.EsExitoso)
            await CargarAsync();
        else
            StateHasChanged();
    }
}
