using System.Globalization;
using CaeManager.Domain.Common;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

namespace CaeManager.Web.Components.DesignSystem;

public partial class CampoFecha
{
    private readonly string _id = $"campo-fecha-{Guid.NewGuid():N}";

    private ElementReference _refInput;
    private ElementReference _refDia;

    /// <summary>Lo que el navegador tiene ahora en el input, según lo último que llegó de él o que se le impuso.</summary>
    private string _texto = string.Empty;

    /// <summary>Lo que Blazor renderizó por última vez en <c>value</c>; no se toca al teclear (ver CampoTexto).</summary>
    private string _valorMostrado = string.Empty;

    private int _generacion;
    private DateOnly? _ultimoNotificado;
    private bool _parametrosRecibidos;
    private string? _errorPropio;

    private bool _abierto;
    private DateOnly _mesVisible;
    private DateOnly _diaEnfoco;
    private bool _enfocarDia;
    private bool _enfocarInput;

    [Parameter] public string? Etiqueta { get; set; }
    [Parameter] public DateOnly? Valor { get; set; }
    [Parameter] public EventCallback<DateOnly?> ValorChanged { get; set; }
    [Parameter] public string? Placeholder { get; set; }
    [Parameter] public bool Deshabilitado { get; set; }
    [Parameter] public string? MensajeError { get; set; }

    /// <summary>Se dispara al perder el foco, después de <see cref="ValorChanged"/>, como en CampoTexto.</summary>
    [Parameter] public EventCallback OnBlur { get; set; }

    /// <summary>
    /// Opcional: al salir con un texto que no es fecha, en vez de notificar null se entrega ese texto. Lo usa CampoTexto
    /// Type="date" para que el formulario que enlaza texto lo reciba tal cual y su propia validación lo rechace ("Introduce
    /// una fecha válida"), en lugar de recibir un vacío que guardaría la fecha borrada.
    /// </summary>
    [Parameter] public EventCallback<string> TextoNoValido { get; set; }

    [Parameter(CaptureUnmatchedValues = true)]
    public Dictionary<string, object>? AtributosAdicionales { get; set; }

    private string? Error => !string.IsNullOrWhiteSpace(MensajeError) ? MensajeError : _errorPropio;
    private bool TieneError => !string.IsNullOrWhiteSpace(Error);
    private string IdMensajeError => $"{_id}-error";
    private string IdCalendario => $"{_id}-calendario";

    // El botón no repite la etiqueta en su aria-label (la toma por aria-describedby): así «getByLabel("Fecha de emisión")»
    // sigue encontrando un solo control, el campo.
    private string IdEtiqueta => $"{_id}-etiqueta";

    private string ClaseInput
    {
        get
        {
            var claseBase = TieneError ? "campo-input campo-input-error campo-fecha-input" : "campo-input campo-fecha-input";
            return AtributosAdicionales is not null
                && AtributosAdicionales.TryGetValue("class", out var clase)
                && clase is string texto
                && !string.IsNullOrWhiteSpace(texto)
                ? $"{claseBase} {texto}"
                : claseBase;
        }
    }

    /// <summary>Los atributos del llamador sin «class» (fusionada en ClaseInput) y con el mensaje de error en aria-describedby.</summary>
    private Dictionary<string, object>? AtributosInput
    {
        get
        {
            var atributos = AtributosAdicionales is null
                ? new Dictionary<string, object>()
                : AtributosAdicionales.Where(kvp => kvp.Key != "class").ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
            if (TieneError)
            {
                atributos["aria-describedby"] =
                    atributos.TryGetValue("aria-describedby", out var previo) && previo is string previoTexto && !string.IsNullOrWhiteSpace(previoTexto)
                        ? $"{previoTexto} {IdMensajeError}"
                        : IdMensajeError;
            }

            return atributos.Count == 0 ? null : atributos;
        }
    }

