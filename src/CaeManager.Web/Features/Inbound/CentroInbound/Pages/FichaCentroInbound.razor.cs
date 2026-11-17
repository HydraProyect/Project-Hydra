using CaeManager.Application.Centros.Queries.ObtenerCentroPorId;
using CaeManager.Application.Inbound.ContratosAcceso.Queries.ObtenerContratosAcceso;
using CaeManager.Domain.Common;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Inbound.ContratosAcceso.Servicios;
using CaeManager.Web.Features.Inbound.InformacionDelCentro;
using MediatR;
using Microsoft.AspNetCore.Components;

namespace CaeManager.Web.Features.Inbound.CentroInbound.Pages;

/// <summary>
/// Centro de Inbound (tarjeta 14), vista del Gestor CAE del Tenant TCE. Lee la ficha con <see cref="ObtenerCentroPorIdQuery"/> (que
/// devuelve null si el Centro no es visible) y los contratos de acceso del Centro con <see cref="ObtenerContratosAccesoQuery"/>; Aptos
/// hoy (<c>ObtenerAptosHoyDelCentroQuery</c>) e Información del Centro van por sus puertos. La autoridad es la de las Queries; la pantalla no escribe nada.
/// </summary>
public partial class FichaCentroInbound
{
    private const string PestanaAptos = "Aptos";
    private const string PestanaRequisitos = "Requisitos";
    private const string PestanaEmpresas = "Empresas";
    private const string PestanaInformacion = "Informacion";
    private const string SinDato = "—";
    private const string SinDatoTitulo = "Todavía no disponible";

    [Parameter] public Guid CentroId { get; set; }

    [Inject] private IMediator Mediator { get; set; } = default!;
    [Inject] private ResolutorContratosAcceso Resolutor { get; set; } = default!;
    [Inject] private IAptosHoyDelCentro AptosHoy { get; set; } = default!;
    [Inject] private IInformacionDelCentro InformacionCentro { get; set; } = default!;
    [Inject] private NavigationManager Navegacion { get; set; } = default!;

    private CentroDetalleDto? _centro;
    private IReadOnlyList<ContratoAccesoFila> _contratos = [];
    private IReadOnlyList<TrabajadorAptoHoy>? _aptos;
    private IReadOnlyList<DocumentoInformacionCentro>? _informacion;
    private IReadOnlyList<PestanaDefinicion> _pestanas = [];
    private IReadOnlyList<OpcionFranjaEstado> _opcionesAptitud = [];
    private string _pestanaActiva = PestanaAptos;
    private DateOnly _hoy = DiaDeNegocio.Hoy();
    private bool _cargando = true;
    private bool _noEncontrado;
    private bool _errorCarga;
    private Guid _idCargado;
    private bool _hayCarga;

    private string _busquedaAptos = string.Empty;
    private string _empresaAptos = string.Empty;
    private string? _estadoAptos;

    private IReadOnlyList<BreadcrumbElemento> Miguero =>
        [new BreadcrumbElemento("Centros"), new BreadcrumbElemento(_centro?.Nombre ?? "…")];

    protected override async Task OnParametersSetAsync()
    {
        if (_hayCarga && _idCargado == CentroId)
            return;
        _hayCarga = true;
        _idCargado = CentroId;
        await CargarAsync();
    }

