using System.Text.Json;
using CaeManager.Application.Auditoria.Queries;
using CaeManager.Application.Common;
using CaeManager.Infrastructure.Identity;
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

    [Inject] private IMediator Mediator { get; set; } = default!;
    [Inject] private UserManager<ApplicationUser> UserManager { get; set; } = default!;

    private ResultadoPaginado<RegistroAuditoriaDto>? _resultado;
    private Dictionary<Guid, string> _usuariosPorId = new();
    private bool _cargando = true;
    private bool _error;
    private string? _filtroEntidadTipo;
    private int _pagina = 1;
    private const int TamanoPagina = 30;

    protected override Task OnInitializedAsync() => CargarAsync();

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

            foreach (var id in idsFaltantes)
            {
                var usuario = await UserManager.FindByIdAsync(id.ToString());
                _usuariosPorId[id] = usuario?.NombreCompleto ?? usuario?.Email ?? "(usuario eliminado)";
            }
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
