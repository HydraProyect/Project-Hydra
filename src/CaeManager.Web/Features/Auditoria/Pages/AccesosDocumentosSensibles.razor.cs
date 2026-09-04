using CaeManager.Application.Auditoria.Queries;
using CaeManager.Infrastructure.Identity;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Identity;

namespace CaeManager.Web.Features.Auditoria.Pages;

/// <summary>
/// Consulta mínima de <c>RegistroAccesoDocumentoSensible</c> (HO-099-01 § 8):
/// demuestra que el permiso funciona, sin filtros ni exportación — eso es
/// otro incremento si hace falta. Gateada en <c>AccesosDocumentosSensibles.razor</c>
/// por <c>Policies.ConsultarAccesoDocumentosSensibles</c> (RequireRole
/// Administrador + RequireClaim del permiso específico de DEC-36).
/// </summary>
public partial class AccesosDocumentosSensibles : CaeManager.Web.Components.PaginaIntegrableConfiguracionBase
{
    [Inject] private IMediator Mediator { get; set; } = default!;
    [Inject] private UserManager<ApplicationUser> UserManager { get; set; } = default!;

    private IReadOnlyList<AccesoDocumentoSensibleDto> _accesos = [];
    private readonly Dictionary<Guid, string> _nombresPorUsuarioId = [];
    private bool _cargando = true;
    private bool _errorCarga;
    private int _pagina = 1;
    private int _totalPaginas = 1;
    private int _totalElementos;
    private int _tamanoPagina = 30;

    protected override Task OnInitializedAsync() => CargarAsync();

    private Task IrAPaginaAsync(int pagina)
    {
        _pagina = pagina;
        return CargarAsync();
    }

    private Task CambiarTamanoPaginaAsync(int tamano)
    {
        _tamanoPagina = tamano;
        _pagina = 1;
        return CargarAsync();
    }

    private async Task CargarAsync()
    {
        _cargando = true;
        _errorCarga = false;
        StateHasChanged();

        try
        {
            var resultado = await Mediator.Send(new ObtenerAccesosDocumentosSensiblesQuery(_pagina, _tamanoPagina));
            _accesos = resultado.Elementos;
            _totalPaginas = resultado.TotalPaginas;
            _totalElementos = resultado.TotalElementos;

            foreach (var acceso in _accesos)
            {
                if (acceso.UsuarioId is not { } usuarioId || _nombresPorUsuarioId.ContainsKey(usuarioId))
                    continue;

                var usuario = await UserManager.FindByIdAsync(usuarioId.ToString());
                _nombresPorUsuarioId[usuarioId] = usuario?.NombreCompleto ?? usuario?.Email ?? "(usuario eliminado)";
            }
        }
        catch (Exception)
        {
            _errorCarga = true;
        }
        finally
        {
            _cargando = false;
        }
    }

    private string NombreDe(Guid? usuarioId) =>
        usuarioId is { } id && _nombresPorUsuarioId.TryGetValue(id, out var nombre) ? nombre : "Sistema";
}
