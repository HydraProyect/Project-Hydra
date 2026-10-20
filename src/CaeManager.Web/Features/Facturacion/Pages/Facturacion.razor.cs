using System.Globalization;
using CaeManager.Application.Dashboard;
using CaeManager.Application.Dashboard.Queries;
using CaeManager.Application.Dashboard.TarifasOperadorCae;
using CaeManager.Domain.Common;
using CaeManager.Domain.Dashboard;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.CuadroDireccion;
using CaeManager.Web.Features.Facturacion.Recursos;
using FluentValidation;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;

namespace CaeManager.Web.Features.Facturacion.Pages;

/// <summary>
/// «Facturación»: la cuota que el Operador CAE externo cobra a cada Empresa gestionada (Tenant beneficiario) contra su
/// maqueta («Facturacion TALVEG.dc.html», assets/facturacion-operador.js y «Drawer Tarifas TALVEG»). No calcula nada:
/// compone lo que ya miden y valoran <see cref="ObtenerSerieCuadroDireccionQuery"/>,
/// <see cref="ContarExtranjerosCobrablesDelMesQuery"/> y <see cref="ObtenerTarifasOperadorCaeQuery"/>
/// (<see cref="FacturacionOperadorModelo"/>), con las mismas autorizaciones y el mismo alcance que el Cuadro de
/// dirección; las tarifas las fija <see cref="FijarTarifaOperadorCaeCommand"/>, que solo admite al Administrador o la
/// Dirección CAE del Operador. Lo que cada Empresa gestionada cobra a sus Clientes empresariales (TarifaCliente) no se
/// usa de momento: <see cref="FacturacionPorClienteEmpresarial"/> se conserva sin ruta y la pestaña sale
/// «Próximamente».
/// </summary>
public partial class Facturacion : CaeManager.Web.Components.PaginaInteractiva
{
    [Inject] private IMediator Mediator { get; set; } = default!;
    [Inject] private ToastService ToastService { get; set; } = default!;
    [Inject] private TimeProvider Reloj { get; set; } = default!;
    [Inject] private IStringLocalizer<TextosFacturacion> Textos { get; set; } = default!;

    /// <summary>Los meses que ofrece el selector: los tres anteriores, el actual y el siguiente (que sale como futuro).</summary>
    private const int MesesAntes = 3;

    private static readonly ConceptoTarifaOperadorCae[] Conceptos =
        [ConceptoTarifaOperadorCae.VisitaTrabajadorExtranjero, ConceptoTarifaOperadorCae.CentroNuevo, ConceptoTarifaOperadorCae.ObraNueva];

    /// <summary>Un índice por concepto, en el orden de <see cref="Conceptos"/>: lo recorre el drawer de tarifas.</summary>
    private static readonly int[] IndicesTarifa = [0, 1, 2];

    private int _anyo;
    private int _mes;
    private bool _cargando = true;
    private bool _error;
    private bool _sinAcceso;
    private int _solicitud;
    private ModeloFacturacion? _modelo;
    private IReadOnlyList<TarifaOperadorCaeDto>? _tarifas;
    private Guid _empresaId;

    protected override async Task OnInitializedAsync()
    {
        var hoy = DiaDeNegocio.Hoy(Reloj);
        _anyo = hoy.Year;
        _mes = hoy.Month;
        await CargarAsync();
    }

    private (int Anyo, int Mes) MesActual
    {
        get
        {
            var hoy = DiaDeNegocio.Hoy(Reloj);
            return (hoy.Year, hoy.Month);
        }
    }

    private DateTime AhoraUtc => Reloj.GetUtcNow().UtcDateTime;

    // ------------------------------------------------------------------ carga

    private async Task CargarAsync()
    {
        var solicitud = ++_solicitud;
        _cargando = true;
        _error = false;
        _sinAcceso = false;
        StateHasChanged();

        try
        {
            // En serie: el DbContext del circuito no admite consultas concurrentes.
            var serie = await Mediator.Send(new ObtenerSerieCuadroDireccionQuery(_anyo, _mes, 1, ConFacturacion: true));
            var extranjeros = await Mediator.Send(new ContarExtranjerosCobrablesDelMesQuery(_anyo, _mes));
            var tarifas = await Mediator.Send(new ObtenerTarifasOperadorCaeQuery());
            if (solicitud != _solicitud)
                return;

            _tarifas = tarifas;
            _modelo = FacturacionOperadorModelo.Construir(_anyo, _mes, serie, extranjeros, tarifas, AhoraUtc);
            if (_modelo.Empresas.All(e => e.TenantId != _empresaId))
                _empresaId = _modelo.Empresas.FirstOrDefault()?.TenantId ?? Guid.Empty;
        }
        catch (UnauthorizedAccessException)
        {
            if (solicitud != _solicitud)
                return;

            _sinAcceso = true;
        }
        catch (Exception)
        {
            if (solicitud != _solicitud)
                return;

            _error = true;
        }
        finally
        {
            if (solicitud == _solicitud)
                _cargando = false;
        }
    }

