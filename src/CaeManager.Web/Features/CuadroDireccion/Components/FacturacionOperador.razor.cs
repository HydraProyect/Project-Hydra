using System.Globalization;
using CaeManager.Application.Dashboard.Queries;
using CaeManager.Domain.Dashboard;
using CaeManager.Web.Components.DesignSystem;
using Microsoft.AspNetCore.Components;

namespace CaeManager.Web.Features.CuadroDireccion.Components;

/// <summary>
/// La pestaña «Facturación» del Cuadro de dirección: lo que el Operador CAE externo cobra a cada Empresa gestionada.
/// Lee <see cref="ObtenerFacturacionOperadorCaeQuery"/> (y, en el detalle de una Empresa, las filas de
/// <see cref="ObtenerExtranjerosDeEmpresasGestionadasDelMesQuery"/>, acotadas a la vigencia como la cifra) y no calcula nada con precios propios. Quien no gestiona las tarifas
/// (<c>PuedeFijarTarifas</c> en falso: el Coordinador CAE) ve las unidades y, donde iría un importe, el aviso «Los importes
/// los ven Dirección CAE y Administrador», no «Sin dato»: el servidor le oculta los importes por regla, no por falta de dato.
/// </summary>
public partial class FacturacionOperador : ComponentBase
{
    private FacturacionOperadorCaeDto? _datos;
    private bool _error;
    private string _seccion = "general";
    private Guid? _empresa;
    private Guid? _drawerTenantId;
    private int _anyoCargado;
    private int _mesCargado;
    private int _version;

    private ExtranjerosDelMesDto? _extranjeros;
    private bool _cargandoExtranjeros;
    private int _versionCargandoExtranjeros;
    private bool _errorExtranjeros;

    /// <summary>El mes que se mira: lo manda la barra del Cuadro.</summary>
    [Parameter, EditorRequired] public int Anyo { get; set; }

    [Parameter, EditorRequired] public int Mes { get; set; }

    /// <summary>«octubre de 2026»: el mes en los textos de la pestaña.</summary>
    [Parameter, EditorRequired] public string NombreMes { get; set; } = string.Empty;

    /// <summary>El mes ya tiene cierre mensual: sus unidades y su tarifa quedaron congeladas.</summary>
    [Parameter] public bool MesCerrado { get; set; }

    /// <summary>Quien lleva la cartera principal de cada Empresa gestionada (rótulo «Cartera de …»), si se sabe.</summary>
    [Parameter] public IReadOnlyDictionary<Guid, string>? Principales { get; set; }

    private bool VeImportes => _datos?.PuedeFijarTarifas == true;

    private string SinDato => T["SinDato"].Value;

    /// <summary>Lo que ve quien no gestiona las tarifas donde iría un importe: no es «sin dato», es que ese rol no ve importes.</summary>
    private string Reservado => T["FactImportesReservados"].Value;

    /// <summary>La versión corta de <see cref="Reservado"/> para las columnas derivadas (con la frase entera como ayuda).</summary>
    private string ReservadoCorto => T["FactImporteReservado"].Value;

    private int SinTarifa => _datos is null ? 0 : _datos.Filas.Count(f => f.SinTarifa);

    private int CentrosCobrados => _datos is null ? 0 : _datos.Filas.Where(f => f.Centro.PrecioUnitario is not null && f.Centro.CobroActivo).Sum(f => f.Centro.Unidades);

    private int ObrasCobradas => _datos is null ? 0 : _datos.Filas.Where(f => f.Obra.PrecioUnitario is not null && f.Obra.CobroActivo).Sum(f => f.Obra.Unidades);

    private IReadOnlyList<PestanaDefinicion> Definiciones =>
    [
        new("general", T["FactPestanaResumen"].Value),
        new("detalle", T["FactPestanaPorEmpresa"].Value),
    ];

    private FilaFacturacionOperadorCaeDto? Seleccionada =>
        _datos is null ? null : _datos.Filas.FirstOrDefault(f => f.TenantId == _empresa) ?? _datos.Filas.FirstOrDefault();

    protected override async Task OnParametersSetAsync()
    {
        if (_anyoCargado == Anyo && _mesCargado == Mes && (_datos is not null || _error)) return;
        await CargarAsync();
    }