    /// <summary>
    /// El texto se resincroniza con <see cref="Valor"/> solo cuando el padre lo cambia por su cuenta (limpia el formulario,
    /// propone hoy, carga otro registro). Si el valor que llega es el último que este campo notificó, lo que hay en pantalla
    /// manda: es justo lo que el usuario está escribiendo.
    /// </summary>
    protected override void OnParametersSet()
    {
        if (_parametrosRecibidos && Valor == _ultimoNotificado) return;

        _parametrosRecibidos = true;
        _ultimoNotificado = Valor;
        _errorPropio = null;
        MostrarTexto(MascaraFecha.Formatear(Valor));
    }

    /// <summary>
    /// Impone <paramref name="nuevo"/> en el navegador. Si Blazor ya tenía ese mismo texto renderizado el diff no enviaría
    /// nada (el navegador se fue por su lado al teclear), así que se recrea el input con @key.
    /// </summary>
    private void MostrarTexto(string nuevo)
    {
        if (nuevo == _texto && nuevo == _valorMostrado) return;
        if (nuevo == _valorMostrado) _generacion++;
        _valorMostrado = nuevo;
        _texto = nuevo;
    }

    private async Task AlTeclearAsync(ChangeEventArgs e)
    {
        if (Deshabilitado) return;

        var crudo = e.Value?.ToString() ?? string.Empty;
        _texto = crudo;
        var mascara = MascaraFecha.Aplicar(crudo);
        // Con el script del navegador activo ya llega enmascarado y no se reescribe nada; sin él, se impone aquí.
        if (mascara != crudo) MostrarTexto(mascara);
        _errorPropio = null;

        if (mascara.Length == 0)
            await NotificarAsync(null);
        else if (MascaraFecha.EsCompleta(mascara, out var fecha))
            await NotificarAsync(fecha);
    }

    private async Task AlSalirAsync()
    {
        if (Deshabilitado) return;
        await ConfirmarTextoAsync();
        await OnBlur.InvokeAsync();
    }

    private async Task ConfirmarTextoAsync()
    {
        var mascara = MascaraFecha.Aplicar(_texto);
        if (mascara.Length == 0)
        {
            _errorPropio = null;
            await NotificarAsync(null);
        }
        else if (MascaraFecha.TryInterpretar(mascara, out var fecha))
        {
            _errorPropio = null;
            MostrarTexto(MascaraFecha.Formatear(fecha)); // «1/2/2026» pegado queda como 01/02/2026
            await NotificarAsync(fecha);
        }
        else
        {
            _errorPropio = Comunes["CampoFechaInvalida"];
            if (TextoNoValido.HasDelegate)
            {
                _ultimoNotificado = null;
                await TextoNoValido.InvokeAsync(mascara);
            }
            else
            {
                await NotificarAsync(null);
            }
        }
    }

    private async Task NotificarAsync(DateOnly? fecha)
    {
        if (fecha == _ultimoNotificado) return;
        _ultimoNotificado = fecha;
        await ValorChanged.InvokeAsync(fecha);
    }

    private void AlternarCalendario()
    {
        if (Deshabilitado) return;
        if (_abierto)
        {
            Cerrar();
            return;
        }

        var inicial = MascaraFecha.TryInterpretar(_texto, out var escrita) ? escrita : Valor ?? Hoy();
        _diaEnfoco = inicial;
        _mesVisible = new DateOnly(inicial.Year, inicial.Month, 1);
        _abierto = true;
        _enfocarDia = true;
    }

    private void Cerrar()
    {
        _abierto = false;
        _enfocarInput = true;
    }

    private Task CerrarYVolverAsync()
    {
        Cerrar();
        return Task.CompletedTask;
    }

    private async Task ElegirAsync(DateOnly fecha)
    {
        MostrarTexto(MascaraFecha.Formatear(fecha));
        _errorPropio = null;
        Cerrar();
        await NotificarAsync(fecha);
    }