    private async Task CargarAsync()
    {
        _cargando = true;
        _errorCarga = false;
        _noEncontrado = false;
        try
        {
            _hoy = DiaDeNegocio.Hoy();
            _centro = await Mediator.Send(new ObtenerCentroPorIdQuery(CentroId));
            if (_centro is null)
            {
                _noEncontrado = true;
                return;
            }

            var contratos = await Mediator.Send(new ObtenerContratosAccesoQuery(CentroId));
            _contratos = (await Resolutor.ResolverAsync(contratos))
                .OrderBy(f => f.Contrato.Estado is Domain.Inbound.ContratosAcceso.EstadoContratoAcceso.Rechazado
                    or Domain.Inbound.ContratosAcceso.EstadoContratoAcceso.Cancelado ? 1 : 0)
                .ThenBy(f => f.Contrato.FechaFin ?? DateOnly.MaxValue)
                .ThenBy(f => f.EmpresaNombre, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            _aptos = await AptosHoy.ObtenerAsync(CentroId);
            _informacion = await InformacionCentro.ListarAsync(CentroId);
            _opcionesAptitud =
            [
                new OpcionFranjaEstado("Aptos", TonoBadge.Exito, nameof(EstadoAptitudHoy.Apto)),
                new OpcionFranjaEstado("En tolerancia", TonoBadge.Advertencia, nameof(EstadoAptitudHoy.EnTolerancia)),
                new OpcionFranjaEstado("No aptos", TonoBadge.Peligro, nameof(EstadoAptitudHoy.NoApto)),
            ];
            _pestanas =
            [
                new PestanaDefinicion(PestanaAptos, "Aptos hoy")
                {
                    Contador = _aptos is null ? null : new ContadorPestana(
                        _aptos.Count(a => a.Estado == EstadoAptitudHoy.NoApto), "no aptos", EnAlerta: _aptos.Any(a => a.Estado == EstadoAptitudHoy.NoApto)),
                },
                new PestanaDefinicion(PestanaRequisitos, "Requisitos del Centro"),
                new PestanaDefinicion(PestanaEmpresas, "Empresas contratistas") { Contador = new ContadorPestana(_contratos.Count, "contratos") },
                new PestanaDefinicion(PestanaInformacion, "Información del Centro")
                {
                    Contador = _informacion is null ? null : new ContadorPestana(_informacion.Count, "documentos"),
                },
            ];
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

    private void CambiarPestana(string id) => _pestanaActiva = id;

    private void IrABreadcrumb(int indice)
    {
        if (indice == 0)
            Navegacion.NavigateTo("/inbound/centros");
    }

    // ---------------------------------------------------------------- Aptos hoy

    private IEnumerable<TrabajadorAptoHoy> SinEstado =>
        (_aptos ?? [])
        .Where(a => string.IsNullOrWhiteSpace(_busquedaAptos)
            || a.Nombre.Contains(_busquedaAptos.Trim(), StringComparison.CurrentCultureIgnoreCase)
            || a.DniParcial.Contains(_busquedaAptos.Trim(), StringComparison.CurrentCultureIgnoreCase))
        .Where(a => _empresaAptos.Length == 0 || a.EmpresaContratista == _empresaAptos);

    // Primero lo que no deja entrar, después lo que vence y al final los aptos (maqueta).
    private IReadOnlyList<TrabajadorAptoHoy> AptosVisibles =>
        SinEstado
            .Where(a => string.IsNullOrEmpty(_estadoAptos) || SeleccionEstados.Separar(_estadoAptos).Contains(a.Estado.ToString()))
            .OrderByDescending(a => a.Estado)
            .ThenBy(a => a.EmpresaContratista, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(a => a.Nombre, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    private IReadOnlyDictionary<string, int> RecuentosAptos =>
        SinEstado.GroupBy(a => a.Estado.ToString()).ToDictionary(g => g.Key, g => g.Count());

    private IReadOnlyList<OpcionEstado> OpcionesEmpresaAptos =>
        (_aptos ?? []).Select(a => a.EmpresaContratista).Distinct().OrderBy(e => e, StringComparer.CurrentCultureIgnoreCase)
            .Select(e => new OpcionEstado(e, e)).ToList();

    private Task BuscarAptos(string texto)
    {
        _busquedaAptos = texto ?? string.Empty;
        return Task.CompletedTask;
    }

    private void CambiarEmpresaAptos(string valor) => _empresaAptos = valor;

    private void CambiarEstadoAptos(string? valor) => _estadoAptos = valor;

    private static TonoBadge TonoDe(EstadoAptitudHoy estado) => estado switch
    {
        EstadoAptitudHoy.Apto => TonoBadge.Exito,
        EstadoAptitudHoy.EnTolerancia => TonoBadge.Advertencia,
        _ => TonoBadge.Peligro,
    };

    private static string TextoDe(EstadoAptitudHoy estado) => estado switch
    {
        EstadoAptitudHoy.Apto => "Apto",
        EstadoAptitudHoy.EnTolerancia => "Vencido · en tolerancia",
        _ => "No apto",
    };
}
