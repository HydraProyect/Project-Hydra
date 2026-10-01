using CaeManager.Application.Tenants.Commands.GuardarLogoTenant;
using CaeManager.Application.Tenants.Commands.RetirarLogoTenant;
using CaeManager.Application.Tenants.Queries.ObtenerLogoOrganizacion;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Configuracion.Recursos;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.Localization;

namespace CaeManager.Web.Features.Configuracion.Pages;

/// <summary>
/// Configuración → Organización: subir, sustituir y retirar el logo de la organización (selector de
/// Tenant beneficiario, lote 1). Lo abren el Administrador de la organización y Soporte TALVEG dentro de
/// una Sesión Privilegiada. Quién puede escribir no se decide aquí: lo dice
/// <see cref="LogoOrganizacionDto.PuedeEditar"/> y lo impone cada comando en Application.
/// </summary>
public partial class OrganizacionLogo
{
    /// <summary>Mismo máximo que <c>GuardarLogoTenantCommandValidator</c>, para cortar la lectura antes.</summary>
    private const int TamanoMaximoBytes = GuardarLogoTenantCommandValidator.TamanoMaximoBytes;

    [Inject] private IMediator Mediator { get; set; } = default!;
    [Inject] private ToastService ToastService { get; set; } = default!;
    [Inject] private IStringLocalizer<TextosConfiguracion> Textos { get; set; } = default!;

    private LogoOrganizacionDto? _organizacion;
    private bool _errorCarga;
    private bool _guardando;
    private bool _confirmandoRetirada;
    private string? _mensajeError;

    private bool TieneLogo => !string.IsNullOrEmpty(_organizacion?.LogoVersion);

    private string? UrlLogo => _organizacion is null
        ? null
        : AvatarTenant.UrlDeLogo(_organizacion.TenantId, _organizacion.LogoVersion);

    /// <summary>Por qué no se puede editar: Soporte sin aprovisionamiento, o alguien que no es Administrador.</summary>
    private string TextoSoloLectura => _organizacion?.EnSesionDeSoporte == true
        ? Textos["OrganizacionSoloLecturaSoporte"]
        : Textos["OrganizacionSoloLecturaAdministrador"];

    protected override Task OnInitializedAsync() => CargarAsync();

    private async Task CargarAsync()
    {
        _errorCarga = false;
        try
        {
            _organizacion = await Mediator.Send(new ObtenerLogoOrganizacionQuery());
            _errorCarga = _organizacion is null;
        }
        catch (Exception)
        {
            _errorCarga = true;
        }
    }

    private async Task SubirArchivoAsync(InputFileChangeEventArgs args)
    {
        if (_guardando) return;
        _mensajeError = null;
        _guardando = true;
        try
        {
            byte[] bytes;
            try
            {
                await using var flujo = args.File.OpenReadStream(TamanoMaximoBytes);
                using var memoria = new MemoryStream();
                await flujo.CopyToAsync(memoria);
                bytes = memoria.ToArray();
            }
            catch (IOException)
            {
                // OpenReadStream lanza IOException al superar el máximo.
                _mensajeError = Textos["OrganizacionErrorTamano"];
                return;
            }

            var resultado = await Mediator.Send(new GuardarLogoTenantCommand(bytes));
            if (resultado.EsFallido)
            {
                _mensajeError = resultado.Error.Mensaje;
                return;
            }

            ToastService.Mostrar(Textos["OrganizacionToastGuardado"], TonoToast.Exito);
            await CargarAsync();
        }
        catch (Exception)
        {
            _mensajeError = Textos["OrganizacionErrorGuardar"];
        }
        finally
        {
            _guardando = false;
        }
    }

    private void PedirRetirar() => _confirmandoRetirada = true;

    private void CerrarConfirmacion(bool visible) => _confirmandoRetirada = visible;

    private async Task RetirarAsync()
    {
        if (_guardando) return;
        _mensajeError = null;
        _guardando = true;
        try
        {
            var resultado = await Mediator.Send(new RetirarLogoTenantCommand());
            _confirmandoRetirada = false;
            if (resultado.EsFallido)
            {
                _mensajeError = resultado.Error.Mensaje;
                return;
            }

            ToastService.Mostrar(Textos["OrganizacionToastRetirado"], TonoToast.Exito);
            await CargarAsync();
        }
        catch (Exception)
        {
            _confirmandoRetirada = false;
            _mensajeError = Textos["OrganizacionErrorGuardar"];
        }
        finally
        {
            _guardando = false;
        }
    }
}
