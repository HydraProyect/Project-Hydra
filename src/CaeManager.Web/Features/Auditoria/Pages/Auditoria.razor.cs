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
    /// de la propia página (volver atrás, compartir la URL) — la recarga de
    /// datos la sigue disparando explícitamente cada manejador de filtro, no
    /// este método, para no depender del timing del router (P1-18 de
    /// docs/business/MATURITY_REVIEW.md).
    /// </summary>
    protected override void OnParametersSet()
    {
        _filtroEntidadTipo = string.IsNullOrWhiteSpace(EntidadTipoInicial) ? null : EntidadTipoInicial;
    }

    private async Task CargarAsync()
    {
        _cargando = true;
        _error = false;
        StateHasChanged();

        try
        {
            _resultado = await Mediator.Send(new ObtenerAuditoriaQuery(_filtroEntidadTipo, UsuarioId: null, _pagina, TamanoPagina));

            var idsFaltantes = _resultado.Elementos
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
                    _usuariosPorId[id] = usuario?.NombreCompleto ?? usuario?.Email ?? "(usuario eliminado)";
                }
            });
        }
        catch (Exception)
        {
            _error = true;
        }
        finally
        {
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

    private Task IrAPaginaAsync(int pagina)
    {
        _pagina = pagina;
        return CargarAsync();
    }

    private string EnlaceExportar =>
        _filtroEntidadTipo is null ? "/auditoria/exportar.xlsx" : $"/auditoria/exportar.xlsx?entidad={Uri.EscapeDataString(_filtroEntidadTipo)}";

    private string NombreUsuario(Guid? usuarioId) =>
        usuarioId is null ? "Sistema" : _usuariosPorId.GetValueOrDefault(usuarioId.Value, "—");

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