    private async Task ElegirMesAsync(int anyo, int mes)
    {
        if (_cargando || (anyo == _anyo && mes == _mes))
            return;

        _anyo = anyo;
        _mes = mes;
        await CargarAsync();
    }

    // ------------------------------------------------------------------ selector de mes

    private sealed record ChipMes(int Anyo, int Mes, string Corto, string Marca, bool Futuro, string Titulo);

    private IEnumerable<ChipMes> Meses()
    {
        var (anyoActual, mesActual) = MesActual;
        var actual = new DateOnly(anyoActual, mesActual, 1);
        for (var i = -MesesAntes; i <= 1; i++)
        {
            var d = actual.AddMonths(i);
            var futuro = i > 0;
            yield return new ChipMes(
                d.Year, d.Month, Textos[$"MesCorto{d.Month}"].Value,
                futuro ? Textos["MarcaFuturo"].Value : i == 0 ? Textos["MarcaEnCurso"].Value : Textos["MarcaCerrado"].Value,
                futuro,
                futuro ? Textos["TituloMesFuturo", NombreMes(d.Year, d.Month)].Value : NombreMes(d.Year, d.Month));
        }
    }

    private string NombreMes(int anyo, int mes) => $"{Textos[$"Mes{mes}"].Value} {anyo.ToString(CultureInfo.CurrentCulture)}";

    // ------------------------------------------------------------------ estado del mes

    private (string Clave, string Texto, string Detalle) EstadoDelMes => _modelo?.Estado switch
    {
        EstadoCifraMensual.Existe => ("cerrado", Textos["EstadoCerrado"].Value, Textos["EstadoCerradoDetalle"].Value),
        EstadoCifraMensual.Estimado => ("sin-cerrar", Textos["EstadoSinCerrar"].Value, Textos["EstadoSinCerrarDetalle"].Value),
        _ => ("en-curso", Textos["EstadoEnCurso"].Value,
            Textos["EstadoEnCursoDetalle", DiaDeNegocio.Hoy(Reloj).ToString("d", CultureInfo.CurrentCulture)].Value),
    };

    private string AlcanceParcial => _modelo is { TenantsSinAlcanceCompleto: 1 }
        ? Textos["AlcanceParcialUna"].Value
        : Textos["AlcanceParcial", _modelo?.TenantsSinAlcanceCompleto ?? 0].Value;

    // ------------------------------------------------------------------ empresas

    private EmpresaFacturada? Seleccionada => _modelo?.Empresas.FirstOrDefault(e => e.TenantId == _empresaId);

    private void ElegirEmpresa(Guid tenantId) => _empresaId = tenantId;

    private string SubEmpresa(EmpresaFacturada e) =>
        Textos["SubEmpresa", Unidades(e.Extranjeros), Unidades(e.CentrosNuevos), Unidades(e.ObrasNuevas)].Value;

    private string Unidades(int? unidades) => unidades?.ToString(CultureInfo.CurrentCulture) ?? Textos["SinDato"].Value;

    private string TextoCuota(EmpresaFacturada e)
    {
        if (e.ImportesOcultos || e.SinVigencia)
            return Textos["SinImporte"].Value;
        return e.SinTarifa ? Textos["SinTarifa"].Value : Dinero(e.Cuota);
    }

    private static string ClaseCuota(EmpresaFacturada e) => e.SinTarifa && !e.ImportesOcultos ? "fct-cuota fct-cuota-aviso" : "fct-cuota";

    private string TextoTotal() => _modelo is { Total.Count: > 0 } ? Dinero(_modelo.Total) : Dinero(0m, "EUR", false);

    // ------------------------------------------------------------------ conceptos

    private string NombreConcepto(ConceptoTarifaOperadorCae concepto) => concepto switch
    {
        ConceptoTarifaOperadorCae.VisitaTrabajadorExtranjero => Textos["ConceptoExtranjeros"].Value,
        ConceptoTarifaOperadorCae.CentroNuevo => Textos["ConceptoCentros"].Value,
        _ => Textos["ConceptoObras"].Value,
    };

