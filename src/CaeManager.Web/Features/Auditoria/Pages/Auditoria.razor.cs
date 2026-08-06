using System.Text.Json;
using CaeManager.Application.Auditoria.Queries;
using CaeManager.Application.Centros.Commands.RestaurarCentro;
using CaeManager.Application.Clientes.Commands.RestaurarCliente;
using CaeManager.Application.Common;
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

public partial class Auditoria : ComponentBase
{
    // Catálogo fijo de agregados de dominio auditables (ver
    // AuditoriaInterceptor: EntidadTipo es el nombre simple de la clase).
    private static readonly string[] TiposEntidad =
        ["Cliente", "Empresa", "Centro", "Trabajador", "TipoDocumento", "Documento", "Asignacion", "ParametroSistema"];

    // Solo estas 5 tienen Restaurar*Command (patrón "Deshacer", ver
    // UX_PATTERNS.md § Eliminar) — son las únicas para las que la Auditoría
    // puede ofrecer una restauración real (H1, docs/ux-audit/14-administracion.md).
    private static readonly HashSet<string> EntidadesRestaurables =
        ["Cliente", "Empresa", "Centro", "Trabajador", "Documento"];

    [Inject] private IMediator Mediator { get; set; } = default!;
    [Inject] private UserManager<ApplicationUser> UserManager { get; set; } = default!;
    [Inject] private PuertaAccesoDatos PuertaAccesoDatos { get; set; } = default!;
    [Inject] private NavigationManager NavigationManager { get; set; } = default!;
    [Inject] private ToastService ToastService { get; set; } = default!;

    [SupplyParameterFromQuery(Name = "entidad")]
    public string? EntidadTipoInicial { get; set; }

    private ResultadoPaginado<RegistroAuditoriaDto>? _resultado;
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

    private string NombreUsuario(Guid? usuarioId) =>
        usuarioId is null ? "Sistema" : _usuariosPorId.GetValueOrDefault(usuarioId.Value, "—");

    /// <summary>
    /// H1 (docs/ux-audit/14-administracion.md): esto es lo que hace real la
    /// promesa "Podrás recuperarlas desde Auditoría" del borrado en lote de
    /// Cliente/Empresa/Centro/Trabajador/Documento. El borrado es lógico
    /// (<c>MarcarComoEliminado()</c> solo cambia un flag), así que
    /// <c>AuditoriaInterceptor</c> lo registra como "Modificado" — "Eliminado"
    /// en <c>Accion</c> solo existe para un borrado físico que este dominio no
    /// hace — por eso hay que mirar el JSON de <c>DatosDespues</c>, igual que
    /// ya hace <see cref="TieneArchivoAnterior"/> con <c>DatosAntes</c>.
    /// </summary>
    private bool PuedeRestaurar(RegistroAuditoriaDto registro)
    {
        if (!EntidadesRestaurables.Contains(registro.EntidadTipo) || registro.Accion != "Modificado" || registro.DatosDespues is null)
            return false;

        try
        {
            using var datosDespues = JsonDocument.Parse(registro.DatosDespues);
            return datosDespues.RootElement.TryGetProperty("EstaEliminado", out var valor) && valor.ValueKind == JsonValueKind.True;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private bool EstaRestaurando(RegistroAuditoriaDto registro) => _restaurando.Contains(registro.Id);

    private async Task RestaurarAsync(RegistroAuditoriaDto registro)
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

    /// <summary>
    /// El interceptor de auditoría ya guarda el ArchivoUrl anterior en el
    /// JSON de DatosAntes de cada Modificado de Documento — esto solo
    /// comprueba si hay uno para decidir si mostrar el enlace.
    /// </summary>
    private static bool TieneArchivoAnterior(RegistroAuditoriaDto registro)
    {
        if (registro.EntidadTipo != "Documento" || registro.Accion != "Modificado" || registro.DatosAntes is null)
            return false;

        try
        {
            using var datosAntes = JsonDocument.Parse(registro.DatosAntes);
            return datosAntes.RootElement.TryGetProperty("ArchivoUrl", out var valor) && valor.ValueKind == JsonValueKind.String;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