    private async Task CargarAsync()
    {
        var version = ++_version;
        (_anyoCargado, _mesCargado) = (Anyo, Mes);
        _error = false;
        _extranjeros = null;
        _errorExtranjeros = false;
        _cargandoExtranjeros = false;
        try
        {
            var datos = await Mediator.Send(new ObtenerFacturacionOperadorCaeQuery(Anyo, Mes));
            if (version != _version) return;
            _datos = datos;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (version != _version) return;
            Logger.LogError(ex, "Error al cargar la facturación del Operador CAE.");
            _error = true;
            return;
        }

        if (_seccion == "detalle")
            await CargarExtranjerosAsync();
    }

    private async Task CambiarSeccionAsync(string seccion)
    {
        _seccion = seccion;
        if (seccion == "detalle")
            await CargarExtranjerosAsync();
    }

    private async Task VerDetalleAsync(Guid tenantId)
    {
        _empresa = tenantId;
        await CambiarSeccionAsync("detalle");
    }

    private void CambiarEmpresa(ChangeEventArgs e)
    {
        if (Guid.TryParse(e.Value?.ToString(), out var id))
            _empresa = id;
    }

    /// <summary>
    /// Las filas Trabajador × Visita del mes de las Empresas gestionadas, una sola vez por mes y acotadas a la vigencia de su
    /// Asignación de Operación (<see cref="ObtenerExtranjerosDeEmpresasGestionadasDelMesQuery"/>): son las que cuenta la cifra y
    /// las del Excel por Empresa. Cada carga lleva la versión del mes que la pidió: si el mes cambia mientras llega, su
    /// respuesta (o su fallo) se descarta entera, sin tocar el estado del mes nuevo, y la carga del mes nuevo no queda
    /// bloqueada por la anterior.
    /// </summary>
    private async Task CargarExtranjerosAsync()
    {
        var version = _version;
        if (_extranjeros is not null || (_cargandoExtranjeros && _versionCargandoExtranjeros == version)) return;

        _cargandoExtranjeros = true;
        _versionCargandoExtranjeros = version;
        _errorExtranjeros = false;
        var (anyo, mes) = (Anyo, Mes);
        try
        {
            var filas = await Mediator.Send(new ObtenerExtranjerosDeEmpresasGestionadasDelMesQuery(anyo, mes));
            if (version == _version) _extranjeros = filas;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (version != _version) return;
            Logger.LogError(ex, "Error al cargar los Trabajadores extranjeros del mes.");
            _errorExtranjeros = true;
        }
        finally
        {
            if (version == _version) _cargandoExtranjeros = false;
        }
    }

    private IReadOnlyList<FilaExtranjeroDelMesDto> ExtranjerosDe(Guid tenantId) =>
        _extranjeros?.Filas.Where(f => f.TenantId == tenantId).ToList() ?? [];

    private void AbrirTarifas(Guid tenantId) => _drawerTenantId = tenantId;

    private string? Gestor(Guid tenantId) => Principales is not null && Principales.TryGetValue(tenantId, out var nombre) && nombre.Length > 0 ? nombre : null;

    private string RutaExportar(Guid? tenantId) => FormattableString.Invariant(
        $"{CuadroDireccionEndpoints.RutaExtranjerosDelMes}?anyo={Anyo}&mes={Mes}") + (tenantId is { } id ? FormattableString.Invariant($"&tenantId={id}") : string.Empty);

    // ---------------------------------------------------------------- formatos (maqueta: eur sin decimales, eur2 con dos, rel «×2,4»)

    private static string N(int valor) => valor.ToString("N0", CuadroDireccionUi.Castellano);

    private static string Simbolo(string? moneda) => string.IsNullOrEmpty(moneda) || moneda == "EUR" ? "€" : moneda;

    private static string Eur(decimal valor, string? moneda) => valor.ToString("N0", CuadroDireccionUi.Castellano) + " " + Simbolo(moneda);

    private string Eur2(decimal? valor, string? moneda) =>
        valor is { } v ? v.ToString("N2", CuadroDireccionUi.Castellano) + " " + Simbolo(moneda) : SinDato;