    private string UnidadConcepto(ConceptoTarifaOperadorCae concepto) => concepto switch
    {
        ConceptoTarifaOperadorCae.VisitaTrabajadorExtranjero => Textos["UnidadExtranjero"].Value,
        ConceptoTarifaOperadorCae.CentroNuevo => Textos["UnidadCentro"].Value,
        _ => Textos["UnidadObra"].Value,
    };

    private string TextoTarifa(EmpresaFacturada e, ConceptoFacturado c)
    {
        if (e.ImportesOcultos)
            return Textos["SinImporte"].Value;
        return c.Estado switch
        {
            EstadoTarifaConcepto.NoSeCobra => Textos["NoSeCobra"].Value,
            EstadoTarifaConcepto.Cobra => Textos["TarifaPorUnidad", Dinero(c.Precio ?? 0m, c.Moneda, true), UnidadConcepto(c.Concepto)].Value,
            _ => Textos["SinTarifa"].Value,
        };
    }

    private string TextoImporte(ConceptoFacturado c) =>
        c.Estado == EstadoTarifaConcepto.Cobra && c.Importe is { } importe ? Dinero(importe, c.Moneda, false) : Textos["SinImporte"].Value;

    private static bool TarifaActiva(ConceptoFacturado c) => c.Estado == EstadoTarifaConcepto.Cobra;

    private string TextoGestiones(EmpresaFacturada e) => e.Gestiones is { } gestiones
        ? Textos["GestionesDetalle", Math.Max(0, gestiones - (e.GestionesExtranjero ?? 0)).ToString(CultureInfo.CurrentCulture),
            (e.GestionesExtranjero ?? 0).ToString(CultureInfo.CurrentCulture)].Value
        : Textos["SinDato"].Value;

    private string DineroOSinDato(double? valor, EmpresaFacturada e) =>
        valor is { } v && e.Cuota.Count == 1 ? Dinero((decimal)v, e.Cuota[0].Moneda, true) : Textos["SinDato"].Value;

    private string TextoRelacion(EmpresaFacturada e) => e.Relacion is { } r
        ? "×" + r.ToString("0.0", CultureInfo.CurrentCulture)
        : Textos["SinDato"].Value;

    // ------------------------------------------------------------------ dinero

    private static string Simbolo(string? moneda) => (moneda ?? "EUR").Trim().ToUpperInvariant() switch
    {
        "EUR" => "€",
        "USD" => "$",
        var otra => otra,
    };

    /// <summary>«1.234 €» o «40,00 €»: con decimales los precios y las tasas, sin ellos los importes enteros.</summary>
    private static string Dinero(decimal importe, string? moneda, bool decimales)
    {
        var enteroExacto = !decimales && decimal.Round(importe, 0) == importe;
        return importe.ToString(enteroExacto ? "N0" : "N2", CultureInfo.CurrentCulture) + " " + Simbolo(moneda);
    }

    private static string Dinero(IReadOnlyList<ImporteEnMoneda> importes) =>
        string.Join(" + ", importes.Select(i => Dinero(i.Importe, i.Moneda, false)));

    // ------------------------------------------------------------------ exportaciones

    private string RutaExcel => FormattableString.Invariant($"{FacturacionEndpoints.RutaCuotaDelOperador}?anyo={_anyo}&mes={_mes}");

    private string RutaExtranjeros =>
        FormattableString.Invariant($"{CuadroDireccionEndpoints.RutaExtranjerosDelMes}?anyo={_anyo}&mes={_mes}");

    private int ExtranjerosDelMes => _modelo?.ExtranjerosDelMes ?? 0;

    private string TituloExtranjeros => ExtranjerosDelMes > 0
        ? Textos["ExtranjerosFilas", ExtranjerosDelMes.ToString(CultureInfo.CurrentCulture)].Value
        : Textos["ExtranjerosNinguno", NombreMes(_anyo, _mes)].Value;

    // ------------------------------------------------------------------ drawer de tarifas

    private readonly InstantaneaFormulario _instantanea = new();
    private bool _drawerVisible;
    private bool _guardando;
    private string? _errorGuardar;
    private Guid _drawerTenantId;
    private string _drawerNombre = string.Empty;
    private bool _drawerSinTarifa;
    private string _moneda = "EUR";
    private readonly bool[] _activo = new bool[3];
    private readonly decimal?[] _precio = new decimal?[3];

    private bool HayCambiosTarifas => _drawerVisible && _instantanea.Difiere(ValoresDrawer());