    private void CambiarMes(int meses)
    {
        _mesVisible = _mesVisible.AddMonths(meses);
        var dia = Math.Min(_diaEnfoco.Day, DateTime.DaysInMonth(_mesVisible.Year, _mesVisible.Month));
        _diaEnfoco = new DateOnly(_mesVisible.Year, _mesVisible.Month, dia);
    }

    private Task AlPulsarEnCalendarioAsync(KeyboardEventArgs e)
    {
        if (e.Key == "Escape") Cerrar();
        return Task.CompletedTask;
    }

    private Task AlPulsarEnRejillaAsync(KeyboardEventArgs e)
    {
        var lunes = _diaEnfoco.AddDays(-DiaDeLaSemana(_diaEnfoco));
        DateOnly? destino = e.Key switch
        {
            "ArrowLeft" => _diaEnfoco.AddDays(-1),
            "ArrowRight" => _diaEnfoco.AddDays(1),
            "ArrowUp" => _diaEnfoco.AddDays(-7),
            "ArrowDown" => _diaEnfoco.AddDays(7),
            "Home" => lunes,
            "End" => lunes.AddDays(6),
            "PageUp" => _diaEnfoco.AddMonths(-1),
            "PageDown" => _diaEnfoco.AddMonths(1),
            _ => null,
        };
        if (destino is { } d)
        {
            _diaEnfoco = d;
            _mesVisible = new DateOnly(d.Year, d.Month, 1);
            _enfocarDia = true;
        }

        return Task.CompletedTask;
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_enfocarDia && _abierto)
        {
            _enfocarDia = false;
            await EnfocarAsync(_refDia);
        }
        else if (_enfocarInput && !_abierto)
        {
            _enfocarInput = false;
            await EnfocarAsync(_refInput);
        }
    }

    private static async Task EnfocarAsync(ElementReference elemento)
    {
        try
        {
            await elemento.FocusAsync();
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException or InvalidOperationException or TaskCanceledException)
        {
            // Sin circuito o sin elemento (prerenderizado, desconexión): el foco no es crítico.
        }
    }

    private DateOnly Hoy() => DiaDeNegocio.Hoy(Servicios.GetService<TimeProvider>() ?? TimeProvider.System);

    /// <summary>0 = lunes … 6 = domingo: la semana empieza en lunes en es-ES y ca-ES.</summary>
    private static int DiaDeLaSemana(DateOnly fecha) => ((int)fecha.DayOfWeek + 6) % 7;

    private int HuecosIniciales => DiaDeLaSemana(_mesVisible);

    private IEnumerable<DateOnly> DiasDelMes =>
        Enumerable.Range(0, DateTime.DaysInMonth(_mesVisible.Year, _mesVisible.Month)).Select(_mesVisible.AddDays);

    private static IEnumerable<string> DiasSemana
    {
        get
        {
            var nombres = CultureInfo.CurrentCulture.DateTimeFormat.ShortestDayNames;
            return Enumerable.Range(1, 7).Select(i => nombres[i % 7]);
        }
    }

    private string TituloMes
    {
        get
        {
            var texto = _mesVisible.ToString("MMMM yyyy", CultureInfo.CurrentCulture);
            return texto.Length == 0 ? texto : char.ToUpper(texto[0], CultureInfo.CurrentCulture) + texto[1..];
        }
    }

    private Dictionary<string, object> AtributosDia(DateOnly fecha)
    {
        var elegido = Valor == fecha || (MascaraFecha.TryInterpretar(_texto, out var escrita) && escrita == fecha);
        var atributos = new Dictionary<string, object>
        {
            ["class"] = elegido ? "campo-fecha-dia campo-fecha-dia-elegido" : "campo-fecha-dia",
            ["tabindex"] = fecha == _diaEnfoco ? "0" : "-1",
            ["aria-label"] = fecha.ToString("D", CultureInfo.CurrentCulture),
            ["aria-pressed"] = elegido ? "true" : "false",
            ["data-fecha"] = MascaraFecha.AIso(fecha),
        };
        if (fecha == Hoy()) atributos["aria-current"] = "date";
        return atributos;
    }
}