    private string Rel(decimal? relacion) =>
        relacion is { } r ? "×" + r.ToString("N1", CuadroDireccionUi.Castellano) : SinDato;

    private static string Fecha(DateOnly fecha) => fecha.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);

    /// <summary>El importe de un concepto en la tabla: «—» sin tarifa, «No se cobra» con el cobro desactivado.</summary>
    private string ImporteConcepto(ConceptoFacturadoOperadorCaeDto concepto, FilaFacturacionOperadorCaeDto fila) =>
        !VeImportes ? string.Empty
        : fila.SinTarifa || concepto.PrecioUnitario is null ? "—"
        : !concepto.CobroActivo ? T["FactNoSeCobra"].Value
        : Eur(concepto.Importe, fila.Moneda);

    private string PorUnidad(ConceptoTarifaOperadorCae concepto) => concepto switch
    {
        ConceptoTarifaOperadorCae.VisitaTrabajadorExtranjero => T["FactPorUnidadVisitaTrabajadorExtranjero"].Value,
        ConceptoTarifaOperadorCae.CentroNuevo => T["FactPorUnidadCentroNuevo"].Value,
        _ => T["FactPorUnidadObraNueva"].Value,
    };

    /// <summary>El pie de la tarjeta de cuota: cuántas Empresas tienen tarifa, y cuántas no.</summary>
    private string SubCuota => !VeImportes || _datos is null ? string.Empty
        : SinTarifa > 0 ? T["FactKpiCuotaSub", _datos.Total.EmpresasConTarifa, _datos.Filas.Count].Value + T["FactKpiCuotaSubSin", SinTarifa].Value
        : T["FactKpiCuotaSub", _datos.Total.EmpresasConTarifa, _datos.Filas.Count].Value;

    private string TextoTarifa(ConceptoFacturadoOperadorCaeDto concepto, FilaFacturacionOperadorCaeDto fila) =>
        !VeImportes ? ReservadoCorto
        : fila.SinTarifa || concepto.PrecioUnitario is null ? T["FactSinTarifa"].Value
        : !concepto.CobroActivo ? T["FactNoSeCobra"].Value
        : Eur2(concepto.PrecioUnitario, fila.Moneda) + " / " + PorUnidad(concepto.Concepto);

    /// <summary>Las cuatro cifras de «Ingreso por gestión» de una Empresa, con su cuenta; siempre por Empresa gestionada, nunca por persona.</summary>
    private IEnumerable<(string Titulo, string Valor, string Texto)> IngresosDe(FilaFacturacionOperadorCaeDto fila)
    {
        var i = fila.Ingreso;
        var gestiones = fila.Gestiones ?? 0;
        var normales = fila.GestionesNormales ?? 0;
        var conCuota = VeImportes && fila.Cuota is not null;
        var cuota = fila.Cuota ?? 0m;
        string Cuenta(decimal importe, string unidades) =>
            (conCuota ? Eur(importe, fila.Moneda) + " ÷ " : string.Empty) + unidades;

        string Valor(string calculado) => VeImportes ? calculado : ReservadoCorto;
        yield return (T["FactComoTotal"].Value, Valor(Eur2(i.PorGestion, fila.Moneda)),
            (conCuota ? Eur(cuota, fila.Moneda) + " ÷ " : string.Empty) + T["FactIngresoGestionesTotal", N(gestiones), N(normales), N(fila.GestionesDeExtranjeros)].Value);
        yield return (T["FactColCostoGestion"].Value, Valor(Eur2(i.PorGestionNormal, fila.Moneda)), Cuenta(cuota - fila.Extranjero.Importe, T["FactIngresoGestionesNormales", N(normales)].Value));
        yield return (T["FactColCostoExtranjero"].Value, Valor(Eur2(i.PorGestionExtranjero, fila.Moneda)), Cuenta(fila.Extranjero.Importe, T["FactIngresoGestionesExtranjero", N(fila.GestionesDeExtranjeros)].Value));
        yield return (T["FactColRelacion"].Value, Valor(Rel(i.Relacion)), T["FactIngresoRelacionDef"].Value);
    }
}
