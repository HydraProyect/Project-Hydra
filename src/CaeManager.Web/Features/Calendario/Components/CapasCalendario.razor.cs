using CaeManager.Application.Calendario.Queries;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;

namespace CaeManager.Web.Features.Calendario.Components;

/// <summary>
/// Las capas «Festivos» y «Jornada intensiva» del Mes de /calendario: carga <see cref="ObtenerCapasCalendarioQuery"/>
/// del mes que se ve, guarda las casillas y «Festivos de», y entrega a la página las <see cref="MarcasCapasMes"/> ya
/// filtradas. Es una capa: si su carga falla, el calendario sigue sin ella (no hay error de página por una capa).
/// </summary>
public partial class CapasCalendario
{
    [Inject] private IMediator Mediator { get; set; } = default!;
    [Inject] private ILogger<CapasCalendario> Logger { get; set; } = default!;

    [Parameter, EditorRequired] public int Anio { get; set; }
    [Parameter, EditorRequired] public int Mes { get; set; }

    /// <summary>Cada vez que cambian los datos del mes, una casilla o «Festivos de».</summary>
    [Parameter] public EventCallback<MarcasCapasMes> MarcasChanged { get; set; }

    private CapasCalendarioDto _capas = CapasCalendarioDto.Vacio;
    private bool _festivos = true;
    private bool _intensiva = true;
    private string? _comunidad;
    private (int Anio, int Mes)? _cargado;
    private int _version;

    protected override async Task OnParametersSetAsync()
    {
        if (_cargado == (Anio, Mes)) return;
        _cargado = (Anio, Mes);
        var version = ++_version;

        CapasCalendarioDto capas;
        try
        {
            capas = await Mediator.Send(new ObtenerCapasCalendarioQuery(Anio, Mes));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogWarning(ex, "No se pudieron cargar las capas del calendario de {Anio}-{Mes:00}.", Anio, Mes);
            capas = CapasCalendarioDto.Vacio;
        }

        // Una respuesta de un mes que ya no se ve se descarta: manda la del último mes pedido.
        if (version != _version) return;
        _capas = capas;
        if (_comunidad is not null && !_capas.Comunidades.Contains(_comunidad)) _comunidad = null;
        await AvisarAsync();
    }

    private Task CambiarFestivosAsync(ChangeEventArgs e)
    {
        _festivos = e.Value is true;
        return AvisarAsync();
    }

    private Task CambiarIntensivaAsync(ChangeEventArgs e)
    {
        _intensiva = e.Value is true;
        return AvisarAsync();
    }

    private Task CambiarComunidadAsync(ChangeEventArgs e)
    {
        var valor = e.Value as string;
        _comunidad = string.IsNullOrEmpty(valor) || !_capas.Comunidades.Contains(valor) ? null : valor;
        return AvisarAsync();
    }

    private Task AvisarAsync() => MarcasChanged.InvokeAsync(MarcasCapasMes.Calcular(
        _capas, _festivos, _intensiva, _comunidad,
        f => MarcasCapasMes.EsNacional(f) ? Textos["FestivoNacional", f.Nombre] : Textos["FestivoConLugar", f.Nombre, f.Lugar!],
        etiqueta => Textos["FestivoAccesible", etiqueta],
        Textos["IntensivaAccesible"]));
}