    private object?[] ValoresDrawer() => [_moneda, _activo[0], _precio[0], _activo[1], _precio[1], _activo[2], _precio[2]];

    private TarifaOperadorCaeDto? Vigente(Guid tenantId, ConceptoTarifaOperadorCae concepto) =>
        _tarifas is null ? null : ValoracionOperadorCae.VigenteEn(_tarifas, tenantId, concepto, AhoraUtc);

    private void AbrirTarifas(EmpresaFacturada empresa)
    {
        if (_tarifas is null)
            return;

        _drawerTenantId = empresa.TenantId;
        _drawerNombre = empresa.Nombre;
        _drawerSinTarifa = _tarifas.All(t => t.PropietarioTenantId != empresa.TenantId);
        _errorGuardar = null;
        _moneda = Conceptos.Select(c => Vigente(empresa.TenantId, c)).FirstOrDefault(v => v is not null)?.MonedaIso.Trim().ToUpperInvariant() ?? "EUR";
        for (var i = 0; i < Conceptos.Length; i++)
        {
            var version = Vigente(empresa.TenantId, Conceptos[i]);
            // Sin tarifa, como la maqueta: extranjeros activo y los otros dos sin cobrar, todos sin precio.
            _activo[i] = version?.Activa ?? i == 0;
            _precio[i] = version?.PrecioUnitario;
        }

        _drawerVisible = true;
        _instantanea.Fijar(ValoresDrawer());
    }

    private Task CerrarDrawerAsync(bool visible)
    {
        _drawerVisible = visible;
        if (!visible)
            _errorGuardar = null;
        return Task.CompletedTask;
    }

    private void AlternarActivo(int indice, bool valor) => _activo[indice] = valor;

    private void FijarPrecio(int indice, object? valor)
    {
        _precio[indice] = decimal.TryParse(valor?.ToString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var precio) && precio >= 0
            ? precio
            : null;
    }

    private string UnidadPrecio => _moneda == "USD" ? Textos["UnidadPrecioUsd"].Value : Textos["UnidadPrecioEur"].Value;

    private string NombreTarifaDrawer(int indice) => indice == 0 ? Textos["NombreTarifaExtranjeros"].Value : NombreConcepto(Conceptos[indice]);

    private string AyudaDrawer(int indice) => indice switch
    {
        0 => Textos["AyudaExtranjeros"].Value,
        1 => Textos["AyudaCentros"].Value,
        _ => Textos["AyudaObras"].Value,
    };

    private string? MotivoGuardarDeshabilitado => HayCambiosTarifas ? null : Textos["MotivoSinCambios"].Value;

    private async Task GuardarTarifasAsync()
    {
        if (_guardando || !HayCambiosTarifas)
            return;

        _guardando = true;
        _errorGuardar = null;
        try
        {
            foreach (var comando in ComandosAEnviar())
            {
                var resultado = await Mediator.Send(comando);
                if (resultado.EsFallido)
                {
                    _errorGuardar = resultado.Error.Mensaje;
                    return;
                }
            }

            _drawerVisible = false;
            ToastService.Mostrar(Textos["AvisoTarifasGuardadas", NombreMes(MesActual.Anyo, MesActual.Mes)].Value, TonoToast.Exito);
            await CargarAsync();
        }
        catch (ValidationException ex)
        {
            _errorGuardar = ex.Errors.FirstOrDefault()?.ErrorMessage ?? Textos["ErrorGuardarTarifas"].Value;
        }
        catch (Exception)
        {
            _errorGuardar = Textos["ErrorGuardarTarifas"].Value;
        }
        finally
        {
            _guardando = false;
        }
    }

    /// <summary>
    /// Una versión nueva por concepto que cambió (y las tres si la Empresa no tenía tarifa o cambió la moneda: la tarifa
    /// se guarda entera). Un precio en blanco vale cero, como la maqueta.
    /// </summary>
    private IEnumerable<FijarTarifaOperadorCaeCommand> ComandosAEnviar()
    {
        var todas = _drawerSinTarifa;
        for (var i = 0; i < Conceptos.Length; i++)
        {
            var precio = _precio[i] ?? 0m;
            var version = Vigente(_drawerTenantId, Conceptos[i]);
            var cambia = todas || version is null || version.Activa != _activo[i] || version.PrecioUnitario != precio
                || !string.Equals(version.MonedaIso.Trim(), _moneda, StringComparison.OrdinalIgnoreCase);
            if (cambia)
                yield return new FijarTarifaOperadorCaeCommand(_drawerTenantId, Conceptos[i], precio, _moneda, _activo[i]);
        }
    }
}
