using System.Globalization;
using CaeManager.Application.ApiKeys.Commands.RevocarClaveApiDeMiOrganizacion;
using CaeManager.Application.ApiKeys.Queries.ObtenerClavesApi;
using CaeManager.Application.ApiKeys.Queries.ObtenerClavesApiDeMiOrganizacion;
using CaeManager.Domain.Common;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.ApiKeys.Recursos;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;

namespace CaeManager.Web.Features.ApiKeys.Pages;

/// <summary>
/// Las claves de la API pública de la propia organización, para que su Administrador las vea y las
/// revoque. La autoridad vive en Application (<c>AutoridadClavesApiDelTenant</c>); el atributo de la
/// página solo evita mostrarla a quien nunca podría usarla. Emitir sigue siendo de la Plataforma
/// TALVEG (<see cref="ClavesApi"/>).
/// </summary>
public partial class ClavesApiOrganizacion : CaeManager.Web.Components.PaginaIntegrableConfiguracionBase, IDisposable
{
    [Inject] private IMediator Mediator { get; set; } = default!;
    [Inject] private ToastService ToastService { get; set; } = default!;
    [Inject] private ILogger<ClavesApiOrganizacion> Logger { get; set; } = default!;
    [Inject] private IStringLocalizer<TextosClavesApi> Textos { get; set; } = default!;
    private readonly CancellationTokenSource _ciclo = new();
    private bool _desechado;
    private IReadOnlyList<ClaveApiDto> _claves = [];
    private bool _cargando = true;
    private ClaveApiDto? _claveARevocar;
    private bool _revocando;

    private string MensajeRevocacion => _claveARevocar is null ? string.Empty : Textos["RevocarConfirmacion", _claveARevocar.NombreDescriptivo].Value;

    protected override Task OnInitializedAsync() => CargarAsync();

    private async Task CargarAsync()
    {
        try { _claves = await Mediator.Send(new ObtenerClavesApiDeMiOrganizacionQuery(), _ciclo.Token); }
        catch (OperationCanceledException) when (_ciclo.IsCancellationRequested) { }
        catch (Exception ex) { Logger.LogError(ex, "Error al cargar las claves API de la organización."); ToastService.Mostrar(Textos["ErrorCarga"].Value, TonoToast.Error); }
        finally { if (!_desechado) _cargando = false; }
    }

    // Mismo corte que ClaveApi.EstaVigente, más el respaldo que el listado ya evaluó.
    private static bool EstaVigente(ClaveApiDto clave) => clave.EstaActiva && !clave.Cortada && DateTime.UtcNow < clave.CaducaEnUtc;
    private string TextoEstado(ClaveApiDto clave) =>
        !clave.EstaActiva ? Textos["Revocada"].Value : clave.Cortada ? Textos["Cortada"].Value : EstaVigente(clave) ? Textos["Activa"].Value : Textos["Caducada"].Value;

    private static string FechaCreacion(ClaveApiDto clave) => clave.CreadaEnUtc.EnHoraPeninsular().ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
    private static string FechaCaducidad(ClaveApiDto clave) => DiaDeNegocio.De(clave.CaducaEnUtc.AddTicks(-1)).ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
    private string FechaUltimoUso(ClaveApiDto clave) => clave.UltimoUsoUtc?.EnHoraPeninsular().ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture) ?? Textos["Nunca"].Value;

    private void CerrarRevocacion(bool visible) { if (!visible && !_revocando) _claveARevocar = null; }

    private async Task RevocarAsync()
    {
        if (_revocando || _claveARevocar is not { } clave) return;
        _revocando = true;
        try
        {
            var resultado = await Mediator.Send(new RevocarClaveApiDeMiOrganizacionCommand(clave.Id));
            if (_desechado) return;
            if (resultado.EsFallido) { ToastService.Mostrar(resultado.Error.Mensaje, TonoToast.Error); return; }
            ToastService.Mostrar(Textos["RevocadaAviso"].Value, TonoToast.Exito);
            _claveARevocar = null;
            await CargarAsync();
        }
        catch (Exception ex) { if (!_desechado) { Logger.LogError(ex, "Error al revocar la clave API {ClaveApiId}.", clave.Id); ToastService.Mostrar(Textos["ErrorRevocar"].Value, TonoToast.Error); } }
        finally { if (!_desechado) _revocando = false; }
    }

    public void Dispose() { _desechado = true; _claveARevocar = null; _ciclo.Cancel(); _ciclo.Dispose(); }
}
