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
    private ElementReference _refFila;
    private ElementReference _refCalendario;

    /// <summary>Lo que el navegador tiene ahora en el input, según lo último que llegó de él o que se le impuso.</summary>
    private string _texto = string.Empty;

    /// <summary>Lo que Blazor renderizó por última vez en <c>value</c>; no se toca al teclear (ver CampoTexto).</summary>
    private string _valorMostrado = string.Empty;

    private int _generacion;
    private DateOnly? _ultimoNotificado;
    private bool _parametrosRecibidos;
    private bool _deshabilitadoAntes;
    /// <summary>
    /// null hasta el primer aviso: un campo recién creado dice su estado, así el formulario que reinicia su marca al
    /// abrirse no arrastra la de un campo anterior que ya no podría retirarla. El formulario que conserva la marca más
    /// allá de la vida del campo (tramos, filas plegables) lo declara con <see cref="NoValidoPrevio"/> y entonces el campo
    /// nuevo no avisa de su estado inicial.
    /// </summary>
    private bool? _noValidoNotificado;

    /// <summary>
    /// true cuando el campo nació con la marca de no válido que el padre ya tenía (ver <see cref="NoValidoPrevio"/>) y la
    /// persona aún no lo ha tocado: el texto a medias se perdió al destruirse el campo anterior, pero la marca no se retira
    /// hasta que la persona teclee, elija una fecha o vacíe el campo escribiendo.
    /// </summary>
    private bool _marcaHeredada;
    private string? _errorPropio;

    private bool _abierto;
    private DateOnly _mesVisible;
    private DateOnly _diaEnfoco;
    private bool _enfocarDia;
    private bool _enfocarInput;
    private bool _anclarPendiente;
    private bool _anclado;

    [Parameter] public string? Etiqueta { get; set; }
    [Parameter] public DateOnly? Valor { get; set; }
    [Parameter] public EventCallback<DateOnly?> ValorChanged { get; set; }
    [Parameter] public string? Placeholder { get; set; }
    [Parameter] public bool Deshabilitado { get; set; }
    [Parameter] public string? MensajeError { get; set; }

    /// <summary>Se dispara al perder el foco, después de <see cref="ValorChanged"/>, como en CampoTexto.</summary>
    [Parameter] public EventCallback OnBlur { get; set; }

    /// <summary>
    /// Opcional: avisa cuando el campo pasa a tener (true) o deja de tener (false) un texto que no es una fecha entera: a
    /// medias («15/03», «10/1/2030»), imposible («31/02/2026») o sin cifras («mañana»). Al salir del campo el valor
    /// notificado es null, como el «» de un campo de fecha nativo; antes de salir, el padre aún tiene la fecha anterior.
    /// Por eso todo formulario que guarde lo consume y bloquea el envío mientras sea true. Solo se notifica el cambio, no
    /// cada pulsación.
    /// </summary>
    [Parameter] public EventCallback<bool> NoValidoChanged { get; set; }

    /// <summary>
    /// Opcional, para el formulario que conserva la marca de <see cref="NoValidoChanged"/> más allá de la vida del campo
    /// (una lista de tramos o de filas cuyos campos se destruyen al cambiar de tramo o plegar la fila): lo que el padre
    /// tiene marcado ahora para este campo. Con un valor (true o false) el campo recién creado no avisa de su estado
    /// inicial: no puede retirar una marca que no observó él. Con true y sin fecha, nace con el aviso «no es una fecha» a la
    /// vista y la marca se mantiene hasta que la persona teclee o elija una fecha; salir del campo sin tocarlo no la quita.
    /// Con null (por defecto) el campo avisa su estado al crearse, como siempre: es lo que necesita el formulario que
    /// reinicia su marca al abrirse.
    /// </summary>
    [Parameter] public bool? NoValidoPrevio { get; set; }

    /// <summary>Opcional: primer y último día elegibles en el calendario (lo que hacía <c>min</c>/<c>max</c> en el campo nativo). Lo escrito a mano no se recorta: lo valida el formulario.</summary>
    [Parameter] public DateOnly? Minimo { get; set; }
    [Parameter] public DateOnly? Maximo { get; set; }

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
    ///
    /// <para>
    /// Un reinicio del padre al mismo valor (null tras un texto no válido) no se distingue de un re-render cualquiera. Se
    /// cubren los dos casos que lo necesitan: al deshabilitarse, el campo vuelve al valor del padre (lo no válido de un
    /// campo apagado no se puede corregir); y el padre que limpia un formulario entero recrea el campo con <c>@key</c>
    /// (Auditoría, «Quitar el filtro»).
    /// </para>
    /// </summary>
    protected override async Task OnParametersSetAsync()
    {
        var seDeshabilita = Deshabilitado && !_deshabilitadoAntes;
        _deshabilitadoAntes = Deshabilitado;
        if (_parametrosRecibidos && Valor == _ultimoNotificado && !seDeshabilita) return;

        var primera = !_parametrosRecibidos;
        _parametrosRecibidos = true;
        _ultimoNotificado = Valor;
        _errorPropio = null;
        _marcaHeredada = false;
        if (seDeshabilita) _abierto = false;
        MostrarTexto(MascaraFecha.Formatear(Valor));
        if (primera && NoValidoPrevio is { } previo)
        {
            // El padre ya sabe cómo está esta fecha: el campo nuevo no lo contradice. Un campo apagado o con fecha no
            // hereda la marca (nada que corregir): avisa false y la retira, como antes.
            _marcaHeredada = previo && Valor is null && !Deshabilitado;
            _noValidoNotificado = _marcaHeredada ? true : previo ? null : false;
            if (_marcaHeredada) _errorPropio = Comunes["CampoFechaInvalida"];
        }

        await NotificarEstadoAsync();
    }

    /// <summary>
    /// Hay texto y no es todavía (o no es) una fecha entera dd/mm/aaaa. Cuenta lo que llegó del navegador, no la máscara:
    /// «mañana» pegado sobre una fecha se enmascara en «», pero no es un vaciado voluntario (ver MascaraFecha.EsNoValido).
    /// </summary>
    private bool TextoNoValido => _marcaHeredada || MascaraFecha.EsNoValido(_texto);

    private async Task NotificarEstadoAsync()
    {
        var noValido = TextoNoValido;
        if (noValido == _noValidoNotificado) return;
        _noValidoNotificado = noValido;
        await NoValidoChanged.InvokeAsync(noValido);
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
        _marcaHeredada = false;
        _texto = crudo;
        var mascara = MascaraFecha.Aplicar(crudo);
        // Con el script del navegador activo ya llega enmascarado y no se reescribe nada; sin él, se impone aquí. Un texto
        // sin cifras («mañana») no se enmascara en «»: se queda a la vista, como en el navegador, y cuenta como no válido.
        if (mascara != crudo && mascara.Length > 0) MostrarTexto(mascara);

        _errorPropio = null;

        // Solo el campo vacío de verdad (Supr o Retroceso hasta dejarlo en blanco) es «sin fecha».
        if (crudo.Length == 0)
            await NotificarAsync(null);
        else if (MascaraFecha.EsCompleta(mascara, out var fecha))
            await NotificarAsync(fecha);
        await NotificarEstadoAsync();
    }

    private async Task AlSalirAsync()
    {
        if (Deshabilitado) return;
        await ConfirmarTextoAsync();
        await OnBlur.InvokeAsync();
    }

    private async Task ConfirmarTextoAsync()
    {
        // Entrar y salir sin tocar un campo que nació marcado no es corregirlo ni vaciarlo: la marca sigue y el aviso también.
        if (_marcaHeredada) return;

        if (_texto.Length == 0)
        {
            _errorPropio = null;
            await NotificarAsync(null);
        }
        else if (MascaraFecha.TryInterpretar(_texto, out var fecha))
        {
            _errorPropio = null;
            MostrarTexto(MascaraFecha.Formatear(fecha)); // una ISO entera sin el script queda en dd/mm/aaaa
            await NotificarAsync(fecha);
        }
        else
        {
            // Como el campo nativo: lo que no es fecha llega al padre como «sin fecha» (nunca el texto, que los formularios
            // interpretarían cada uno a su manera), el texto se queda a la vista para corregirlo y el aviso lo da el propio
            // campo; NoValidoChanged sigue en true, y el formulario que guarda bloquea el envío con ella. «10/1/2030» no se
            // completa a 10/01/2030: puede ser una corrección a medias. Solo si cambia: salir otra vez del campo con el
            // mismo texto no vuelve a notificar.
            _errorPropio = Comunes["CampoFechaInvalida"];
            await NotificarAsync(null);
        }

        await NotificarEstadoAsync();
    }

    /// <summary>Solo notifica un cambio de valor: un mismo null tras otro no recarga la lista ni borra el error del padre.</summary>
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
        _anclarPendiente = true;
    }

    /// <param name="devolverFoco">false al salir con Tab: el navegador ya llevó el foco al control siguiente.</param>
    private void Cerrar(bool devolverFoco = true)
    {
        _abierto = false;
        _enfocarInput = devolverFoco;
    }

    private Task CerrarYVolverAsync()
    {
        Cerrar();
        return Task.CompletedTask;
    }

    private async Task ElegirAsync(DateOnly fecha)
    {
        if (!EsElegible(fecha)) return;
        MostrarTexto(MascaraFecha.Formatear(fecha));
        _marcaHeredada = false;
        _errorPropio = null;
        Cerrar();
        await NotificarAsync(fecha);
        await NotificarEstadoAsync();
    }

    private bool EsElegible(DateOnly fecha) => (Minimo is not { } min || fecha >= min) && (Maximo is not { } max || fecha <= max);

    private void CambiarMes(int meses)
    {
        if (Desplazar(() => _mesVisible.AddMonths(meses)) is not { } mes) return;
        _mesVisible = mes;
        var dia = Math.Min(_diaEnfoco.Day, DateTime.DaysInMonth(_mesVisible.Year, _mesVisible.Month));
        _diaEnfoco = new DateOnly(_mesVisible.Year, _mesVisible.Month, dia);
    }

    /// <summary>Un paso del calendario que se sale de 0001-9999 no mueve nada, en vez de tirar el circuito.</summary>
    private static DateOnly? Desplazar(Func<DateOnly> paso)
    {
        try
        {
            return paso();
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private Task AlPulsarEnCalendarioAsync(KeyboardEventArgs e)
    {
        if (e.Key == "Escape") Cerrar();
        return Task.CompletedTask;
    }

    /// <summary>Mayúsculas+Tab desde el primer control del calendario lo deja: se cierra, como al salir con Tab de la rejilla.</summary>
    private Task AlPulsarEnMesAnteriorAsync(KeyboardEventArgs e)
    {
        if (e.Key == "Tab" && e.ShiftKey) Cerrar(devolverFoco: false);
        return Task.CompletedTask;
    }

    private Task AlPulsarEnRejillaAsync(KeyboardEventArgs e)
    {
        if (e.Key == "Tab")
        {
            // La rejilla es el último control del calendario: Tab sale de él (Mayúsculas+Tab vuelve al mes siguiente).
            if (!e.ShiftKey) Cerrar(devolverFoco: false);
            return Task.CompletedTask;
        }

        var actual = _diaEnfoco;
        DateOnly? destino = e.Key switch
        {
            "ArrowLeft" => Desplazar(() => actual.AddDays(-1)),
            "ArrowRight" => Desplazar(() => actual.AddDays(1)),
            "ArrowUp" => Desplazar(() => actual.AddDays(-7)),
            "ArrowDown" => Desplazar(() => actual.AddDays(7)),
            "Home" => Desplazar(() => actual.AddDays(-DiaDeLaSemana(actual))),
            "End" => Desplazar(() => actual.AddDays(6 - DiaDeLaSemana(actual))),
            "PageUp" => Desplazar(() => actual.AddMonths(-1)),
            "PageDown" => Desplazar(() => actual.AddMonths(1)),
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
        if (_abierto && _anclarPendiente)
        {
            _anclarPendiente = false;
            _anclado = await LlamarScriptAsync("talvegCampoFecha.anclar", _refCalendario, _refFila, IdCalendario);
        }
        else if (!_abierto && _anclado)
        {
            // Por id: el calendario ya salió del DOM y su ElementReference no se resolvería.
            _anclado = false;
            await LlamarScriptAsync("talvegCampoFecha.soltar", IdCalendario);
        }

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

    /// <summary>
    /// El calendario pasa a la capa superior del navegador (Popover API, como CampoSelectAvanzado): así no queda bajo la
    /// cabecera ni el lateral ni lo recorta el overflow de un Drawer o un Modal. Sin el script (bUnit, script bloqueado) cae
    /// a la posición absoluta del CSS y sigue siendo usable.
    /// </summary>
    private async Task<bool> LlamarScriptAsync(string funcion, params object[] argumentos)
    {
        try
        {
            await JS.InvokeVoidAsync(funcion, argumentos);
            return true;
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException or InvalidOperationException or TaskCanceledException)
        {
            return false;
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

    /// <summary>Las semanas del mes visible, de lunes a domingo; null es una celda vacía antes del día 1 o tras el último.</summary>
    private IEnumerable<DateOnly?[]> Semanas
    {
        get
        {
            var huecos = DiaDeLaSemana(_mesVisible);
            var celdas = Enumerable.Repeat<DateOnly?>(null, huecos)
                .Concat(Enumerable.Range(0, DateTime.DaysInMonth(_mesVisible.Year, _mesVisible.Month)).Select(d => (DateOnly?)_mesVisible.AddDays(d)))
                .ToList();
            while (celdas.Count % 7 != 0) celdas.Add(null);
            return celdas.Chunk(7);
        }
    }

    /// <summary>El nombre corto que se ve en la cabecera y el entero que lee el lector de pantalla.</summary>
    private static IEnumerable<(string Corto, string Largo)> DiasSemana
    {
        get
        {
            var formato = CultureInfo.CurrentCulture.DateTimeFormat;
            return Enumerable.Range(1, 7).Select(i => (formato.ShortestDayNames[i % 7], formato.DayNames[i % 7]));
        }
    }

    private string IdTitulo => $"{_id}-titulo";
    private string IdInstrucciones => $"{_id}-instrucciones";

    private bool EsElegido(DateOnly fecha) =>
        Valor == fecha || (MascaraFecha.TryInterpretar(_texto, out var escrita) && escrita == fecha);

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
        // El estado «elegido» lo lleva la celda (aria-selected), no el botón: aria-pressed además lo anunciaría dos veces.
        var atributos = new Dictionary<string, object>
        {
            ["class"] = EsElegido(fecha) ? "campo-fecha-dia campo-fecha-dia-elegido" : "campo-fecha-dia",
            ["tabindex"] = fecha == _diaEnfoco ? "0" : "-1",
            ["aria-label"] = fecha.ToString("D", CultureInfo.CurrentCulture),
            ["data-fecha"] = MascaraFecha.AIso(fecha),
        };
        if (fecha == Hoy()) atributos["aria-current"] = "date";
        // aria-disabled y no disabled: el día fuera de rango sigue enfocable para moverse por la rejilla con las flechas.
        if (!EsElegible(fecha)) atributos["aria-disabled"] = "true";
        return atributos;
    }
}
